using System.Text.Json;
using WireMock.Matchers;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace DevtestLike.Tests.Environment;

/// <summary>
/// The licence-check company, virtualized. Its rules match on the request's JSON (JMESPath expressions), like the transactions
/// of a DevTest service image: licence numbers starting SUS are suspended, EXP expired, anything else valid.
/// </summary>
public sealed class LicenseCheckService : IDisposable
{
    private readonly WireMockServer server = WireMockServer.Start();

    public string Url => server.Url! + "/";

    /// <summary>The licence numbers the rental service asked about, in order.</summary>
    public IReadOnlyList<string> Checked => server.LogEntries
        .Select(e => JsonDocument.Parse(e.RequestMessage?.Body ?? "{}").RootElement)
        .Select(body => body.TryGetProperty("licenseNumber", out var n) ? n.GetString() ?? "" : "").ToList();

    public LicenseCheckService WithStandardRules()
    {
        server.Reset();
        Answer("starts_with(licenseNumber, 'SUS')", "suspended", priority: 1);
        Answer("starts_with(licenseNumber, 'EXP')", "expired", priority: 1);
        Answer(null, "valid", priority: 10);
        return this;
    }

    /// <summary>A rule for requests where the JMESPath <paramref name="when"/> is true (null: any request). Lower priority wins.</summary>
    private void Answer(string? when, string status, int priority)
    {
        var request = Request.Create().WithPath("/v1/license-checks").UsingPost();
        if (when is not null) request = request.WithBody(new JmesPathMatcher(when));
        server.Given(request)
            .AtPriority(priority)
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                // Response templating: a fresh check id per call, not one canned value.
                .WithBody($$$"""{"checkId":"LC-{{Random Type="Guid"}}","status":"{{{status}}}"}""")
                .WithTransformer());
    }

    public LicenseCheckService Down(int status = 503)
    {
        server.Reset();
        server.Given(Request.Create().WithPath("/v1/license-checks").UsingPost()).RespondWith(Response.Create().WithStatusCode(status));
        return this;
    }

    public LicenseCheckService Slow(TimeSpan delay)
    {
        WithStandardRules();
        server.Given(Request.Create().WithPath("/v1/license-checks").UsingPost()).AtPriority(0)
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                .WithBody("""{"checkId":"LC-slow","status":"valid"}""").WithDelay(delay));
        return this;
    }

    public void Dispose() => server.Stop();
}

/// <summary>
/// The payment company, virtualized: cards whose token starts tok_declined are declined (402), others approved.
/// Voids succeed. Every request is kept, so tests can check what amount was held and what was released.
/// </summary>
public sealed class PaymentService : IDisposable
{
    private readonly WireMockServer server = WireMockServer.Start();

    public string Url => server.Url! + "/";

    public sealed record Call(string Path, JsonElement Body);

    public IReadOnlyList<Call> Calls => server.LogEntries
        .Select(e => new Call(e.RequestMessage?.Path ?? "", JsonDocument.Parse(string.IsNullOrEmpty(e.RequestMessage?.Body) ? "{}" : e.RequestMessage.Body).RootElement))
        .ToList();

    public IEnumerable<Call> Authorizations(string reference) => Calls.Where(c =>
        c.Path == "/v1/authorizations" && c.Body.TryGetProperty("reference", out var r) && r.GetString() == reference);

    public PaymentService WithStandardRules()
    {
        server.Reset();
        server.Given(Request.Create().WithPath("/v1/authorizations").UsingPost()
                .WithBody(new JmesPathMatcher("starts_with(cardToken, 'tok_declined')")))
            .AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(402).WithHeader("Content-Type", "application/json")
                .WithBody("""{"status":"declined","reason":"insufficient_funds"}"""));
        server.Given(Request.Create().WithPath("/v1/authorizations").UsingPost())
            .AtPriority(10)
            .RespondWith(Response.Create().WithStatusCode(201).WithHeader("Content-Type", "application/json")
                // Response templating: a fresh id for every hold, not one canned value.
                .WithBody("""{"status":"approved","authorizationId":"AUTH-{{Random Type="Guid"}}"}""")
                .WithTransformer());
        server.Given(Request.Create().WithPath(new WildcardMatcher("/v1/authorizations/*/void")).UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                .WithBody("""{"status":"voided"}"""));
        return this;
    }

    /// <summary>Holds work, but voids fail: the payment company went down after the booking.</summary>
    public PaymentService VoidsDown()
    {
        WithStandardRules();
        server.Given(Request.Create().WithPath(new WildcardMatcher("/v1/authorizations/*/void")).UsingPost()).AtPriority(0)
            .RespondWith(Response.Create().WithStatusCode(503));
        return this;
    }

    public PaymentService Down(int status = 503)
    {
        server.Reset();
        server.Given(Request.Create().WithPath(new WildcardMatcher("/v1/*")).UsingPost()).RespondWith(Response.Create().WithStatusCode(status));
        return this;
    }

    public void Dispose() => server.Stop();
}
