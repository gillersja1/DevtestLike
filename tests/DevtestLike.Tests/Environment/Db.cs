namespace DevtestLike.Tests.Environment;

/// <summary>
/// Database validation: reads the system of record directly, so a test can check what was actually written,
/// not only what the service said. DevTest does this with JDBC steps and assertions on the result set.
/// </summary>
public static class Db
{
    public sealed record QuoteRow(string Status, decimal Premium, int RiskScore, string ApplicantName);

    public sealed record PolicyRow(string QuoteId, string HolderName, decimal Premium, string Status);

    public static async Task<QuoteRow?> QuoteAsync(string quoteId)
    {
        await using var select = Enterprise.Database.CreateCommand(
            "select status, premium, risk_score, applicant_name from quotes where id = $1::uuid");
        select.Parameters.Add(new() { Value = quoteId });
        await using var reader = await select.ExecuteReaderAsync();
        return await reader.ReadAsync()
            ? new QuoteRow(reader.GetString(0), reader.GetDecimal(1), reader.GetInt32(2), reader.GetString(3))
            : null;
    }

    public static async Task<PolicyRow?> PolicyAsync(string policyNumber)
    {
        await using var select = Enterprise.Database.CreateCommand(
            "select quote_id::text, holder_name, premium, status from policies where policy_number = $1");
        select.Parameters.Add(new() { Value = policyNumber });
        await using var reader = await select.ExecuteReaderAsync();
        return await reader.ReadAsync()
            ? new PolicyRow(reader.GetString(0), reader.GetString(1), reader.GetDecimal(2), reader.GetString(3))
            : null;
    }

    public static async Task<long> PoliciesForQuoteAsync(string quoteId)
    {
        await using var count = Enterprise.Database.CreateCommand("select count(*) from policies where quote_id = $1::uuid");
        count.Parameters.Add(new() { Value = quoteId });
        return (long)(await count.ExecuteScalarAsync())!;
    }

    public static async Task<long> QuotesForApplicantAsync(string name)
    {
        await using var count = Enterprise.Database.CreateCommand("select count(*) from quotes where applicant_name = $1");
        count.Parameters.Add(new() { Value = name });
        return (long)(await count.ExecuteScalarAsync())!;
    }
}
