using System.Globalization;
using System.Xml.Linq;

namespace PolicyService;

public sealed record Applicant(string Name, int Age, decimal VehicleValue, string State);

public sealed record Rating(int RiskScore, decimal BaseRate);

/// <summary>Thrown when the rating engine can't give an answer: down, too slow, or talking nonsense.</summary>
public sealed class RatingUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// The third-party rating engine the policy service depends on. In tests it's a virtual service (WireMock.Net),
/// the way DevTest stands in for a dependency that's slow, costly or not built yet.
/// </summary>
public sealed class RatingClient(HttpClient http)
{
    public async Task<Rating> RateAsync(Applicant applicant, CancellationToken ct)
    {
        var request = Soap.Envelope(new XElement(Soap.Rating + "RateRisk",
            new XElement(Soap.Rating + "age", applicant.Age),
            new XElement(Soap.Rating + "vehicleValue", applicant.VehicleValue.ToString(CultureInfo.InvariantCulture)),
            new XElement(Soap.Rating + "state", applicant.State)));

        HttpResponseMessage response;
        try
        {
            using var content = new StringContent(request.ToString(), System.Text.Encoding.UTF8, "text/xml");
            content.Headers.Add("SOAPAction", "\"http://devtestlike.example/rating/RateRisk\"");
            response = await http.PostAsync("rating", content, ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            throw new RatingUnavailableException("The rating engine didn't answer in time.", e);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new RatingUnavailableException($"The rating engine failed with HTTP {(int)response.StatusCode}.");
        }
        try
        {
            var result = Soap.Operation(XDocument.Parse(await response.Content.ReadAsStringAsync(ct)))
                ?? throw new FormatException("No SOAP body");
            return new Rating(
                (int)result.Element(Soap.Rating + "riskScore")!,
                decimal.Parse(result.Element(Soap.Rating + "baseRate")!.Value, CultureInfo.InvariantCulture));
        }
        catch (Exception e) when (e is not RatingUnavailableException)
        {
            throw new RatingUnavailableException("The rating engine's reply couldn't be read.", e);
        }
    }
}
