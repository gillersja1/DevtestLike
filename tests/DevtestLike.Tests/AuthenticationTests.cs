using System.Net;
using DevtestLike.Tests.Environment;

namespace DevtestLike.Tests;

/// <summary>
/// Authentication at both layers the service enforces: an OAuth2 access token from Keycloak on the HTTP
/// request (the calling application), and a WS-Security UsernameToken in the SOAP header (the agent).
/// </summary>
public class AuthenticationTests
{
    [SetUp]
    public void SetUp()
    {
        Enterprise.RatingEngine.Reset();
        Enterprise.RatingEngine.WithStandardRules();
    }

    private static Task<SoapReply> QuoteAs(PolicyCaller caller) => caller.GetQuoteAsync("Auth Test", age: 40);

    [Test]
    public async Task AnAllowedClientWithTheRightAgentGetsThrough()
    {
        var reply = await QuoteAs(new PolicyCaller(Enterprise.PolicyService));

        Assert.That(reply.Status, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(reply.IsFault, Is.False);
    }

    [Test]
    public async Task NoTokenIsRefusedBeforeTheSoapLayer()
    {
        var reply = await QuoteAs(new PolicyCaller(Enterprise.PolicyService) { ClientId = null });

        Assert.That(reply.Status, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task AForgedTokenIsRefused()
    {
        // Well-formed, but not signed by Keycloak.
        const string forged = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJhenAiOiJwb2xpY3ktY2xpZW50IiwiZXhwIjo0MTAyNDQ0ODAwfQ.c2lnbmF0dXJl";

        var reply = await QuoteAs(new PolicyCaller(Enterprise.PolicyService) { BearerOverride = forged });

        Assert.That(reply.Status, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task AValidTokenForAnotherApplicationIsForbidden()
    {
        // A real Keycloak client, but not one the policy service accepts.
        var reply = await QuoteAs(new PolicyCaller(Enterprise.PolicyService)
        {
            ClientId = "reporting-client",
            ClientSecret = "reporting-client-secret",
        });

        Assert.That(reply.Status, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test]
    public void TheIdentityProviderRejectsAWrongClientSecret()
    {
        // The right client, the wrong secret: Keycloak won't issue a token at all.
        Assert.ThrowsAsync<HttpRequestException>(() => PolicyCaller.TokenAsync("policy-client", "not-the-secret"));
    }

    [Test]
    public async Task MissingWsSecurityIsAClientFault()
    {
        var reply = await QuoteAs(new PolicyCaller(Enterprise.PolicyService) { Username = null });

        Assert.Multiple(() =>
        {
            Assert.That(reply.FaultCode, Is.EqualTo("AuthenticationFailed"));
            Assert.That(reply.Value("//faultcode"), Is.EqualTo("soap:Client"));
        });
    }

    [Test]
    public async Task AWrongAgentPasswordIsAClientFault()
    {
        var reply = await QuoteAs(new PolicyCaller(Enterprise.PolicyService) { Password = "guess" });

        Assert.That(reply.FaultCode, Is.EqualTo("AuthenticationFailed"));
    }

    [Test]
    public async Task ARefusedCallWritesNothing()
    {
        const string name = "Should Not Exist";

        await new PolicyCaller(Enterprise.PolicyService) { Password = "guess" }.GetQuoteAsync(name, 40);
        await new PolicyCaller(Enterprise.PolicyService) { ClientId = null }.GetQuoteAsync(name, 40);

        Assert.That(await Db.QuotesForApplicantAsync(name), Is.Zero);
        Assert.That(Enterprise.RatingEngine.Requests, Is.Empty, "the rating engine was never called");
    }
}
