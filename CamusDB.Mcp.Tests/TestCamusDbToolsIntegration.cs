
/**
 * This file is part of CamusDB
 *
 * For the full copyright and license information, please view the LICENSE.txt
 * file that was distributed with this source code.
 */

using System.Text.Json;
using CamusDB.Mcp.Client;
using CamusDB.Mcp.Tools;
using Xunit;

namespace CamusDB.Mcp.Tests;

/// <summary>
/// End-to-end tests that drive the real MCP tool methods against a live CamusDB instance.
///
/// These exercise the full round-trip through <c>CamusDB.Client</c> and the CamusDB server —
/// the layer the pure-logic unit tests (see <see cref="TestStatementGuard"/>) cannot reach. In
/// particular, <see cref="ListTables_ReturnsCreatedTable"/> is a regression guard for the
/// column-name mismatch bug where <c>SHOW TABLES</c> returns its result under the <c>tables</c>
/// column but the tool only looked for <c>Table</c>/<c>table</c>, silently yielding an empty list.
///
/// Each test that mutates schema uses a unique table name so the suite is safe to re-run against a
/// persistent database, and drops the table on the way out.
/// </summary>
[Collection("CamusServer")]
public sealed class TestCamusDbToolsIntegration
{
    private readonly CamusServerFixture _fx;

    public TestCamusDbToolsIntegration(CamusServerFixture fx) => _fx = fx;

    private CamusClient Camus => _fx.Client;
    private string Db => _fx.Database;

    private static string UniqueTableName() => "mcp_it_" + Guid.NewGuid().ToString("N")[..12];

    private static JsonElement Json<T>(T value) => JsonSerializer.SerializeToElement(value);

    private async Task DropTableAsync(string table)
    {
        try
        {
            await Camus.ExecuteNonQueryAsync(Db, $"DROP TABLE {table}", null, CancellationToken.None);
        }
        catch
        {
            // Best-effort cleanup; a failed drop must not mask the test's own assertion outcome.
        }
    }

    [SkippableFact]
    public async Task ListDatabases_IncludesTestDatabase()
    {
        _fx.SkipIfUnavailable();

        ListDatabasesResult result = await CamusDbTools.ListDatabases(Camus, CancellationToken.None);

        Assert.Contains(Db, result.Databases);
    }

    [SkippableFact]
    public async Task ListTables_ReturnsCreatedTable()
    {
        _fx.SkipIfUnavailable();

        string table = UniqueTableName();
        try
        {
            await CamusDbTools.CreateTable(
                Camus, Db, table,
                [
                    new ColumnDefinition { Name = "id", Type = "oid", Primary = true, Default = "gen_id()" },
                    new ColumnDefinition { Name = "name", Type = "string(100)" },
                ],
                ifNotExists: false,
                CancellationToken.None);

            ListTablesResult result = await CamusDbTools.ListTables(Camus, Db, CancellationToken.None);

            // Regression: before the fix this array was always empty because of the SHOW TABLES
            // column-name mismatch, regardless of how many tables the database actually had.
            Assert.Contains(table, result.Tables);
        }
        finally
        {
            await DropTableAsync(table);
        }
    }

    [SkippableFact]
    public async Task GetTableSchema_ReturnsDeclaredColumns()
    {
        _fx.SkipIfUnavailable();

        string table = UniqueTableName();
        try
        {
            await CamusDbTools.CreateTable(
                Camus, Db, table,
                [
                    new ColumnDefinition { Name = "id", Type = "oid", Primary = true, Default = "gen_id()" },
                    new ColumnDefinition { Name = "name", Type = "string(100)" },
                    new ColumnDefinition { Name = "age", Type = "int64" },
                ],
                ifNotExists: false,
                CancellationToken.None);

            GetTableSchemaResult schema = await CamusDbTools.GetTableSchema(Camus, Db, table, CancellationToken.None);

            // Every declared column should surface in the schema output (column-name key varies by
            // server version, so match against any string cell in each row).
            IEnumerable<string> cells = schema.Columns
                .SelectMany(row => row.Values)
                .Select(v => v?.ToString() ?? "");

            HashSet<string> present = new(cells, StringComparer.OrdinalIgnoreCase);
            Assert.Contains("id", present);
            Assert.Contains("name", present);
            Assert.Contains("age", present);
        }
        finally
        {
            await DropTableAsync(table);
        }
    }

    [SkippableFact]
    public async Task InsertRows_ThenSelect_RoundTripsData()
    {
        _fx.SkipIfUnavailable();

        string table = UniqueTableName();
        try
        {
            await CamusDbTools.CreateTable(
                Camus, Db, table,
                [
                    new ColumnDefinition { Name = "id", Type = "oid", Primary = true, Default = "gen_id()" },
                    new ColumnDefinition { Name = "name", Type = "string(100)" },
                    new ColumnDefinition { Name = "age", Type = "int64" },
                ],
                ifNotExists: false,
                CancellationToken.None);

            List<Dictionary<string, JsonElement>> rows =
            [
                new() { ["name"] = Json("Alice"), ["age"] = Json(30) },
                new() { ["name"] = Json("Bob"), ["age"] = Json(41) },
            ];

            InsertRowsResult inserted = await CamusDbTools.InsertRows(Camus, Db, table, rows, CancellationToken.None);
            Assert.Equal(2, inserted.Inserted);

            SelectQueryResult selected = await CamusDbTools.SelectQuery(
                Camus, _fx.Config, Db, $"SELECT name, age FROM {table}", null, null, CancellationToken.None);

            Assert.Equal(2, selected.RowCount);
            List<string> names = selected.Rows
                .Select(r => r.TryGetValue("name", out object? v) ? v?.ToString() ?? "" : "")
                .ToList();
            Assert.Contains("Alice", names);
            Assert.Contains("Bob", names);
        }
        finally
        {
            await DropTableAsync(table);
        }
    }

    [SkippableFact]
    public async Task ListIndexes_ReturnsPrimaryKeyIndex()
    {
        _fx.SkipIfUnavailable();

        string table = UniqueTableName();
        try
        {
            await CamusDbTools.CreateTable(
                Camus, Db, table,
                [
                    new ColumnDefinition { Name = "id", Type = "oid", Primary = true, Default = "gen_id()" },
                    new ColumnDefinition { Name = "email", Type = "string(200)", Index = true, Unique = true },
                ],
                ifNotExists: false,
                CancellationToken.None);

            ListIndexesResult result = await CamusDbTools.ListIndexes(Camus, Db, table, CancellationToken.None);

            // A table with a primary key and a unique secondary index has at least one readable index.
            Assert.NotEmpty(result.Indexes);
        }
        finally
        {
            await DropTableAsync(table);
        }
    }

    [SkippableFact]
    public async Task ExplainQuery_ReturnsPlan()
    {
        _fx.SkipIfUnavailable();

        string table = UniqueTableName();
        try
        {
            await CamusDbTools.CreateTable(
                Camus, Db, table,
                [new ColumnDefinition { Name = "id", Type = "oid", Primary = true, Default = "gen_id()" }],
                ifNotExists: false,
                CancellationToken.None);

            ExplainQueryResult plan = await CamusDbTools.ExplainQuery(
                Camus, Db, $"EXPLAIN SELECT * FROM {table}", null, CancellationToken.None);

            // The plan rows carry a column schema (stage/node/detail/estimated_rows/estimated_cost);
            // a physical scan of the table surfaces at least one node.
            Assert.NotEmpty(plan.Plan);

            IEnumerable<string> nodes = plan.Plan
                .SelectMany(row => row.Values)
                .Select(v => v?.ToString() ?? "");
            Assert.Contains(nodes, n => n.Contains("scan", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            await DropTableAsync(table);
        }
    }

    [SkippableFact]
    public async Task CreateTable_NonUniqueIndex_CreatesSecondaryIndex()
    {
        _fx.SkipIfUnavailable();

        string table = UniqueTableName();
        try
        {
            await CamusDbTools.CreateTable(
                Camus, Db, table,
                [
                    new ColumnDefinition { Name = "id", Type = "oid", Primary = true, Default = "gen_id()" },
                    new ColumnDefinition { Name = "city", Type = "string(50)", Index = true },
                ],
                ifNotExists: false,
                CancellationToken.None);

            ListIndexesResult result = await CamusDbTools.ListIndexes(Camus, Db, table, CancellationToken.None);

            IEnumerable<string> cells = result.Indexes.SelectMany(r => r.Values).Select(v => v?.ToString() ?? "");
            Assert.Contains($"ix_{table}_city", cells);
        }
        finally
        {
            await DropTableAsync(table);
        }
    }

    [SkippableFact]
    public async Task Numeric_RoundTripsExactly_IncludingValuesWiderThanDecimal()
    {
        _fx.SkipIfUnavailable();

        string table = UniqueTableName();
        try
        {
            await CamusDbTools.CreateTable(
                Camus, Db, table,
                [
                    new ColumnDefinition { Name = "id", Type = "int64", Primary = true },
                    new ColumnDefinition { Name = "amount", Type = "numeric" },
                ],
                ifNotExists: false,
                CancellationToken.None);

            // 38 significant digits: a System.Decimal cannot hold this value.
            const string wide = "12345678901234567890123456789.123456789";

            List<Dictionary<string, JsonElement>> rows =
            [
                new() { ["id"] = Json(1), ["amount"] = JsonDocument.Parse(wide).RootElement.Clone() },
                new() { ["id"] = Json(2), ["amount"] = Json("1.5") },
                new() { ["id"] = Json(3), ["amount"] = Json(42) },
            ];

            InsertRowsResult inserted = await CamusDbTools.InsertRows(Camus, Db, table, rows, CancellationToken.None);
            Assert.Equal(3, inserted.Inserted);

            SelectQueryResult selected = await CamusDbTools.SelectQuery(
                Camus, _fx.Config, Db, $"SELECT id, amount FROM {table} ORDER BY id", null, null, CancellationToken.None);

            Assert.Equal([wide, "1.5", "42"], selected.Rows.Select(r => r["amount"]).ToArray());
        }
        finally
        {
            await DropTableAsync(table);
        }
    }

    [SkippableFact]
    public async Task SelectQuery_BindsArrayParameter()
    {
        _fx.SkipIfUnavailable();

        string table = UniqueTableName();
        try
        {
            await CamusDbTools.CreateTable(
                Camus, Db, table,
                [
                    new ColumnDefinition { Name = "id", Type = "int64", Primary = true },
                    new ColumnDefinition { Name = "tags", Type = "array(string)" },
                ],
                ifNotExists: false,
                CancellationToken.None);

            List<Dictionary<string, JsonElement>> rows =
            [
                new() { ["id"] = Json(1), ["tags"] = Json(new[] { "a", "b" }) },
                new() { ["id"] = Json(2), ["tags"] = Json(Array.Empty<string>()) },
                new() { ["id"] = Json(3), ["tags"] = Json(new[] { "c" }) },
            ];
            await CamusDbTools.InsertRows(Camus, Db, table, rows, CancellationToken.None);

            SelectQueryResult selected = await CamusDbTools.SelectQuery(
                Camus, _fx.Config, Db, $"SELECT id, tags FROM {table} WHERE id = ANY(@ids) ORDER BY id",
                new Dictionary<string, JsonElement> { ["@ids"] = Json(new[] { 1, 3 }) }, null, CancellationToken.None);

            Assert.Equal([1L, 3L], selected.Rows.Select(r => r["id"]).ToArray());
            Assert.Equal(new object?[] { "a", "b" }, (object?[])selected.Rows[0]["tags"]!);
        }
        finally
        {
            await DropTableAsync(table);
        }
    }

    [SkippableFact]
    public async Task ShowCreate_ReturnsTableDdl()
    {
        _fx.SkipIfUnavailable();

        string table = UniqueTableName();
        try
        {
            await CamusDbTools.CreateTable(
                Camus, Db, table,
                [
                    new ColumnDefinition { Name = "id", Type = "oid", Primary = true, Default = "gen_id()" },
                    new ColumnDefinition { Name = "email", Type = "string(200)", Index = true },
                ],
                ifNotExists: false,
                CancellationToken.None);

            ShowCreateResult full = await CamusDbTools.ShowCreate(Camus, Db, table, null, false, CancellationToken.None);
            Assert.Equal(table, full.Name);
            Assert.Contains("CREATE TABLE", full.Ddl, StringComparison.OrdinalIgnoreCase);
            Assert.Contains($"ix_{table}_email", full.Ddl);

            ShowCreateResult bare = await CamusDbTools.ShowCreate(Camus, Db, table, "table", true, CancellationToken.None);
            Assert.DoesNotContain($"ix_{table}_email", bare.Ddl);
        }
        finally
        {
            await DropTableAsync(table);
        }
    }

    [SkippableFact]
    public async Task ListViews_AndListSequences_ReturnCreatedObjects()
    {
        _fx.SkipIfUnavailable();

        string table = UniqueTableName();
        string view = table + "_v";
        string sequence = table + "_seq";
        try
        {
            await CamusDbTools.CreateTable(
                Camus, Db, table,
                [new ColumnDefinition { Name = "id", Type = "int64", Primary = true }],
                ifNotExists: false,
                CancellationToken.None);
            await Camus.ExecuteNonQueryAsync(Db, $"CREATE VIEW {view} AS SELECT id FROM {table}", null, CancellationToken.None);
            await Camus.ExecuteNonQueryAsync(Db, $"CREATE SEQUENCE {sequence}", null, CancellationToken.None);

            ListViewsResult views = await CamusDbTools.ListViews(Camus, Db, CancellationToken.None);
            Assert.Contains(view, views.Views);

            ListSequencesResult sequences = await CamusDbTools.ListSequences(Camus, Db, CancellationToken.None);
            Assert.Contains(sequences.Sequences, r => r.TryGetValue("sequence", out object? v) && (v as string) == sequence);

            ShowCreateResult viewDdl = await CamusDbTools.ShowCreate(Camus, Db, view, "view", false, CancellationToken.None);
            Assert.Contains("CREATE VIEW", viewDdl.Ddl, StringComparison.OrdinalIgnoreCase);

            ShowCreateResult sequenceDdl = await CamusDbTools.ShowCreate(Camus, Db, sequence, "sequence", false, CancellationToken.None);
            Assert.Contains("CREATE SEQUENCE", sequenceDdl.Ddl, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { await Camus.ExecuteNonQueryAsync(Db, $"DROP VIEW {view}", null, CancellationToken.None); } catch { }
            try { await Camus.ExecuteNonQueryAsync(Db, $"DROP SEQUENCE {sequence}", null, CancellationToken.None); } catch { }
            await DropTableAsync(table);
        }
    }

    [SkippableFact]
    public async Task GetTableStatistics_AndDescribe_Succeed()
    {
        _fx.SkipIfUnavailable();

        string table = UniqueTableName();
        try
        {
            await CamusDbTools.CreateTable(
                Camus, Db, table,
                [new ColumnDefinition { Name = "id", Type = "int64", Primary = true }],
                ifNotExists: false,
                CancellationToken.None);

            GetTableStatisticsResult stats = await CamusDbTools.GetTableStatistics(Camus, Db, table, CancellationToken.None);
            Assert.NotNull(stats.Statistics);

            SelectQueryResult described = await CamusDbTools.SelectQuery(
                Camus, _fx.Config, Db, $"DESCRIBE {table}", null, null, CancellationToken.None);
            Assert.Contains(described.Rows, r => r.Values.Any(v => (v as string) == "id"));
        }
        finally
        {
            await DropTableAsync(table);
        }
    }

    [SkippableFact]
    public async Task ListBranches_AndListAncestors_SeeBranchDatabase()
    {
        _fx.SkipIfUnavailable();

        string branch = "mcp_br_" + Guid.NewGuid().ToString("N")[..12];
        try
        {
            await Camus.ExecuteNonQueryAsync(Db, $"CREATE DATABASE {branch} BRANCH FROM {Db}", null, CancellationToken.None);

            ListBranchesResult branches = await CamusDbTools.ListBranches(Camus, Db, CancellationToken.None);
            Dictionary<string, object?> row = Assert.Single(branches.Branches, r => (r["database"] as string) == branch);
            Assert.Equal(Db, row["parent"]);
            Assert.Equal(1L, row["depth"]);
            Assert.False(string.IsNullOrEmpty(row["forkTimestamp"] as string));

            ListAncestorsResult ancestors = await CamusDbTools.ListAncestors(Camus, branch, CancellationToken.None);
            Assert.Contains(ancestors.Ancestors, r => (r["database"] as string) == Db);
        }
        finally
        {
            try { await Camus.ExecuteNonQueryAsync(Db, $"DROP DATABASE {branch}", null, CancellationToken.None); } catch { }
        }
    }

    [SkippableFact]
    public async Task ListAncestors_OfRootDatabase_IsEmpty()
    {
        _fx.SkipIfUnavailable();

        ListAncestorsResult result = await CamusDbTools.ListAncestors(Camus, Db, CancellationToken.None);

        Assert.Empty(result.Ancestors);
    }
}
