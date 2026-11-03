using System.Globalization;
using DevtestLike.Tests.Environment;

namespace DevtestLike.Tests;

/// <summary>
/// One test, run once per row of a data set (DataSets/applicants.csv), like a DevTest test case driven by a
/// CSV data set. Adding a scenario means adding a row, not writing code.
/// </summary>
public class DataDrivenQuoteTests
{
    public sealed record Case(string Name, int Age, decimal VehicleValue, string State, string Status, int RiskScore, string Premium)
    {
        public override string ToString() => $"{Name} ({Age})";
    }

    public static IEnumerable<TestCaseData> Applicants() =>
        File.ReadLines(Path.Combine(AppContext.BaseDirectory, "DataSets", "applicants.csv"))
            .Skip(1)
            .Where(line => line.Trim().Length > 0 && !line.StartsWith('#'))
            .Select(line => line.Split(','))
            .Select(f => new Case(f[0], int.Parse(f[1], CultureInfo.InvariantCulture), decimal.Parse(f[2], CultureInfo.InvariantCulture),
                f[3], f[4], int.Parse(f[5], CultureInfo.InvariantCulture), f[6]))
            .Select(c => new TestCaseData(c).SetName($"Quote for {c}"));

    [OneTimeSetUp]
    public void StandardRatingRules()
    {
        Enterprise.RatingEngine.Reset();
        Enterprise.RatingEngine.WithStandardRules();
    }

    [TestCaseSource(nameof(Applicants))]
    public async Task QuoteMatchesTheDataSet(Case row)
    {
        var reply = (await new PolicyCaller(Enterprise.PolicyService).GetQuoteAsync(row.Name, row.Age, row.VehicleValue, row.State))
            .ConformsToContract();

        Assert.Multiple(async () =>
        {
            Assert.That(reply.Value("//p:status"), Is.EqualTo(row.Status));
            Assert.That(reply.Value("//p:riskScore"), Is.EqualTo(row.RiskScore.ToString(CultureInfo.InvariantCulture)));
            Assert.That(reply.Value("//p:premium"), Is.EqualTo(row.Premium));
            var saved = await Db.QuoteAsync(reply.Value("//p:quoteId"));
            Assert.That(saved?.Status, Is.EqualTo(row.Status), "and the database agrees");
        });
    }

    [Test]
    public async Task InvalidApplicantsAreRejectedBeforeTheRatingEngineIsCalled(
        [Values(15, 101)] int age)
    {
        Enterprise.RatingEngine.Reset();
        Enterprise.RatingEngine.WithStandardRules();

        var reply = await new PolicyCaller(Enterprise.PolicyService).GetQuoteAsync("Too Young Or Old", age);

        Assert.That(reply.FaultCode, Is.EqualTo("InvalidRequest"));
        Assert.That(Enterprise.RatingEngine.Requests, Is.Empty);
    }
}
