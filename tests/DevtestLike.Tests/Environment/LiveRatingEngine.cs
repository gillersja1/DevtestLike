using System.Globalization;
using System.Xml.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace DevtestLike.Tests.Environment;

/// <summary>
/// A stand-in for the third party's real rating engine (their sandbox): a real HTTP service on a real port, with
/// its own rules. Record-and-playback captures its traffic, so the tests can carry on after it's gone.
/// </summary>
public sealed class LiveRatingEngine : IAsyncDisposable
{
    private readonly WebApplication app;

    private LiveRatingEngine(WebApplication app) => this.app = app;

    public string Url { get; private set; } = "";

    /// <summary>How many requests it has answered: playback works when this stops going up.</summary>
    public int Calls { get; private set; }

    public static async Task<LiveRatingEngine> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var engine = new LiveRatingEngine(app);
        XNamespace r = "http://devtestlike.example/rating";

        app.MapPost("/rating", async (HttpRequest request) =>
        {
            engine.Calls++;
            var body = await XDocument.LoadAsync(request.Body, LoadOptions.None, default);
            var age = (int)body.Descendants(r + "age").Single();
            var value = decimal.Parse(body.Descendants(r + "vehicleValue").Single().Value, CultureInfo.InvariantCulture);
            // The third party's own rules: riskier for young drivers and expensive cars.
            var risk = Math.Min(100, (age < 25 ? 40 : 15) + (int)(value / 10000m) * 5);
            var baseRate = 450m + value / 100m;
            return Results.Text($"""
                <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
                  <soap:Body>
                    <r:RateRiskResponse xmlns:r="http://devtestlike.example/rating">
                      <r:riskScore>{risk}</r:riskScore>
                      <r:baseRate>{baseRate.ToString("0.00", CultureInfo.InvariantCulture)}</r:baseRate>
                    </r:RateRiskResponse>
                  </soap:Body>
                </soap:Envelope>
                """, "text/xml; charset=utf-8");
        });

        await app.StartAsync();
        engine.Url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First() + "/";
        return engine;
    }

    public async ValueTask DisposeAsync()
    {
        await app.StopAsync();
        await app.DisposeAsync();
    }
}
