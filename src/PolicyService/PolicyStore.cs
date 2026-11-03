using Npgsql;

namespace PolicyService;

public sealed record Quote(Guid Id, Applicant Applicant, int RiskScore, decimal Premium, string Status, DateTimeOffset ExpiresAt);

public sealed record Policy(string PolicyNumber, Guid QuoteId, string HolderName, decimal Premium, string Status, DateTimeOffset IssuedAt);

/// <summary>The system of record, in Postgres. Tests check it directly, the way DevTest's database steps do.</summary>
public sealed class PolicyStore(NpgsqlDataSource db)
{
    public async Task EnsureCreatedAsync(CancellationToken ct = default)
    {
        await using var create = db.CreateCommand("""
            create table if not exists quotes (
                id uuid primary key,
                applicant_name text not null,
                age int not null,
                vehicle_value numeric(12, 2) not null,
                state char(2) not null,
                risk_score int not null,
                premium numeric(10, 2) not null,
                status text not null check (status in ('offered', 'declined', 'bound')),
                created_at timestamptz not null default now(),
                expires_at timestamptz not null);
            create table if not exists policies (
                policy_number text primary key,
                quote_id uuid not null unique references quotes (id),
                holder_name text not null,
                premium numeric(10, 2) not null,
                status text not null check (status in ('active', 'cancelled')),
                issued_at timestamptz not null default now());
            create sequence if not exists policy_numbers start 100000;
            """);
        await create.ExecuteNonQueryAsync(ct);
    }

    public async Task SaveQuoteAsync(Quote quote, CancellationToken ct)
    {
        await using var insert = db.CreateCommand("""
            insert into quotes (id, applicant_name, age, vehicle_value, state, risk_score, premium, status, expires_at)
            values ($1, $2, $3, $4, $5, $6, $7, $8, $9)
            """);
        foreach (var value in new object[] { quote.Id, quote.Applicant.Name, quote.Applicant.Age, quote.Applicant.VehicleValue,
                     quote.Applicant.State, quote.RiskScore, quote.Premium, quote.Status, quote.ExpiresAt })
        {
            insert.Parameters.Add(new() { Value = value });
        }
        await insert.ExecuteNonQueryAsync(ct);
    }

    public enum IssueOutcome { Issued, UnknownQuote, NotOffered, Expired }

    /// <summary>Binds an offered quote into a policy, in one transaction: either both rows change or neither does.</summary>
    public async Task<(IssueOutcome Outcome, Policy? Policy)> IssueAsync(Guid quoteId, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await using var find = new NpgsqlCommand(
            "select applicant_name, premium, status, expires_at from quotes where id = $1 for update", connection, tx);
        find.Parameters.Add(new() { Value = quoteId });
        string name, status;
        decimal premium;
        DateTimeOffset expires;
        await using (var reader = await find.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct))
            {
                return (IssueOutcome.UnknownQuote, null);
            }
            (name, premium, status, expires) = (reader.GetString(0), reader.GetDecimal(1), reader.GetString(2), reader.GetFieldValue<DateTimeOffset>(3));
        }
        if (status != "offered")
        {
            return (IssueOutcome.NotOffered, null);
        }
        if (now > expires)
        {
            return (IssueOutcome.Expired, null);
        }

        await using var issue = new NpgsqlCommand("""
            insert into policies (policy_number, quote_id, holder_name, premium, status)
            values ('POL-' || nextval('policy_numbers'), $1, $2, $3, 'active')
            returning policy_number, issued_at
            """, connection, tx);
        issue.Parameters.Add(new() { Value = quoteId });
        issue.Parameters.Add(new() { Value = name });
        issue.Parameters.Add(new() { Value = premium });
        string number;
        DateTimeOffset issued;
        await using (var reader = await issue.ExecuteReaderAsync(ct))
        {
            await reader.ReadAsync(ct);
            (number, issued) = (reader.GetString(0), reader.GetFieldValue<DateTimeOffset>(1));
        }

        await using var bind = new NpgsqlCommand("update quotes set status = 'bound' where id = $1", connection, tx);
        bind.Parameters.Add(new() { Value = quoteId });
        await bind.ExecuteNonQueryAsync(ct);

        await tx.CommitAsync(ct);
        return (IssueOutcome.Issued, new Policy(number, quoteId, name, premium, "active", issued));
    }

    public async Task<Policy?> GetPolicyAsync(string policyNumber, CancellationToken ct)
    {
        await using var select = db.CreateCommand(
            "select policy_number, quote_id, holder_name, premium, status, issued_at from policies where policy_number = $1");
        select.Parameters.Add(new() { Value = policyNumber });
        await using var reader = await select.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new Policy(reader.GetString(0), reader.GetGuid(1), reader.GetString(2), reader.GetDecimal(3), reader.GetString(4),
                reader.GetFieldValue<DateTimeOffset>(5))
            : null;
    }
}
