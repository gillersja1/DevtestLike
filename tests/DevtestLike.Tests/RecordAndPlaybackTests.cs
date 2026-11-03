using DevtestLike.Tests.Environment;
using Microsoft.Extensions.Time.Testing;
using WireMock.Server;
using WireMock.Settings;

namespace DevtestLike.Tests;

/// <summary>
/// Record and playback, DevTest's way of building a virtual service from real traffic: put a recorder between the
/// policy service and the live rating engine, capture the conversations, then retire the live one and answer from
/// the recordings. The quotes must come out the same either way.
/// </summary>
public class RecordAndPlaybackTests
{
    private static readonly (string Name, int Age, decimal Value)[] Applicants =
    [
        ("Recorded Rae", 22, 18000m),
        ("Recorded Ray", 41, 32000m),
        ("Recorded Roz", 67, 12500m),
    ];

    private string recordings = null!;

    [SetUp]
    public void Folder() =>
        recordings = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "devtestlike-recordings", Guid.NewGuid().ToString("N"))).FullName;

    [TearDown]
    public void CleanUp() => Directory.Delete(recordings, recursive: true);

    private static async Task<Dictionary<string, (string Risk, string Premium)>> QuoteAllAsync(string ratingUrl)
    {
        await using var service = Enterprise.Host(new FakeTimeProvider(DateTimeOffset.UtcNow), ratingUrl: ratingUrl);
        var agent = new PolicyCaller(service);
        var results = new Dictionary<string, (string, string)>();
        foreach (var (name, age, value) in Applicants)
        {
            var reply = (await agent.GetQuoteAsync(name, age, value)).ConformsToContract();
            results[name] = (reply.Value("//p:riskScore"), reply.Value("//p:premium"));
        }
        return results;
    }

    [Test]
    public async Task RecordedTrafficReplaysTheSameQuotesAfterTheLiveServiceIsGone()
    {
        // Live: the real rating engine, straight.
        await using var live = await LiveRatingEngine.StartAsync();
        var direct = await QuoteAllAsync(live.Url);

        // Record: the same quotes through a recording proxy in front of it.
        var recorder = WireMockServer.Start(new WireMockServerSettings
        {
            ProxyAndRecordSettings = new ProxyAndRecordSettings
            {
                Url = live.Url.TrimEnd('/'),
                SaveMapping = true,
                SaveMappingToFile = false,
                // Match recordings on what matters (the SOAP body), not on headers that change per call.
                ExcludedHeaders = ["Host", "traceparent", "Content-Length", "Connection", "User-Agent", "Accept", "Accept-Encoding"],
            },
        });
        var recorded = await QuoteAllAsync(recorder.Url + "/");
        recorder.SaveStaticMappings(recordings);
        recorder.Stop();
        var callsWhileLive = live.Calls;

        // Playback: the live engine is retired; a virtual service answers from the recordings alone.
        await live.DisposeAsync();
        var playback = WireMockServer.Start();
        playback.ReadStaticMappings(recordings);
        var replayed = await QuoteAllAsync(playback.Url + "/");
        playback.Stop();

        Assert.Multiple(() =>
        {
            Assert.That(Directory.GetFiles(recordings, "*.json"), Has.Length.EqualTo(Applicants.Length), "one recording per conversation");
            Assert.That(recorded, Is.EqualTo(direct), "recording doesn't change the answers");
            Assert.That(replayed, Is.EqualTo(direct), "playback gives the same answers as the live service");
            Assert.That(callsWhileLive, Is.EqualTo(Applicants.Length * 2), "live answered the direct and recorded runs only");
        });
    }

    [Test]
    public async Task PlaybackOnlyKnowsWhatWasRecorded()
    {
        await using var live = await LiveRatingEngine.StartAsync();
        var recorder = WireMockServer.Start(new WireMockServerSettings
        {
            ProxyAndRecordSettings = new ProxyAndRecordSettings
            {
                Url = live.Url.TrimEnd('/'),
                SaveMapping = true,
                SaveMappingToFile = false,
                ExcludedHeaders = ["Host", "traceparent", "Content-Length", "Connection", "User-Agent", "Accept", "Accept-Encoding"],
            },
        });
        await QuoteAllAsync(recorder.Url + "/");
        recorder.SaveStaticMappings(recordings);
        recorder.Stop();

        var playback = WireMockServer.Start();
        playback.ReadStaticMappings(recordings);
        await using var service = Enterprise.Host(new FakeTimeProvider(DateTimeOffset.UtcNow), ratingUrl: playback.Url + "/");

        // An applicant nobody recorded: the virtual service has no answer, and the policy service says so.
        var reply = await new PolicyCaller(service).GetQuoteAsync("Never Recorded", age: 30, vehicleValue: 99999m);
        playback.Stop();

        Assert.That(reply.FaultCode, Is.EqualTo("RatingUnavailable"));
    }
}
