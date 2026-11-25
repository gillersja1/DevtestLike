extern alias rental;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Apache.NMS;
using Apache.NMS.ActiveMQ;
using Microsoft.AspNetCore.Mvc.Testing;

namespace DevtestLike.Tests.Environment;

/// <summary>A JSON reply: the status, the body, and a path into it, like a DevTest JSON Path filter.</summary>
public sealed record JsonReply(HttpStatusCode Status, JsonElement Body, string? Location)
{
    /// <summary>
    /// The value at a simple path such as "$.price.total" or "$.driver.name" (dots and [index] only), as text.
    /// </summary>
    public string Value(string path)
    {
        var node = Body;
        foreach (var part in path.TrimStart('$').Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var name = part;
            int? index = null;
            if (part.EndsWith(']') && part.IndexOf('[') is var open and > 0)
            {
                index = int.Parse(part[(open + 1)..^1]);
                name = part[..open];
            }
            if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(name, out node))
                throw new AssertionException($"Nothing at {path} in:\n{Body}");
            if (index is { } i) node = node[i];
        }
        return node.ValueKind == JsonValueKind.String ? node.GetString()! : node.GetRawText();
    }

    public bool Has(string property) => Body.ValueKind == JsonValueKind.Object && Body.TryGetProperty(property, out _);

    /// <summary>The error code from a problem-details reply.</summary>
    public string Code => Value("$.code");
}

/// <summary>Calls the reservations API the way the branch counter app would: JSON, with an OAuth2 token.</summary>
public sealed class RentalCaller(WebApplicationFactory<rental::RentalService.RentalApp> service)
{
    public string? ClientId { get; init; } = "counter-client";
    public string? ClientSecret { get; init; } = "counter-client-secret";

    public Task<JsonReply> BookAsync(object booking) => SendAsync(HttpMethod.Post, "/api/reservations", JsonSerializer.Serialize(booking));

    public Task<JsonReply> GetAsync(string number) => SendAsync(HttpMethod.Get, $"/api/reservations/{number}");

    public Task<JsonReply> CancelAsync(string number) => SendAsync(HttpMethod.Post, $"/api/reservations/{number}/cancel");

    public async Task<JsonReply> SendAsync(HttpMethod method, string path, string? json = null)
    {
        using var http = service.CreateClient();
        using var request = new HttpRequestMessage(method, path);
        if (json is not null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        if (ClientId is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await PolicyCaller.TokenAsync(ClientId, ClientSecret!));
        using var response = await http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        return new JsonReply(response.StatusCode,
            body.Length == 0 ? default : JsonDocument.Parse(body).RootElement.Clone(),
            response.Headers.Location?.ToString());
    }

    /// <summary>A booking that passes every rule, with the parts a test wants to change.</summary>
    public static object Booking(string name = "Dee Driver", int age = 34, string license = "OH1234567", string carClass = "midsize",
        int days = 3, string card = "tok_visa_4242", string branch = "CMH-Airport", DateTimeOffset? pickup = null)
    {
        var at = pickup ?? Enterprise.RentalClock.AddDays(7);
        return new
        {
            driver = new { name, age, licenseNumber = license, licenseState = license[..2] },
            carClass,
            branch,
            pickupAt = at,
            dropoffAt = at.AddDays(days),
            cardToken = card,
        };
    }
}

/// <summary>Database validation for the reservations table.</summary>
public static class RentalDb
{
    public sealed record ReservationRow(string Status, string CarClass, int Days, decimal Total, string AuthorizationId, string LicenseNumber);

    public static async Task<ReservationRow?> ReservationAsync(string number)
    {
        await using var select = Enterprise.Database.CreateCommand(
            "select status, car_class, days, total, authorization_id, license_number from reservations where confirmation_number = $1");
        select.Parameters.Add(new() { Value = number });
        await using var row = await select.ExecuteReaderAsync();
        return await row.ReadAsync()
            ? new ReservationRow(row.GetString(0), row.GetString(1), row.GetInt32(2), row.GetDecimal(3), row.GetString(4), row.GetString(5))
            : null;
    }

    /// <summary>How many reservations a driver has: a refused booking must leave this unchanged.</summary>
    public static async Task<long> CountForAsync(string driverName)
    {
        await using var count = Enterprise.Database.CreateCommand("select count(*) from reservations where driver_name = $1");
        count.Parameters.Add(new() { Value = driverName });
        return (long)(await count.ExecuteScalarAsync())!;
    }
}

/// <summary>A JMS consume step for the reservation events: by correlation id and event type.</summary>
public static class RentalBroker
{
    public const string Queue = "reservation.events";

    public static async Task<JsonElement?> ReceiveAsync(string confirmationNumber, string eventType, TimeSpan wait)
    {
        var factory = new ConnectionFactory(Enterprise.BrokerUri);
        using var connection = await factory.CreateConnectionAsync(Enterprise.BrokerUser, Enterprise.BrokerPassword);
        await connection.StartAsync();
        using var session = await connection.CreateSessionAsync(AcknowledgementMode.AutoAcknowledge);
        using var consumer = await session.CreateConsumerAsync(await session.GetQueueAsync(Queue),
            $"JMSCorrelationID = '{confirmationNumber}' AND eventType = '{eventType}'");
        return await consumer.ReceiveAsync(wait) is ITextMessage message ? JsonDocument.Parse(message.Text).RootElement.Clone() : null;
    }
}
