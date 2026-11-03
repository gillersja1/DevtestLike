using DevtestLike.Tests.Environment;
using Microsoft.Extensions.Time.Testing;

namespace DevtestLike.Tests;

/// <summary>
/// Service virtualization: the rating engine is a virtual service, so the tests can make it do what the real
/// one rarely does on demand (fail, stall, return garbage, restart) and check the policy service copes,
/// and can inspect exactly what the policy service sent it.
/// </summary>
public class VirtualServiceTests
{
    private PolicyCaller agent = null!;

    [SetUp]
    public void SetUp()
    {
        Enterprise.RatingEngine.Reset();
        agent = new PolicyCaller(Enterprise.PolicyService);
    }

    [Test]
    public async Task ThePolicyServiceSendsTheApplicantsDetailsToTheRatingEngine()
    {
        Enterprise.RatingEngine.WithStandardRules();

        await agent.GetQuoteAsync("Wire Check", age: 33, vehicleValue: 18500.50m, state: "tx");

        var sent = System.Xml.Linq.XDocument.Parse(Enterprise.RatingEngine.Requests.Single());
        System.Xml.Linq.XNamespace r = "http://devtestlike.example/rating";
        Assert.Multiple(() =>
        {
            Assert.That(sent.Descendants(r + "age").Single().Value, Is.EqualTo("33"));
            Assert.That(sent.Descendants(r + "vehicleValue").Single().Value, Is.EqualTo("18500.50"));
            Assert.That(sent.Descendants(r + "state").Single().Value, Is.EqualTo("TX"), "normalized to upper case");
            Assert.That(sent.Descendants(r + "name"), Is.Empty, "the applicant's name stays with us");
        });
    }

    [Test]
    public async Task ARatingEngineOutageIsAServerFaultAndSavesNothing()
    {
        Enterprise.RatingEngine.Down(503);
        const string name = "Outage Olive";

        var reply = await agent.GetQuoteAsync(name, age: 40);

        Assert.Multiple(async () =>
        {
            Assert.That(reply.FaultCode, Is.EqualTo("RatingUnavailable"));
            Assert.That(reply.Value("//faultcode"), Is.EqualTo("soap:Server"));
            Assert.That(await Db.QuotesForApplicantAsync(name), Is.Zero);
        });
    }

    [Test]
    public async Task ASlowRatingEngineTimesOutInsteadOfHanging()
    {
        // 1 s timeout, 3 s virtual delay: the quote fails fast rather than waiting.
        Enterprise.RatingEngine.Slow(TimeSpan.FromSeconds(3));
        await using var service = Enterprise.Host(new FakeTimeProvider(DateTimeOffset.UtcNow), ratingTimeoutSeconds: 1);
        var started = DateTime.UtcNow;

        var reply = await new PolicyCaller(service).GetQuoteAsync("Slow Sam", age: 40);

        Assert.Multiple(() =>
        {
            Assert.That(reply.FaultCode, Is.EqualTo("RatingUnavailable"));
            Assert.That(DateTime.UtcNow - started, Is.LessThan(TimeSpan.FromSeconds(2.5)));
        });
    }

    [Test]
    public async Task GarbageFromTheRatingEngineIsAServerFault()
    {
        Enterprise.RatingEngine.Garbage();

        var reply = await agent.GetQuoteAsync("Garbage Gus", age: 40);

        Assert.That(reply.FaultCode, Is.EqualTo("RatingUnavailable"));
    }

    [Test]
    public async Task AfterTheRatingEngineRestartsQuotesWorkAgain()
    {
        // A stateful virtual service: the first call hits it mid-restart, the second finds it back.
        Enterprise.RatingEngine.DownOnceThenUp();

        var first = await agent.GetQuoteAsync("Restart Rita", age: 40);
        var second = await agent.GetQuoteAsync("Restart Rita", age: 40);

        Assert.Multiple(() =>
        {
            Assert.That(first.FaultCode, Is.EqualTo("RatingUnavailable"));
            Assert.That(second.IsFault, Is.False);
            Assert.That(second.Value("//p:status"), Is.EqualTo("offered"));
        });
    }
}
