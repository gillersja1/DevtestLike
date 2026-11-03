using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using System.Xml.XPath;
using Microsoft.AspNetCore.Mvc.Testing;

namespace DevtestLike.Tests.Environment;

/// <summary>A SOAP reply: the status, the XML, and XPath over it with the service's prefixes.</summary>
public sealed record SoapReply(HttpStatusCode Status, XDocument Xml)
{
    private static readonly XmlNamespaceManager Prefixes = CreatePrefixes();

    private static XmlNamespaceManager CreatePrefixes()
    {
        var ns = new XmlNamespaceManager(new NameTable());
        ns.AddNamespace("soap", "http://schemas.xmlsoap.org/soap/envelope/");
        ns.AddNamespace("p", "http://devtestlike.example/policy");
        return ns;
    }

    /// <summary>The text at <paramref name="xpath"/> (e.g. "//p:quoteId"), like a DevTest XPath filter.</summary>
    public string Value(string xpath) =>
        Xml.XPathSelectElement(xpath, Prefixes)?.Value ?? throw new AssertionException($"Nothing at {xpath} in:\n{Xml}");

    public bool Has(string xpath) => Xml.XPathSelectElement(xpath, Prefixes) is not null;

    public bool IsFault => Has("//soap:Fault");

    public string FaultCode => Value("//soap:Fault/detail/p:code");

    /// <summary>Checks the body against the contract (contracts/policy.xsd); a mismatch fails the test.</summary>
    public SoapReply ConformsToContract()
    {
        var body = Xml.Root!.Element(XName.Get("Body", "http://schemas.xmlsoap.org/soap/envelope/"))!.Elements().Single();
        var errors = new List<string>();
        new XDocument(body).Validate(Contract.Schemas, (_, e) => errors.Add(e.Message));
        Assert.That(errors, Is.Empty, $"Reply breaks the contract:\n{body}");
        return this;
    }
}

public static class Contract
{
    public static readonly XmlSchemaSet Schemas = Load();

    private static XmlSchemaSet Load()
    {
        var set = new XmlSchemaSet();
        set.Add("http://devtestlike.example/policy", Path.Combine(AppContext.BaseDirectory, "Contracts", "policy.xsd"));
        set.Compile();
        return set;
    }
}

/// <summary>
/// Calls the policy service the way a real agency portal would: an OAuth2 token from Keycloak (client
/// credentials) on the HTTP request, and the agent's WS-Security UsernameToken inside the SOAP envelope.
/// </summary>
public sealed class PolicyCaller(WebApplicationFactory<Program> service)
{
    private static readonly Dictionary<string, string> Tokens = [];

    public string? ClientId { get; init; } = "policy-client";
    public string? ClientSecret { get; init; } = "policy-client-secret";
    public string? BearerOverride { get; init; }
    public string? Username { get; init; } = Enterprise.AgentUser;
    public string? Password { get; init; } = Enterprise.AgentPassword;

    public Task<SoapReply> GetQuoteAsync(string name, int age, decimal vehicleValue = 25000m, string state = "OH") =>
        SendAsync(new XElement(P + "GetQuote",
            new XElement(P + "applicant",
                new XElement(P + "name", name),
                new XElement(P + "age", age),
                new XElement(P + "vehicleValue", vehicleValue.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new XElement(P + "state", state))));

    public Task<SoapReply> IssuePolicyAsync(string quoteId) =>
        SendAsync(new XElement(P + "IssuePolicy", new XElement(P + "quoteId", quoteId)));

    public Task<SoapReply> GetPolicyAsync(string policyNumber) =>
        SendAsync(new XElement(P + "GetPolicy", new XElement(P + "policyNumber", policyNumber)));

    private static readonly XNamespace P = "http://devtestlike.example/policy";
    private static readonly XNamespace Env = "http://schemas.xmlsoap.org/soap/envelope/";
    private static readonly XNamespace Wsse = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd";

    public async Task<SoapReply> SendAsync(XElement operation) => await SendRawAsync(Envelope(operation).ToString());

    public async Task<SoapReply> SendRawAsync(string xml)
    {
        using var http = service.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/soap/policy")
        {
            Content = new StringContent(xml, Encoding.UTF8, "text/xml"),
        };
        request.Headers.Add("SOAPAction", "\"http://devtestlike.example/policy\"");
        var bearer = BearerOverride ?? (ClientId is null ? null : await TokenAsync(ClientId, ClientSecret!));
        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }
        using var response = await http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        return new SoapReply(response.StatusCode, body.Length == 0 ? new XDocument() : XDocument.Parse(body));
    }

    private XDocument Envelope(XElement operation) => new(new XElement(Env + "Envelope",
        new XAttribute(XNamespace.Xmlns + "soap", Env),
        new XElement(Env + "Header",
            Username is null ? null : new XElement(Wsse + "Security",
                new XElement(Wsse + "UsernameToken",
                    new XElement(Wsse + "Username", Username),
                    new XElement(Wsse + "Password", Password)))),
        new XElement(Env + "Body", operation)));

    /// <summary>A client-credentials token from Keycloak, cached per client and secret for the run.</summary>
    public static async Task<string> TokenAsync(string clientId, string secret)
    {
        var key = $"{clientId}|{secret}";
        lock (Tokens)
        {
            if (Tokens.TryGetValue(key, out var cached)) return cached;
        }
        using var http = new HttpClient();
        var reply = await http.PostAsync(Enterprise.TokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = clientId,
            ["client_secret"] = secret,
        }));
        reply.EnsureSuccessStatusCode();
        var token = (await reply.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString()!;
        lock (Tokens)
        {
            Tokens[key] = token;
        }
        return token;
    }
}
