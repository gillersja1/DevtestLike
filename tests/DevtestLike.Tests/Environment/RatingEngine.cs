using WireMock.Matchers;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace DevtestLike.Tests.Environment;

/// <summary>
/// The virtual rating engine: a stand-in for the third-party SOAP service the policy service calls. In DevTest
/// terms it's a virtual service whose response rules (the "service image") match on the request's XML, plus the
/// failure modes the real one is too precious to try: outages, slowness, garbage.
/// </summary>
public sealed class RatingEngine : IDisposable
{
    private readonly WireMockServer server;

    private RatingEngine(WireMockServer server) => this.server = server;

    public static RatingEngine Start() => new(WireMockServer.Start());

    public string Url => server.Url! + "/";

    /// <summary>Every request the policy service sent, for checking what went over the wire.</summary>
    public IReadOnlyList<string> Requests => server.LogEntries.Select(e => e.RequestMessage.Body ?? "").ToList();

    public void Reset() => server.Reset();

    /// <summary>
    /// The standard rules, matched on the applicant's age in the request (XPath), like a DevTest VSI with several
    /// transactions: young drivers and older drivers score higher, anyone 75 and over scores high enough to decline.
    /// </summary>
    public RatingEngine WithStandardRules()
    {
        Rule("number(//*[local-name()='age']) < 25", riskScore: 45, baseRate: 520.00m, priority: 1);
        Rule("number(//*[local-name()='age']) >= 75", riskScore: 85, baseRate: 610.00m, priority: 2);
        Rule("number(//*[local-name()='age']) >= 65", riskScore: 30, baseRate: 480.00m, priority: 3);
        Rule("number(//*[local-name()='age']) >= 25", riskScore: 20, baseRate: 500.00m, priority: 4);
        return this;
    }

    /// <summary>A rule matching requests where <paramref name="xpath"/> is true. Lower priority numbers win.</summary>
    public RatingEngine Rule(string xpath, int riskScore, decimal baseRate, int priority = 10)
    {
        server.Given(RateRisk().WithBody(new XPathMatcher(xpath)))
            .AtPriority(priority)
            .RespondWith(Rated(riskScore, baseRate));
        return this;
    }

    public RatingEngine Down(int status = 503)
    {
        server.Given(RateRisk()).RespondWith(Response.Create().WithStatusCode(status).WithBody("Service Unavailable"));
        return this;
    }

    public RatingEngine Slow(TimeSpan delay)
    {
        server.Given(RateRisk()).RespondWith(Rated(20, 500m).WithDelay(delay));
        return this;
    }

    public RatingEngine Garbage()
    {
        server.Given(RateRisk()).RespondWith(Response.Create().WithStatusCode(200)
            .WithHeader("Content-Type", "text/xml").WithBody("<html><body>Maintenance page</body></html>"));
        return this;
    }

    /// <summary>
    /// A stateful virtual service: down for the first call, back up after, as when a dependency restarts. DevTest
    /// models this with conversational (stateful) transactions; WireMock calls them scenarios.
    /// </summary>
    public RatingEngine DownOnceThenUp()
    {
        server.Given(RateRisk()).InScenario("restart").WillSetStateTo("up")
            .RespondWith(Response.Create().WithStatusCode(503));
        server.Given(RateRisk()).InScenario("restart").WhenStateIs("up")
            .RespondWith(Rated(20, 500m));
        return this;
    }

    private static IRequestBuilder RateRisk() =>
        Request.Create().WithPath("/rating").UsingPost().WithHeader("SOAPAction", "*RateRisk*", MatchBehaviour.AcceptOnMatch);

    private static IResponseBuilder Rated(int riskScore, decimal baseRate) => Response.Create()
        .WithStatusCode(200)
        .WithHeader("Content-Type", "text/xml; charset=utf-8")
        .WithBody($"""
            <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
              <soap:Body>
                <r:RateRiskResponse xmlns:r="http://devtestlike.example/rating">
                  <r:riskScore>{riskScore}</r:riskScore>
                  <r:baseRate>{baseRate.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}</r:baseRate>
                </r:RateRiskResponse>
              </soap:Body>
            </soap:Envelope>
            """);

    public void Dispose() => server.Stop();
}
