using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace RealEstate.Infrastructure;

/// <summary>Jedna SQL migrace: název souboru v <c>scripts/</c> a jeho obsah.</summary>
public sealed record SqlMigration(string Name, string Sql)
{
    public string Checksum => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Sql))).ToLowerInvariant();
}

/// <summary>
/// Verzované SQL migrace ze složky <c>scripts/migrate_*.sql</c> (vložené do sestavení jako zdroje).
/// Schéma do té doby drželo pohromadě <c>EnsureCreatedAsync</c> (jen prázdná databáze), ruční
/// <c>make db</c> a idempotentní SQL opsané do <c>DbInitializer</c> – tři místa, která se rozcházela.
/// Nově: každý nový soubor ve <c>scripts/</c> se při startu API spustí právě jednou a zapíše do
/// <c>re_realestate.schema_migrations</c>. Databáze, která už existovala před zavedením tabulky,
/// dostane všechny tehdejší soubory jako „baseline" (zapsané, nespuštěné) – jsou to z velké části
/// jednorázové opravy dat a na produkci už proběhly ručně.
/// </summary>
public static class SqlMigrationRunner
{
    public const string ResourcePrefix = "migrations/";
    private const string Table = "re_realestate.schema_migrations";

    public static async Task<int> ApplyAsync(DbContext db, ILogger? logger, CancellationToken ct = default)
    {
        var available = LoadEmbedded(typeof(SqlMigrationRunner).Assembly);
        if (available.Count == 0)
        {
            logger?.LogWarning("Žádné vložené SQL migrace (scripts/migrate_*.sql) – přeskakuji");
            return 0;
        }

        await db.Database.ExecuteSqlRawAsync($"""
            CREATE TABLE IF NOT EXISTS {Table} (
                name        text PRIMARY KEY,
                checksum    text NOT NULL,
                applied_at  timestamptz NOT NULL DEFAULT now(),
                baseline    boolean NOT NULL DEFAULT false
            );
            """, ct);

        var applied = await db.Database.SqlQueryRaw<string>($"SELECT name AS \"Value\" FROM {Table}").ToListAsync(ct);
        var schemaExists = await db.Database
            .SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM information_schema.tables WHERE table_schema = 're_realestate' AND table_name = 'listings'")
            .FirstAsync(ct) > 0;

        var (baseline, toRun) = Plan(available, applied, schemaExists);

        foreach (var migration in baseline)
        {
            await db.Database.ExecuteSqlRawAsync(
                $"INSERT INTO {Table} (name, checksum, baseline) VALUES ({{0}}, {{1}}, true) ON CONFLICT (name) DO NOTHING",
                [migration.Name, migration.Checksum], ct);
        }
        if (baseline.Count > 0)
            logger?.LogInformation("Schema migrations: {Count} souborů zapsáno jako baseline (existující databáze)", baseline.Count);

        foreach (var migration in toRun)
        {
            logger?.LogInformation("Schema migration {Name}: spouštím", migration.Name);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            // Skript jde přímo přes DbCommand: ExecuteSqlRaw čte složené závorky jako zástupné symboly
            // parametrů, takže jsonb cesta '{has_pool}' nebo regexový kvantifikátor {0,60} shodily start API
            // („Expected an ASCII digit", 8. 10. 2026).
            await using (var command = db.Database.GetDbConnection().CreateCommand())
            {
                command.CommandText = migration.Sql;
                command.CommandTimeout = 600;
                command.Transaction = tx.GetDbTransaction();
                await command.ExecuteNonQueryAsync(ct);
            }
            await db.Database.ExecuteSqlRawAsync(
                $"INSERT INTO {Table} (name, checksum) VALUES ({{0}}, {{1}})",
                [migration.Name, migration.Checksum], ct);
            await tx.CommitAsync(ct);
            logger?.LogInformation("Schema migration {Name}: hotovo", migration.Name);
        }
        return toRun.Count;
    }

    /// <summary>
    /// Co zapsat jako baseline a co spustit. Prázdná tabulka nad existujícím schématem = první běh
    /// na staré databázi: všechno dosavadní je baseline. Na prázdné databázi (schéma právě vytvořil
    /// <c>EnsureCreated</c>) se spustí všechno. Jinak jen soubory, které v tabulce chybí.
    /// </summary>
    public static (List<SqlMigration> Baseline, List<SqlMigration> ToRun) Plan(
        IReadOnlyList<SqlMigration> available, IReadOnlyCollection<string> applied, bool schemaExists)
    {
        var ordered = available.OrderBy(m => m.Name, StringComparer.Ordinal).ToList();
        if (applied.Count == 0 && schemaExists)
            return (ordered, []);

        var appliedSet = applied.ToHashSet(StringComparer.Ordinal);
        return ([], ordered.Where(m => !appliedSet.Contains(m.Name)).ToList());
    }

    public static List<SqlMigration> LoadEmbedded(Assembly assembly)
    {
        var migrations = new List<SqlMigration>();
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            if (!resource.StartsWith(ResourcePrefix, StringComparison.Ordinal) || !resource.EndsWith(".sql", StringComparison.Ordinal))
                continue;
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            migrations.Add(new SqlMigration(resource[ResourcePrefix.Length..], reader.ReadToEnd()));
        }
        return migrations.OrderBy(m => m.Name, StringComparer.Ordinal).ToList();
    }
}
