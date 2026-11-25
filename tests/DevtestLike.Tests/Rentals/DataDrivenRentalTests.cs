using System.Globalization;
using System.Net;
using DevtestLike.Tests.Environment;

namespace DevtestLike.Tests.Rentals;

/// <summary>
/// The counter's pricing and age rules, one test per row of DataSets/rentals.csv, like a DevTest test case driven by
/// a data set. Each row is checked in the reply and, when the booking goes through, in the database.
/// </summary>
public class DataDrivenRentalTests
{
    public sealed record Case(string Name, int Age, string CarClass, int Hours, int Status, string Code, int? Days, string Total)
    {
        public override string ToString() => $"{Name} ({Age}, {CarClass}, {Hours}h)";
    }

    public static IEnumerable<TestCaseData> Rentals() =>
        File.ReadLines(Path.Combine(AppContext.BaseDirectory, "DataSets", "rentals.csv"))
            .Skip(1)
            .Where(line => line.Trim().Length > 0 && !line.StartsWith('#'))
            .Select(line => line.Split(','))
            .Select(f => new Case(f[0], int.Parse(f[1], CultureInfo.InvariantCulture), f[2], int.Parse(f[3], CultureInfo.InvariantCulture),
                int.Parse(f[4], CultureInfo.InvariantCulture), f[5], f[6].Length > 0 ? int.Parse(f[6], CultureInfo.InvariantCulture) : null, f[7]))
            .Select(c => new TestCaseData(c).SetName($"Rental for {c}"));

    [TestCaseSource(nameof(Rentals))]
    public async Task BookingMatchesTheDataSet(Case row)
    {
        var pickup = Enterprise.RentalClock.AddDays(3);
        var reply = await new RentalCaller(Enterprise.Reservations).BookAsync(new
        {
            driver = new { name = row.Name, age = row.Age, licenseNumber = "OH7654321", licenseState = "OH" },
            carClass = row.CarClass,
            branch = "CLE-Downtown",
            pickupAt = pickup,
            dropoffAt = pickup.AddHours(row.Hours),
            cardToken = "tok_visa_4242",
        });

        Assert.That((int)reply.Status, Is.EqualTo(row.Status), reply.Body.ToString());
        if (row.Status != 201)
        {
            Assert.That(reply.Code, Is.EqualTo(row.Code));
            return;
        }
        var number = reply.Value("$.confirmationNumber");
        var saved = await RentalDb.ReservationAsync(number);
        Assert.Multiple(() =>
        {
            Assert.That(reply.Value("$.price.days"), Is.EqualTo(row.Days.ToString()));
            Assert.That(reply.Value("$.price.total"), Is.EqualTo(row.Total));
            Assert.That(saved!.Total, Is.EqualTo(decimal.Parse(row.Total, CultureInfo.InvariantCulture)));
            Assert.That(saved.Days, Is.EqualTo(row.Days));
        });
    }
}
