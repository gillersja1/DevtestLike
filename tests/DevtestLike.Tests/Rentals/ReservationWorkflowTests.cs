using System.Net;
using DevtestLike.Tests.Environment;

namespace DevtestLike.Tests.Rentals;

/// <summary>
/// The rental counter, end to end: book, read back, cancel. Each step feeds the next with a value pulled from the
/// previous reply (the confirmation number), and every step is checked in the reply, the database, the payment
/// company's virtual service and the message broker.
/// </summary>
public class ReservationWorkflowTests
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

    [Test]
    public async Task BookThenReadBackThenCancel()
    {
        // Step 1: book a midsize car for 3 days. 3 x 60.00 = 180.00, no young driver fee at 34.
        var booked = await counter.BookAsync(RentalCaller.Booking(name: "Workflow Wes", carClass: "midsize", days: 3));
        Assert.That(booked.Status, Is.EqualTo(HttpStatusCode.Created));
        var number = booked.Value("$.confirmationNumber");                    // JSON Path filter: save for later steps
        Assert.Multiple(() =>
        {
            Assert.That(number, Does.Match("^RZ[0-9]+$"));
            Assert.That(booked.Location, Is.EqualTo($"/api/reservations/{number}"));
            Assert.That(booked.Value("$.status"), Is.EqualTo("confirmed"));
            Assert.That(booked.Value("$.price.total"), Is.EqualTo("180.00"));
        });

        // The money was held at the payment company, for exactly that amount, under this reservation.
        var hold = Enterprise.Payments.Authorizations(number).Single();
        Assert.That(hold.Body.GetProperty("amount").GetDecimal(), Is.EqualTo(180.00m));

        // The database agrees, and holds the authorization id the payment company returned.
        var row = await RentalDb.ReservationAsync(number);
        Assert.That(row, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(row!.Status, Is.EqualTo("confirmed"));
            Assert.That(row.Total, Is.EqualTo(180.00m));
            Assert.That(row.Days, Is.EqualTo(3));
            Assert.That(row.AuthorizationId, Does.StartWith("AUTH-"));
        });

        // The branch was told (JMS consume step with a selector).
        var confirmed = await RentalBroker.ReceiveAsync(number, "ReservationConfirmed", TimeSpan.FromSeconds(5));
        Assert.That(confirmed, Is.Not.Null, "No ReservationConfirmed message");
        Assert.Multiple(() =>
        {
            Assert.That(confirmed!.Value.GetProperty("total").GetDecimal(), Is.EqualTo(180.00m));
            Assert.That(confirmed.Value.GetProperty("branch").GetString(), Is.EqualTo("CMH-Airport"));
            // The licence number is personal data: it stays in the reservations system.
            Assert.That(confirmed.Value.TryGetProperty("licenseNumber", out _), Is.False);
        });

        // Step 2: read it back by the number step 1 returned.
        var read = await counter.GetAsync(number);
        Assert.Multiple(() =>
        {
            Assert.That(read.Status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(read.Value("$.driver.name"), Is.EqualTo("Workflow Wes"));
            Assert.That(read.Value("$.carClass"), Is.EqualTo("midsize"));
        });

        // Step 3: cancel. The hold is released, the row is cancelled, and the branch is told.
        var cancelled = await counter.CancelAsync(number);
        Assert.Multiple(() =>
        {
            Assert.That(cancelled.Status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(cancelled.Value("$.status"), Is.EqualTo("cancelled"));
        });
        Assert.That(Enterprise.Payments.Calls.Select(c => c.Path), Does.Contain($"/v1/authorizations/{row!.AuthorizationId}/void"));
        Assert.That((await RentalDb.ReservationAsync(number))!.Status, Is.EqualTo("cancelled"));
        Assert.That(await RentalBroker.ReceiveAsync(number, "ReservationCancelled", TimeSpan.FromSeconds(5)), Is.Not.Null);
    }

    [Test]
    public async Task AReservationCanOnlyBeCancelledOnce()
    {
        var number = (await counter.BookAsync(RentalCaller.Booking(name: "Twice Tam"))).Value("$.confirmationNumber");
        await counter.CancelAsync(number);

        var again = await counter.CancelAsync(number);

        Assert.Multiple(() =>
        {
            Assert.That(again.Status, Is.EqualTo(HttpStatusCode.Conflict));
            Assert.That(again.Code, Is.EqualTo("AlreadyCancelled"));
        });
    }

    [Test]
    public async Task IfThePaymentCompanyIsDownTheCancellationWaitsAndNothingChanges()
    {
        var number = (await counter.BookAsync(RentalCaller.Booking(name: "Stuck Sam"))).Value("$.confirmationNumber");
        Enterprise.Payments.VoidsDown();

        var cancel = await counter.CancelAsync(number);
        var row = await RentalDb.ReservationAsync(number);

        Assert.Multiple(() =>
        {
            Assert.That(cancel.Status, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
            Assert.That(cancel.Code, Is.EqualTo("PaymentUnavailable"));
            // Still confirmed, so the customer isn't told it's cancelled while the money is still held.
            Assert.That(row!.Status, Is.EqualTo("confirmed"));
        });
    }

    [Test]
    public async Task UnknownReservationsAreNotFound()
    {
        var read = await counter.GetAsync("RZ0");
        Assert.Multiple(() =>
        {
            Assert.That(read.Status, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(read.Code, Is.EqualTo("UnknownReservation"));
        });
    }
}
