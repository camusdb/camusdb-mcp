
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
}
