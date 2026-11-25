using Npgsql;

namespace RentalService;

public sealed record Reservation(string ConfirmationNumber, Driver Driver, string CarClass, string Branch,
    DateTimeOffset PickupAt, DateTimeOffset DropoffAt, Price Price, string Status, string AuthorizationId);

/// <summary>The reservations system of record, in Postgres, next to the policy service's tables.</summary>
public sealed class RentalStore(NpgsqlDataSource db)
{
    public async Task EnsureCreatedAsync(CancellationToken ct = default)
    {
        await using var create = db.CreateCommand("""
            create table if not exists reservations (
                confirmation_number text primary key,
                driver_name text not null,
                driver_age int not null,
                license_number text not null,
                license_state char(2) not null,
                car_class text not null,
                branch text not null,
                pickup_at timestamptz not null,
                dropoff_at timestamptz not null,
                days int not null,
                daily_rate numeric(8, 2) not null,
                young_driver_fee numeric(8, 2) not null,
                total numeric(10, 2) not null,
                status text not null check (status in ('confirmed', 'cancelled')),
                authorization_id text not null,
                created_at timestamptz not null default now(),
                cancelled_at timestamptz);
            create sequence if not exists reservation_numbers start 400000;
            """);
        await create.ExecuteNonQueryAsync(ct);
    }

    public async Task<string> NextNumberAsync(CancellationToken ct)
    {
        await using var next = db.CreateCommand("select 'RZ' || nextval('reservation_numbers')");
        return (string)(await next.ExecuteScalarAsync(ct))!;
    }

    public async Task SaveAsync(Reservation r, CancellationToken ct)
    {
        await using var insert = db.CreateCommand("""
            insert into reservations (confirmation_number, driver_name, driver_age, license_number, license_state, car_class,
                branch, pickup_at, dropoff_at, days, daily_rate, young_driver_fee, total, status, authorization_id)
            values ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15)
            """);
        foreach (var value in new object[] { r.ConfirmationNumber, r.Driver.Name, r.Driver.Age, r.Driver.LicenseNumber, r.Driver.LicenseState,
            r.CarClass, r.Branch, r.PickupAt, r.DropoffAt, r.Price.Days, r.Price.DailyRate, r.Price.YoungDriverFee, r.Price.Total,
            r.Status, r.AuthorizationId })
        {
            insert.Parameters.Add(new() { Value = value });
        }
        await insert.ExecuteNonQueryAsync(ct);
    }

    public async Task<Reservation?> GetAsync(string number, CancellationToken ct)
    {
        await using var select = db.CreateCommand("""
            select confirmation_number, driver_name, driver_age, license_number, license_state, car_class, branch,
                   pickup_at, dropoff_at, days, daily_rate, young_driver_fee, total, status, authorization_id
            from reservations where confirmation_number = $1
            """);
        select.Parameters.Add(new() { Value = number });
        await using var row = await select.ExecuteReaderAsync(ct);
        if (!await row.ReadAsync(ct)) return null;
        return new Reservation(row.GetString(0), new Driver(row.GetString(1), row.GetInt32(2), row.GetString(3), row.GetString(4)),
            row.GetString(5), row.GetString(6), row.GetFieldValue<DateTimeOffset>(7), row.GetFieldValue<DateTimeOffset>(8),
            new Price(row.GetInt32(9), row.GetDecimal(10), row.GetDecimal(11), row.GetDecimal(12)), row.GetString(13), row.GetString(14));
    }

    /// <summary>Marks a confirmed reservation cancelled. False if it wasn't confirmed (already cancelled).</summary>
    public async Task<bool> CancelAsync(string number, DateTimeOffset now, CancellationToken ct)
    {
        await using var update = db.CreateCommand(
            "update reservations set status = 'cancelled', cancelled_at = $2 where confirmation_number = $1 and status = 'confirmed'");
        update.Parameters.Add(new() { Value = number });
        update.Parameters.Add(new() { Value = now });
        return await update.ExecuteNonQueryAsync(ct) == 1;
    }
}
