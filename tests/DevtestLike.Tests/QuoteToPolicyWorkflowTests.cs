using DevtestLike.Tests.Environment;
using Microsoft.Extensions.Time.Testing;

namespace DevtestLike.Tests;

/// <summary>
/// XML orchestration across systems: a multi-step workflow where each step's request is built from values
/// pulled (by XPath) out of the previous reply, every reply is checked against the XSD contract, and the
/// database is checked after each step. In DevTest this is one test case with several steps, filters and
/// assertions.
/// </summary>
public class QuoteToPolicyWorkflowTests
{
    private PolicyCaller agent = null!;

    [SetUp]
    public void SetUp()
    {
        Enterprise.RatingEngine.Reset();
        Enterprise.RatingEngine.WithStandardRules();
        agent = new PolicyCaller(Enterprise.PolicyService);
    }

    private static string Unique(string name) => $"{name} {Guid.NewGuid():N}"[..20];

    [Test]
    public async Task QuoteThenBindThenReadBack()
    {
        var holder = Unique("Ada Lovelace");

        // Step 1: quote. Risk 20 on a 500.00 base rate: 500.00 x 1.20 = 600.00.
        var quote = (await agent.GetQuoteAsync(holder, age: 36)).ConformsToContract();
        var quoteId = quote.Value("//p:quoteId");
        Assert.Multiple(() =>
        {
            Assert.That(quote.Value("//p:status"), Is.EqualTo("offered"));
            Assert.That(quote.Value("//p:riskScore"), Is.EqualTo("20"));
            Assert.That(quote.Value("//p:premium"), Is.EqualTo("600.00"));
        });
        var row = await Db.QuoteAsync(quoteId);
        Assert.That(row, Is.EqualTo(new Db.QuoteRow("offered", 600.00m, 20, holder)));

        // Step 2: bind the quote, using the id step 1 returned.
        var issued = (await agent.IssuePolicyAsync(quoteId)).ConformsToContract();
        var policyNumber = issued.Value("//p:policyNumber");
        Assert.Multiple(() =>
        {
            Assert.That(policyNumber, Does.Match(@"^POL-\d{6,}$"));
            Assert.That(issued.Value("//p:quoteId"), Is.EqualTo(quoteId));
            Assert.That(issued.Value("//p:premium"), Is.EqualTo("600.00"), "the policy carries the quoted premium");
            Assert.That(issued.Value("//p:status"), Is.EqualTo("active"));
        });

        // The database agrees, across both tables.
        Assert.That((await Db.QuoteAsync(quoteId))!.Status, Is.EqualTo("bound"));
        Assert.That(await Db.PolicyAsync(policyNumber), Is.EqualTo(new Db.PolicyRow(quoteId, holder, 600.00m, "active")));

        // Step 3: read it back by the number step 2 returned.
        var read = (await agent.GetPolicyAsync(policyNumber)).ConformsToContract();
        Assert.That(read.Value("//p:holderName"), Is.EqualTo(holder));
    }

    [Test]
    public async Task AQuoteCanOnlyBeBoundOnce()
    {
        var quoteId = (await agent.GetQuoteAsync(Unique("Grace Hopper"), age: 40)).Value("//p:quoteId");
        await agent.IssuePolicyAsync(quoteId);

        var again = await agent.IssuePolicyAsync(quoteId);

        Assert.Multiple(async () =>
        {
            Assert.That(again.IsFault);
            Assert.That(again.FaultCode, Is.EqualTo("QuoteNotOffered"));
            Assert.That(await Db.PoliciesForQuoteAsync(quoteId), Is.EqualTo(1), "still exactly one policy");
        });
    }

    [Test]
    public async Task ADeclinedQuoteCantBeBound()
    {
        var quote = (await agent.GetQuoteAsync(Unique("Old Timer"), age: 80)).ConformsToContract();
        Assert.That(quote.Value("//p:status"), Is.EqualTo("declined"));
        Assert.That(quote.Value("//p:premium"), Is.EqualTo("0.00"));

        var bind = await agent.IssuePolicyAsync(quote.Value("//p:quoteId"));

        Assert.That(bind.FaultCode, Is.EqualTo("QuoteNotOffered"));
        Assert.That(await Db.PoliciesForQuoteAsync(quote.Value("//p:quoteId")), Is.Zero);
    }

    [Test]
    public async Task AnExpiredQuoteCantBeBound()
    {
        // A service with its own clock, so the test can move time forward past the 30-minute quote.
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var service = Enterprise.Host(clock);
        var timed = new PolicyCaller(service);
        var quoteId = (await timed.GetQuoteAsync(Unique("Late Larry"), age: 50)).Value("//p:quoteId");

        clock.Advance(TimeSpan.FromMinutes(31));
        var bind = await timed.IssuePolicyAsync(quoteId);

        Assert.Multiple(async () =>
        {
            Assert.That(bind.FaultCode, Is.EqualTo("QuoteExpired"));
            Assert.That((await Db.QuoteAsync(quoteId))!.Status, Is.EqualTo("offered"), "nothing changed");
        });
    }

    [Test]
    public async Task UnknownQuotesAndPoliciesAreClientFaults()
    {
        var bind = await agent.IssuePolicyAsync(Guid.NewGuid().ToString());
        var read = await agent.GetPolicyAsync("POL-000000");

        Assert.Multiple(() =>
        {
            Assert.That(bind.FaultCode, Is.EqualTo("UnknownQuote"));
            Assert.That(bind.Value("//faultcode"), Is.EqualTo("soap:Client"));
            Assert.That(read.FaultCode, Is.EqualTo("UnknownPolicy"));
        });
    }

    [Test]
    public async Task MalformedXmlAndUnknownOperationsAreRefused()
    {
        var malformed = await agent.SendRawAsync("<soap:Envelope><not-closed>");
        var unknown = await agent.SendAsync(new System.Xml.Linq.XElement(
            System.Xml.Linq.XName.Get("CancelEverything", "http://devtestlike.example/policy")));

        Assert.Multiple(() =>
        {
            Assert.That(malformed.FaultCode, Is.EqualTo("MalformedXml"));
            Assert.That(unknown.FaultCode, Is.EqualTo("UnknownOperation"));
        });
    }
}
