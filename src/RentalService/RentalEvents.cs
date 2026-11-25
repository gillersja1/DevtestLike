using System.Text.Json;
using Apache.NMS;
using Apache.NMS.ActiveMQ;

namespace RentalService;

/// <summary>Tells the branch, loyalty and billing systems about reservations.</summary>
public interface IRentalEvents
{
    Task PublishAsync(string eventType, Reservation reservation, CancellationToken ct);
}

public sealed class NoRentalEvents : IRentalEvents
{
    public Task PublishAsync(string eventType, Reservation reservation, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// JSON events on an ActiveMQ queue, with the confirmation number as the correlation id and the event
/// type as a message property, so a consumer (or a DevTest JMS step) can select just the one it wants.
/// </summary>
public sealed class ActiveMqRentalEvents(IConfiguration config, ILogger<ActiveMqRentalEvents> log) : IRentalEvents, IDisposable
{
    public const string Queue = "reservation.events";

    private readonly SemaphoreSlim gate = new(1, 1);
    private IConnection? connection;

    public async Task PublishAsync(string eventType, Reservation r, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new
        {
            eventType,
            confirmationNumber = r.ConfirmationNumber,
            branch = r.Branch,
            carClass = r.CarClass,
            pickupAt = r.PickupAt.UtcDateTime,
            dropoffAt = r.DropoffAt.UtcDateTime,
            total = r.Price.Total,
            // The branch needs a name to hand the keys to; the licence number stays in the reservations system.
            driverName = r.Driver.Name,
        });
        try
        {
            using var session = await (await ConnectAsync()).CreateSessionAsync(AcknowledgementMode.AutoAcknowledge);
            using var producer = await session.CreateProducerAsync(await session.GetQueueAsync(Queue));
            producer.DeliveryMode = MsgDeliveryMode.Persistent;
            var message = await session.CreateTextMessageAsync(body);
            message.NMSCorrelationID = r.ConfirmationNumber;
            message.Properties.SetString("eventType", eventType);
            await producer.SendAsync(message);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Already committed: a broker outage mustn't fail the booking. (A real system would use an outbox.)
            log.LogError(e, "Couldn't publish {EventType} for {ConfirmationNumber}", eventType, r.ConfirmationNumber);
        }
    }

    private async Task<IConnection> ConnectAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (connection is { IsStarted: true }) return connection;
            connection = await new ConnectionFactory(config["Messaging:BrokerUri"])
                .CreateConnectionAsync(config["Messaging:Username"], config["Messaging:Password"]);
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
