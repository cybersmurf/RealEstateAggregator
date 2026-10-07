using RealEstate.Infrastructure;

namespace RealEstate.Tests;

// Verzované SQL migrace ze scripts/: na staré databázi baseline, na nové všechno, jinak jen chybějící.
public class SqlMigrationRunnerTests
{
    private static readonly SqlMigration[] Files =
    [
        new("migrate_seller_contact.sql", "ALTER TABLE x ADD COLUMN IF NOT EXISTS seller_name text;"),
        new("migrate_20261007_house_position_index.sql", "CREATE INDEX IF NOT EXISTS ix ON x(house_position);"),
        new("migrate_districts.sql", "INSERT INTO districts VALUES (1);"),
    ];

    [Fact]
    public void Plan_ExistingDatabaseWithoutTable_MarksEverythingAsBaseline()
    {
        var (baseline, toRun) = SqlMigrationRunner.Plan(Files, [], schemaExists: true);

        Assert.Equal(3, baseline.Count);
        Assert.Empty(toRun);
    }

    [Fact]
    public void Plan_EmptyDatabase_RunsEverythingInNameOrder()
    {
        var (baseline, toRun) = SqlMigrationRunner.Plan(Files, [], schemaExists: false);

        Assert.Empty(baseline);
        Assert.Equal(["migrate_20261007_house_position_index.sql", "migrate_districts.sql", "migrate_seller_contact.sql"], toRun.Select(m => m.Name));
    }

    [Fact]
    public void Plan_LaterRun_RunsOnlyMissingFiles()
    {
        var applied = new[] { "migrate_districts.sql", "migrate_seller_contact.sql" };

        var (baseline, toRun) = SqlMigrationRunner.Plan(Files, applied, schemaExists: true);

        Assert.Empty(baseline);
        Assert.Equal(["migrate_20261007_house_position_index.sql"], toRun.Select(m => m.Name));
    }

    [Fact]
    public void Checksum_DependsOnContentOnly()
    {
        Assert.Equal(new SqlMigration("a.sql", "SELECT 1;").Checksum, new SqlMigration("b.sql", "SELECT 1;").Checksum);
        Assert.NotEqual(new SqlMigration("a.sql", "SELECT 1;").Checksum, new SqlMigration("a.sql", "SELECT 2;").Checksum);
    }

    [Fact]
    public void LoadEmbedded_FindsTheScriptsFolder()
    {
        var migrations = SqlMigrationRunner.LoadEmbedded(typeof(SqlMigrationRunner).Assembly);

        Assert.Contains(migrations, m => m.Name == "migrate_house_position.sql");
        Assert.All(migrations, m => Assert.False(string.IsNullOrWhiteSpace(m.Sql)));
    }
}
