using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Npgsql;
using PolicyService;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(config.GetConnectionString("Policies")
    ?? throw new InvalidOperationException("Set ConnectionStrings:Policies.")));
builder.Services.AddSingleton<PolicyStore>();
// Messaging:BrokerUri (e.g. activemq:tcp://broker:61616) turns on the PolicyIssued events; without it they're skipped.
if (string.IsNullOrEmpty(config["Messaging:BrokerUri"]))
{
    builder.Services.AddSingleton<IPolicyEvents, NoPolicyEvents>();
}
else
{
    builder.Services.AddSingleton<IPolicyEvents, ActiveMqPolicyEvents>();
}
builder.Services.AddHttpClient<RatingClient>(http =>
{
    http.BaseAddress = new Uri(config["RatingEngine:Url"] ?? throw new InvalidOperationException("Set RatingEngine:Url."));
    // The rating engine is someone else's system: don't let a slow one hang every quote.
    http.Timeout = TimeSpan.FromSeconds(config.GetValue("RatingEngine:TimeoutSeconds", 3.0));
});

// Layer 1: an OAuth2 access token from the identity provider (Keycloak), for the calling application.
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.Authority = config["Auth:Authority"];
    options.RequireHttpsMetadata = config.GetValue("Auth:RequireHttps", true);
    options.TokenValidationParameters.ValidateAudience = false;
});
var allowedClients = (config["Auth:AllowedClients"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
builder.Services.AddAuthorization(options => options.AddPolicy("PolicyClients", policy =>
    policy.RequireAuthenticatedUser().RequireClaim("azp", allowedClients)));

var app = builder.Build();
await app.Services.GetRequiredService<PolicyStore>().EnsureCreatedAsync();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapPost("/soap/policy", async (HttpRequest request, PolicyStore store, RatingClient rating, IPolicyEvents events, TimeProvider clock,
    CancellationToken ct) =>
{
    XDocument envelope;
    try
    {
        envelope = await XDocument.LoadAsync(request.Body, LoadOptions.None, ct);
    }
    catch (XmlException)
    {
        return Soap.Xml(Soap.Fault(client: true, "The request isn't well-formed XML.", "MalformedXml"), 400);
    }

    // Layer 2: the legacy WS-Security UsernameToken inside the envelope, for the agent making the call.
    var token = envelope.Root?.Element(Soap.Env + "Header")?.Element(Soap.Wsse + "Security")?.Element(Soap.Wsse + "UsernameToken");
    if ((string?)token?.Element(Soap.Wsse + "Username") != config["Legacy:Username"]
        || (string?)token?.Element(Soap.Wsse + "Password") != config["Legacy:Password"])
    {
        return Soap.Xml(Soap.Fault(client: true, "WS-Security UsernameToken missing or wrong.", "AuthenticationFailed"), 500);
    }

    var operation = Soap.Operation(envelope);
    return operation?.Name.LocalName switch
    {
        "GetQuote" when operation.Name.Namespace == Soap.Policy => await GetQuote(operation),
        "IssuePolicy" when operation.Name.Namespace == Soap.Policy => await IssuePolicy(operation),
        "GetPolicy" when operation.Name.Namespace == Soap.Policy => await GetPolicy(operation),
        _ => Soap.Xml(Soap.Fault(client: true, "Unknown operation.", "UnknownOperation"), 500),
    };

    async Task<IResult> GetQuote(XElement op)
    {
        var a = op.Element(Soap.Policy + "applicant");
        if (a is null
            || (string?)a.Element(Soap.Policy + "name") is not { Length: > 0 } name
            || !int.TryParse((string?)a.Element(Soap.Policy + "age"), out var age) || age is < 16 or > 100
            || !decimal.TryParse((string?)a.Element(Soap.Policy + "vehicleValue"), NumberStyles.Number, CultureInfo.InvariantCulture, out var value) || value <= 0
            || (string?)a.Element(Soap.Policy + "state") is not { Length: 2 } state)
        {
            return Soap.Xml(Soap.Fault(client: true, "applicant needs a name, an age from 16 to 100, a vehicleValue and a 2-letter state.", "InvalidRequest"), 500);
        }
        var applicant = new Applicant(name, age, value, state.ToUpperInvariant());

        Rating risk;
        try
        {
            risk = await rating.RateAsync(applicant, ct);
        }
        catch (RatingUnavailableException e)
        {
            return Soap.Xml(Soap.Fault(client: false, e.Message, "RatingUnavailable"), 500);
        }

        // A risk score of 80 or more is declined; otherwise the premium is the base rate, loaded by the risk.
        var declined = risk.RiskScore >= 80;
        var premium = declined ? 0m : Math.Round(risk.BaseRate * (1 + risk.RiskScore / 100m), 2, MidpointRounding.AwayFromZero);
        var quote = new Quote(Guid.NewGuid(), applicant, risk.RiskScore, premium, declined ? "declined" : "offered",
            clock.GetUtcNow().AddMinutes(config.GetValue("Quotes:ValidMinutes", 30)));
        await store.SaveQuoteAsync(quote, ct);

        return Soap.Xml(Soap.Envelope(new XElement(Soap.Policy + "GetQuoteResponse",
            new XElement(Soap.Policy + "quoteId", quote.Id),
            new XElement(Soap.Policy + "status", quote.Status),
            new XElement(Soap.Policy + "riskScore", quote.RiskScore),
            new XElement(Soap.Policy + "premium", quote.Premium.ToString("0.00", CultureInfo.InvariantCulture)),
            new XElement(Soap.Policy + "expiresAt", quote.ExpiresAt.UtcDateTime))));
    }

    async Task<IResult> IssuePolicy(XElement op)
    {
        if (!Guid.TryParse((string?)op.Element(Soap.Policy + "quoteId"), out var quoteId))
        {
            return Soap.Xml(Soap.Fault(client: true, "quoteId must be a quote's id.", "InvalidRequest"), 500);
        }
        var (outcome, policy) = await store.IssueAsync(quoteId, clock.GetUtcNow(), ct);
        if (outcome == PolicyStore.IssueOutcome.Issued)
        {
            // After the commit, so a message never announces a policy that was rolled back.
            await events.PolicyIssuedAsync(policy!, ct);
        }
        return outcome switch
        {
            PolicyStore.IssueOutcome.Issued => Soap.Xml(Soap.Envelope(new XElement(Soap.Policy + "IssuePolicyResponse",
                PolicyElement(policy!)))),
            PolicyStore.IssueOutcome.UnknownQuote => Soap.Xml(Soap.Fault(true, "No such quote.", "UnknownQuote"), 500),
            PolicyStore.IssueOutcome.Expired => Soap.Xml(Soap.Fault(true, "The quote has expired.", "QuoteExpired"), 500),
            _ => Soap.Xml(Soap.Fault(true, "Only an offered quote can be bound, and only once.", "QuoteNotOffered"), 500),
        };
    }

    async Task<IResult> GetPolicy(XElement op)
    {
        var policy = await store.GetPolicyAsync((string?)op.Element(Soap.Policy + "policyNumber") ?? "", ct);
        return policy is null
            ? Soap.Xml(Soap.Fault(true, "No such policy.", "UnknownPolicy"), 500)
            : Soap.Xml(Soap.Envelope(new XElement(Soap.Policy + "GetPolicyResponse", PolicyElement(policy))));
    }

    static XElement PolicyElement(Policy p) => new(Soap.Policy + "policy",
        new XElement(Soap.Policy + "policyNumber", p.PolicyNumber),
        new XElement(Soap.Policy + "quoteId", p.QuoteId),
        new XElement(Soap.Policy + "holderName", p.HolderName),
        new XElement(Soap.Policy + "premium", p.Premium.ToString("0.00", CultureInfo.InvariantCulture)),
        new XElement(Soap.Policy + "status", p.Status),
        new XElement(Soap.Policy + "issuedAt", p.IssuedAt.UtcDateTime));
}).RequireAuthorization("PolicyClients");

app.Run();

/// <summary>Exposed so the tests can host the service in-process.</summary>
public partial class Program;
