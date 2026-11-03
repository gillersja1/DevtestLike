using DevtestLike.Tests.Environment;
using Microsoft.Extensions.Time.Testing;

namespace DevtestLike.Tests;

/// <summary>
/// Messaging across systems: issuing a policy publishes a PolicyIssued XML message on an ActiveMQ queue for
/// downstream systems. The tests consume it the way they would, through NMS (.NET's JMS), selecting the message
/// for one policy by its correlation id, like a DevTest JMS step.
/// </summary>
public class MessagingTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private PolicyCaller agent = null!;

    [SetUp]
    public void SetUp()
    {
        Enterprise.RatingEngine.Reset();
        Enterprise.RatingEngine.WithStandardRules();
        agent = new PolicyCaller(Enterprise.PolicyService);
    }

    private async Task<(string QuoteId, string PolicyNumber)> IssueAsync(string holder)
    {
        var quoteId = (await agent.GetQuoteAsync(holder, age: 36)).Value("//p:quoteId");
        return (quoteId, (await agent.IssuePolicyAsync(quoteId)).Value("//p:policyNumber"));
    }

    [Test]
    public async Task IssuingAPolicyPublishesAnEventDownstreamSystemsCanTrust()
    {
        var (quoteId, policyNumber) = await IssueAsync("Event Eve");

        var message = (await Broker.ReceiveAsync(Enterprise.BrokerUri, policyNumber, Wait))?.ConformsToContract();

        Assert.That(message, Is.Not.Null, $"no PolicyIssued message for {policyNumber}");
        Assert.Multiple(async () =>
        {
            Assert.That(message!.CorrelationId, Is.EqualTo(policyNumber));
            Assert.That(message.EventType, Is.EqualTo("PolicyIssued"));
            Assert.That(message.Value("policyNumber"), Is.EqualTo(policyNumber));
            Assert.That(message.Value("quoteId"), Is.EqualTo(quoteId));
            Assert.That(message.Value("holderName"), Is.EqualTo("Event Eve"));
            // The message, the SOAP reply and the database all agree on the money.
            var row = await Db.PolicyAsync(policyNumber);
            Assert.That(decimal.Parse(message.Value("premium"), System.Globalization.CultureInfo.InvariantCulture), Is.EqualTo(row!.Premium));
        });
    }

    [Test]
    public async Task ARefusedSecondBindPublishesNothingMore()
    {
        var (quoteId, policyNumber) = await IssueAsync("Once Only");
        Assert.That(await Broker.ReceiveAsync(Enterprise.BrokerUri, policyNumber, Wait), Is.Not.Null);

        await agent.IssuePolicyAsync(quoteId);

        Assert.That(await Broker.ReceiveAsync(Enterprise.BrokerUri, policyNumber, TimeSpan.FromSeconds(1)), Is.Null,
            "exactly one event per policy");
    }

    [Test]
    public async Task APolicyIsStillIssuedWhenTheBrokerIsDown()
    {
        // A broker address nothing listens on. The policy is committed before publishing, so the outage is
        // logged rather than failing the agent's request. (A production system would add an outbox.)
        await using var service = Enterprise.Host(new FakeTimeProvider(DateTimeOffset.UtcNow), brokerUri: "activemq:tcp://127.0.0.1:1");
        var offline = new PolicyCaller(service);
        var quoteId = (await offline.GetQuoteAsync("Broker Down", age: 36)).Value("//p:quoteId");

        var issued = await offline.IssuePolicyAsync(quoteId);

        Assert.Multiple(async () =>
        {
            Assert.That(issued.IsFault, Is.False);
            Assert.That(await Db.PolicyAsync(issued.Value("//p:policyNumber")), Is.Not.Null);
        });
    }
}
