using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace RentalService;

/// <summary>An outside company's system didn't answer, or answered nonsense.</summary>
public sealed class VendorUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// The licence-check company's REST API. Someone else's system, charged per call, with a sandbox that is often
/// down: exactly what gets virtualized.
/// </summary>
public sealed class LicenseClient(HttpClient http)
{
    public async Task<string> CheckAsync(Driver driver, CancellationToken ct)
    {
        var reply = await Call(() => http.PostAsJsonAsync("/v1/license-checks",
            new { licenseNumber = driver.LicenseNumber, state = driver.LicenseState }, ct), "licence check");
        return Read(reply, "status", "licence check");
    }

    internal static async Task<JsonElement> Call(Func<Task<HttpResponseMessage>> send, string what, params HttpStatusCode[] alsoFine)
    {
        try
        {
            using var response = await send();
            if (!response.IsSuccessStatusCode && !alsoFine.Contains(response.StatusCode))
                throw new VendorUnavailableException($"The {what} service answered {(int)response.StatusCode}.");
            return await response.Content.ReadFromJsonAsync<JsonElement>();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException)
        {
            throw new VendorUnavailableException($"The {what} service didn't answer properly.", e);
        }
    }

    internal static string Read(JsonElement reply, string property, string what) =>
        reply.ValueKind == JsonValueKind.Object && reply.TryGetProperty(property, out var value) && value.GetString() is { Length: > 0 } text
            ? text
            : throw new VendorUnavailableException($"The {what} service's reply had no {property}.");
}

public sealed record Authorization(bool Approved, string? Id, string? DeclineReason);

/// <summary>The payment company's REST API: hold the rental's amount on the card, and release the hold on cancel.</summary>
public sealed class PaymentClient(HttpClient http)
{
    public async Task<Authorization> AuthorizeAsync(string cardToken, decimal amount, string reference, CancellationToken ct)
    {
        // A decline is an answer (402), not an outage.
        var reply = await LicenseClient.Call(() => http.PostAsJsonAsync("/v1/authorizations",
            new { cardToken, amount, currency = "USD", reference }, ct), "payment", HttpStatusCode.PaymentRequired);
        var status = LicenseClient.Read(reply, "status", "payment");
        return status == "approved"
            ? new Authorization(true, LicenseClient.Read(reply, "authorizationId", "payment"), null)
            : new Authorization(false, null, reply.TryGetProperty("reason", out var r) ? r.GetString() : status);
    }

    public async Task VoidAsync(string authorizationId, CancellationToken ct)
    {
        try
        {
            using var response = await http.PostAsync($"/v1/authorizations/{Uri.EscapeDataString(authorizationId)}/void", null, ct);
            if (!response.IsSuccessStatusCode)
                throw new VendorUnavailableException($"The payment service answered {(int)response.StatusCode} to the void.");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            throw new VendorUnavailableException("The payment service didn't answer the void.", e);
        }
    }
}
