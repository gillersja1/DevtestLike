using System.Net;
using DevtestLike.Tests.Environment;

namespace DevtestLike.Tests.Rentals;

/// <summary>Only the branch counter app may call the reservations API, and a refused call books nothing.</summary>
public class RentalAuthenticationTests
{
    [Test]
    public async Task NoTokenIsUnauthorized()
    {
        var reply = await new RentalCaller(Enterprise.Reservations) { ClientId = null }.BookAsync(RentalCaller.Booking(name: "No Token Nia"));

        Assert.That(reply.Status, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(await RentalDb.CountForAsync("No Token Nia"), Is.Zero);
    }

    // Real Keycloak tokens, for applications that exist but aren't allowed to book cars.
    [TestCase("reporting-client", "reporting-client-secret")]
    [TestCase("policy-client", "policy-client-secret")]
    public async Task OtherApplicationsAreForbidden(string client, string secret)
    {
        var name = $"Wrong App {client}";
        var licenceChecks = Enterprise.LicenseCheck.Checked.Count;

        var reply = await new RentalCaller(Enterprise.Reservations) { ClientId = client, ClientSecret = secret }
            .BookAsync(RentalCaller.Booking(name: name));

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(reply.Status, Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(Enterprise.LicenseCheck.Checked, Has.Count.EqualTo(licenceChecks), "an outside company was called anyway");
            Assert.That(await RentalDb.CountForAsync(name), Is.Zero);
        });
    }
}
