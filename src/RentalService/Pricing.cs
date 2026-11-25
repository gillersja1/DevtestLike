namespace RentalService;

public sealed record Driver(string Name, int Age, string LicenseNumber, string LicenseState);

public sealed record BookingRequest(Driver? Driver, string? CarClass, string? Branch, DateTimeOffset? PickupAt, DateTimeOffset? DropoffAt, string? CardToken);

public sealed record Price(int Days, decimal DailyRate, decimal YoungDriverFee, decimal Total);

/// <summary>
/// The counter's rules. A rental is charged per started day; drivers under 21 can't rent; 21 to 24 pay a young
/// driver fee per day; luxury cars need a driver of 25 or over.
/// </summary>
public static class Pricing
{
    public static readonly IReadOnlyDictionary<string, decimal> DailyRates = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
    {
        ["economy"] = 45.00m,
        ["midsize"] = 60.00m,
        ["suv"] = 85.00m,
        ["luxury"] = 140.00m,
    };

    public const decimal YoungDriverFeePerDay = 25.00m;
    public const int MinimumAge = 21;
    public const int YoungDriverUntil = 25;
    public const int MaxDays = 30;

    /// <summary>What's wrong with the booking, as (code, message), or null if it can be priced.</summary>
    public static (string Code, string Message)? Problem(BookingRequest b, DateTimeOffset now)
    {
        if (b.Driver is not { Name.Length: > 0, LicenseNumber.Length: > 0, LicenseState.Length: 2 })
            return ("InvalidRequest", "driver needs a name, a licenseNumber and a 2-letter licenseState.");
        if (b.CarClass is null || !DailyRates.ContainsKey(b.CarClass))
            return ("InvalidRequest", $"carClass must be one of {string.Join(", ", DailyRates.Keys)}.");
        if (string.IsNullOrWhiteSpace(b.Branch) || string.IsNullOrWhiteSpace(b.CardToken))
            return ("InvalidRequest", "branch and cardToken are required.");
        if (b.PickupAt is not { } pickup || b.DropoffAt is not { } dropoff || dropoff <= pickup)
            return ("InvalidRequest", "pickupAt and dropoffAt are required, and dropoff must be after pickup.");
        if (pickup < now)
            return ("PickupInPast", "pickupAt is in the past.");
        if (DaysBetween(pickup, dropoff) > MaxDays)
            return ("TooLong", $"A rental can be at most {MaxDays} days.");
        if (b.Driver.Age < MinimumAge)
            return ("DriverTooYoung", $"Drivers must be at least {MinimumAge}.");
        if (b.CarClass.Equals("luxury", StringComparison.OrdinalIgnoreCase) && b.Driver.Age < YoungDriverUntil)
            return ("DriverTooYoung", $"Luxury cars need a driver of at least {YoungDriverUntil}.");
        return null;
    }

    public static Price Quote(BookingRequest b)
    {
        var days = DaysBetween(b.PickupAt!.Value, b.DropoffAt!.Value);
        var rate = DailyRates[b.CarClass!];
        var fee = b.Driver!.Age < YoungDriverUntil ? YoungDriverFeePerDay : 0m;
        return new Price(days, rate, fee, (rate + fee) * days);
    }

    /// <summary>Every started 24 hours is a day: 25 hours is two days.</summary>
    public static int DaysBetween(DateTimeOffset pickup, DateTimeOffset dropoff) =>
        Math.Max(1, (int)Math.Ceiling((dropoff - pickup).TotalHours / 24));
}
