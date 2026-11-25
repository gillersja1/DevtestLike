using Microsoft.AspNetCore.Authentication.JwtBearer;
using Npgsql;
using RentalService;

// The rental counter's reservations API: REST and JSON, calling two outside companies (licence checks and
// payments) and announcing reservations on the message broker.
var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(config.GetConnectionString("Rentals")
    ?? throw new InvalidOperationException("Set ConnectionStrings:Rentals.")));
builder.Services.AddSingleton<RentalStore>();
if (string.IsNullOrEmpty(config["Messaging:BrokerUri"]))
    builder.Services.AddSingleton<IRentalEvents, NoRentalEvents>();
else
    builder.Services.AddSingleton<IRentalEvents, ActiveMqRentalEvents>();

// Outside companies: never let a slow one hang the counter.
var vendorTimeout = TimeSpan.FromSeconds(config.GetValue("Vendors:TimeoutSeconds", 3.0));
builder.Services.AddHttpClient<LicenseClient>(http =>
{
    http.BaseAddress = new Uri(config["LicenseCheck:Url"] ?? throw new InvalidOperationException("Set LicenseCheck:Url."));
    http.Timeout = vendorTimeout;
});
builder.Services.AddHttpClient<PaymentClient>(http =>
{
    http.BaseAddress = new Uri(config["Payments:Url"] ?? throw new InvalidOperationException("Set Payments:Url."));
    http.Timeout = vendorTimeout;
});

// An OAuth2 token from Keycloak, for an application allowed to call (the branch counter app).
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.Authority = config["Auth:Authority"];
    options.RequireHttpsMetadata = config.GetValue("Auth:RequireHttps", true);
    options.TokenValidationParameters.ValidateAudience = false;
});
var allowedClients = (config["Auth:AllowedClients"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
builder.Services.AddAuthorization(options => options.AddPolicy("CounterApps", policy =>
    policy.RequireAuthenticatedUser().RequireClaim("azp", allowedClients)));

var app = builder.Build();
await app.Services.GetRequiredService<RentalStore>().EnsureCreatedAsync();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

var api = app.MapGroup("/api/reservations").RequireAuthorization("CounterApps");

static IResult Problem(int status, string code, string detail) =>
    Results.Problem(statusCode: status, detail: detail, extensions: new Dictionary<string, object?> { ["code"] = code });

static object View(Reservation r) => new
{
    confirmationNumber = r.ConfirmationNumber,
    status = r.Status,
    driver = new { name = r.Driver.Name, age = r.Driver.Age },
    carClass = r.CarClass,
    branch = r.Branch,
    pickupAt = r.PickupAt.UtcDateTime,
    dropoffAt = r.DropoffAt.UtcDateTime,
    price = new { days = r.Price.Days, dailyRate = r.Price.DailyRate, youngDriverFee = r.Price.YoungDriverFee, total = r.Price.Total, currency = "USD" },
};

// Book: price it, check the licence, hold the money, then save and announce. Any refusal or outage before the
// save leaves nothing behind.
api.MapPost("", async (BookingRequest? booking, RentalStore store, LicenseClient licenses, PaymentClient payments, IRentalEvents events,
    TimeProvider clock, CancellationToken ct) =>
{
    if (booking is null) return Problem(400, "InvalidRequest", "The body must be a JSON booking.");
    if (Pricing.Problem(booking, clock.GetUtcNow()) is { } problem)
        return Problem(problem.Code == "InvalidRequest" ? 400 : 422, problem.Code, problem.Message);
    var price = Pricing.Quote(booking);

    string license;
    try
    {
        license = await licenses.CheckAsync(booking.Driver!, ct);
    }
    catch (VendorUnavailableException e)
    {
        return Problem(503, "LicenseCheckUnavailable", e.Message);
    }
    if (license != "valid") return Problem(422, "LicenseNotValid", $"The driver's licence is {license}.");

    var number = await store.NextNumberAsync(ct);
    Authorization hold;
    try
    {
        hold = await payments.AuthorizeAsync(booking.CardToken!, price.Total, number, ct);
    }
    catch (VendorUnavailableException e)
    {
        return Problem(503, "PaymentUnavailable", e.Message);
    }
    if (!hold.Approved) return Problem(402, "PaymentDeclined", $"The card was declined: {hold.DeclineReason}.");

    var reservation = new Reservation(number, booking.Driver!, booking.CarClass!.ToLowerInvariant(), booking.Branch!,
        booking.PickupAt!.Value, booking.DropoffAt!.Value, price, "confirmed", hold.Id!);
    await store.SaveAsync(reservation, ct);
    await events.PublishAsync("ReservationConfirmed", reservation, ct);
    return Results.Created($"/api/reservations/{number}", View(reservation));
});

api.MapGet("/{number}", async (string number, RentalStore store, CancellationToken ct) =>
    await store.GetAsync(number, ct) is { } r ? Results.Ok(View(r)) : Problem(404, "UnknownReservation", "No such reservation."));

// Cancel: release the hold first; if the payment company is down, the reservation stays confirmed.
api.MapPost("/{number}/cancel", async (string number, RentalStore store, PaymentClient payments, IRentalEvents events,
    TimeProvider clock, CancellationToken ct) =>
{
    if (await store.GetAsync(number, ct) is not { } r) return Problem(404, "UnknownReservation", "No such reservation.");
    if (r.Status != "confirmed") return Problem(409, "AlreadyCancelled", "The reservation is already cancelled.");
    try
    {
        await payments.VoidAsync(r.AuthorizationId, ct);
    }
    catch (VendorUnavailableException e)
    {
        return Problem(503, "PaymentUnavailable", e.Message);
    }
    if (!await store.CancelAsync(number, clock.GetUtcNow(), ct))
        return Problem(409, "AlreadyCancelled", "The reservation is already cancelled.");
    var cancelled = r with { Status = "cancelled" };
    await events.PublishAsync("ReservationCancelled", cancelled, ct);
    return Results.Ok(View(cancelled));
});

app.Run();

/// <summary>Marks this assembly for the tests' WebApplicationFactory (the policy service has the global Program).</summary>
namespace RentalService
{
    public sealed class RentalApp;
}
