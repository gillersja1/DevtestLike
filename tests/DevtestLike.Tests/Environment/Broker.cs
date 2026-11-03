using System.Xml.Linq;
using System.Xml.Schema;
using Apache.NMS;
using Apache.NMS.ActiveMQ;

namespace DevtestLike.Tests.Environment;

/// <summary>A message the policy service published: its XML body, correlation id and properties.</summary>
public sealed record BrokerMessage(XDocument Body, string CorrelationId, string? EventType)
{
    private static readonly XNamespace E = "http://devtestlike.example/events";

    public string Value(string element) => Body.Root!.Element(E + element)?.Value
        ?? throw new AssertionException($"No <{element}> in:\n{Body}");

    /// <summary>Checks the body against contracts/events.xsd.</summary>
    public BrokerMessage ConformsToContract()
    {
        var errors = new List<string>();
        Body.Validate(Schemas, (_, e) => errors.Add(e.Message));
        Assert.That(errors, Is.Empty, $"Message breaks the contract:\n{Body}");
        return this;
    }

    private static readonly XmlSchemaSet Schemas = Load();

    private static XmlSchemaSet Load()
    {
        var set = new XmlSchemaSet();
        set.Add("http://devtestlike.example/events", Path.Combine(AppContext.BaseDirectory, "Contracts", "events.xsd"));
        set.Compile();
        return set;
    }
}

/// <summary>
/// Reads the policy service's messages off the broker, the way a downstream system (or a DevTest JMS step)
/// would: by queue, picking out the message for one policy with a JMS selector on its correlation id.
/// </summary>
public static class Broker
{
    public const string Queue = "policy.issued";

    public static async Task<BrokerMessage?> ReceiveAsync(string brokerUri, string correlationId, TimeSpan wait)
    {
        var factory = new ConnectionFactory(brokerUri);
        using var connection = await factory.CreateConnectionAsync(Enterprise.BrokerUser, Enterprise.BrokerPassword);
        await connection.StartAsync();
        using var session = await connection.CreateSessionAsync(AcknowledgementMode.AutoAcknowledge);
        using var consumer = await session.CreateConsumerAsync(await session.GetQueueAsync(Queue), $"JMSCorrelationID = '{correlationId}'");
        var message = await consumer.ReceiveAsync(wait) as ITextMessage;
        return message is null
            ? null
            : new BrokerMessage(XDocument.Parse(message.Text), message.NMSCorrelationID, message.Properties.GetString("eventType"));
    }
}
