using System.Globalization;
using System.Xml.Linq;
using Apache.NMS;
using Apache.NMS.ActiveMQ;

namespace PolicyService;

/// <summary>Tells the rest of the enterprise (billing, documents, the data warehouse) that something happened.</summary>
public interface IPolicyEvents
{
    Task PolicyIssuedAsync(Policy policy, CancellationToken ct);
}

/// <summary>For running without a broker: events go nowhere.</summary>
public sealed class NoPolicyEvents : IPolicyEvents
{
    public Task PolicyIssuedAsync(Policy policy, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// Publishes events as XML text messages on an ActiveMQ queue, through Apache NMS (.NET's take on Java's JMS),
/// with the policy number as the correlation id so a consumer can pick out the message about one policy.
/// </summary>
public sealed class ActiveMqPolicyEvents(IConfiguration config, ILogger<ActiveMqPolicyEvents> log) : IPolicyEvents, IDisposable
{
    public const string Queue = "policy.issued";
    public static readonly XNamespace Events = "http://devtestlike.example/events";

    private readonly SemaphoreSlim gate = new(1, 1);
    private IConnection? connection;

    public async Task PolicyIssuedAsync(Policy policy, CancellationToken ct)
    {
        var body = new XElement(Events + "PolicyIssued",
            new XAttribute(XNamespace.Xmlns + "e", Events),
            new XElement(Events + "policyNumber", policy.PolicyNumber),
            new XElement(Events + "quoteId", policy.QuoteId),
            new XElement(Events + "holderName", policy.HolderName),
            new XElement(Events + "premium", policy.Premium.ToString("0.00", CultureInfo.InvariantCulture)),
            new XElement(Events + "issuedAt", policy.IssuedAt.UtcDateTime));
        try
        {
            using var session = await (await ConnectAsync()).CreateSessionAsync(AcknowledgementMode.AutoAcknowledge);
            using var producer = await session.CreateProducerAsync(await session.GetQueueAsync(Queue));
            producer.DeliveryMode = MsgDeliveryMode.Persistent;
            var message = await session.CreateTextMessageAsync(body.ToString(SaveOptions.DisableFormatting));
            message.NMSCorrelationID = policy.PolicyNumber;
            message.Properties.SetString("eventType", "PolicyIssued");
            await producer.SendAsync(message);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The policy is already committed, so a broker outage mustn't undo it or fail the caller. A production
            // system would write the event to an outbox table in the same transaction and relay it later.
            log.LogError(e, "Couldn't publish PolicyIssued for {PolicyNumber}", policy.PolicyNumber);
        }
    }

    private async Task<IConnection> ConnectAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (connection is { IsStarted: true })
            {
                return connection;
            }
            var factory = new ConnectionFactory(config["Messaging:BrokerUri"]);
            connection = await factory.CreateConnectionAsync(config["Messaging:Username"], config["Messaging:Password"]);
            await connection.StartAsync();
            return connection;
        }
        finally
        {
            gate.Release();
        }
    }

    public void Dispose()
    {
        connection?.Dispose();
        gate.Dispose();
    }
}
