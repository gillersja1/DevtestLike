using System.Net;
using DevtestLike.Tests.Environment;

namespace DevtestLike.Tests.Rentals;

/// <summary>
/// The outside companies on a bad day, which their real sandboxes can't be made to have on demand: a suspended
/// licence, a declined card, an outage, a slow reply. Each refusal must leave nothing behind: no reservation, and
/// no money held.
/// </summary>
public class VendorTests
{
    private RentalCaller counter = null!;

    [SetUp]
    public void Counter() => counter = new RentalCaller(Enterprise.Reservations);

    [TearDown]
    public void Restore()
    {
        Enterprise.LicenseCheck.WithStandardRules();
        Enterprise.Payments.WithStandardRules();
    }

    [TestCase("SUS0000001", "suspended")]
    [TestCase("EXP0000001", "expired")]
    public async Task ABadLicenceIsRefusedBeforeAnyMoneyIsHeld(string license, string state)
    {
        var name = $"Refused {state}";
        var paymentCalls = Enterprise.Payments.Calls.Count;

        var reply = await counter.BookAsync(new
        {
            driver = new { name, age = 40, licenseNumber = license, licenseState = "OH" },
            carClass = "economy", branch = "CMH-Airport",
            pickupAt = Enterprise.RentalClock.AddDays(1), dropoffAt = Enterprise.RentalClock.AddDays(2),
            cardToken = "tok_visa_4242",
        });

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(reply.Status, Is.EqualTo(HttpStatusCode.UnprocessableEntity));
            Assert.That(reply.Code, Is.EqualTo("LicenseNotValid"));
            Assert.That(reply.Value("$.detail"), Does.Contain(state));
            Assert.That(Enterprise.LicenseCheck.Checked, Does.Contain(license));
            Assert.That(Enterprise.Payments.Calls, Has.Count.EqualTo(paymentCalls), "the card was charged anyway");
            Assert.That(await RentalDb.CountForAsync(name), Is.Zero);
        });
    }

    [Test]
    public async Task ADeclinedCardMeansNoReservation()
    {
        var reply = await counter.BookAsync(RentalCaller.Booking(name: "Declined Dot", card: "tok_declined_insufficient"));

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(reply.Status, Is.EqualTo(HttpStatusCode.PaymentRequired));
            Assert.That(reply.Code, Is.EqualTo("PaymentDeclined"));
            Assert.That(reply.Value("$.detail"), Does.Contain("insufficient_funds"));
            Assert.That(await RentalDb.CountForAsync("Declined Dot"), Is.Zero);
        });
    }

    [Test]
    public async Task ALicenceCheckOutageIsAServerErrorAndSavesNothing()
    {
        Enterprise.LicenseCheck.Down();

        var reply = await counter.BookAsync(RentalCaller.Booking(name: "Outage Olu"));

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(reply.Status, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
            Assert.That(reply.Code, Is.EqualTo("LicenseCheckUnavailable"));
            Assert.That(await RentalDb.CountForAsync("Outage Olu"), Is.Zero);
        });
    }

    [Test]
    public async Task APaymentOutageIsAServerErrorAndSavesNothing()
    {
        Enterprise.Payments.Down();

        var reply = await counter.BookAsync(RentalCaller.Booking(name: "Outage Pat"));

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(reply.Status, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
            Assert.That(reply.Code, Is.EqualTo("PaymentUnavailable"));
            Assert.That(await RentalDb.CountForAsync("Outage Pat"), Is.Zero);
        });
    }

    [Test]
    public async Task ASlowLicenceCheckTimesOutInsteadOfHangingTheCounter()
    {
        Enterprise.LicenseCheck.Slow(TimeSpan.FromSeconds(3));
        await using var impatient = Enterprise.RentalHost(new Microsoft.Extensions.Time.Testing.FakeTimeProvider(Enterprise.RentalClock),
            vendorTimeoutSeconds: 0.5);

        var started = DateTime.UtcNow;
        var reply = await new RentalCaller(impatient).BookAsync(RentalCaller.Booking(name: "Slow Sol"));

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(reply.Status, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
            Assert.That(reply.Code, Is.EqualTo("LicenseCheckUnavailable"));
            Assert.That(DateTime.UtcNow - started, Is.LessThan(TimeSpan.FromSeconds(2.5)));
            Assert.That(await RentalDb.CountForAsync("Slow Sol"), Is.Zero);
        });
    }
}
