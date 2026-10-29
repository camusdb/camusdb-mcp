
/**
 * This file is part of CamusDB
 *
 * For the full copyright and license information, please view the LICENSE.txt
 * file that was distributed with this source code.
 */

using CamusDB.Client;
using CamusDB.Mcp.Tools;
using Xunit;

namespace CamusDB.Mcp.Tests;

/// <summary>
/// Tests for the SQL text that <c>insert_rows</c> composes from its <c>onConflict</c>,
/// <c>conflictColumns</c> and <c>returning</c> inputs, and for the literal quoting of the
/// diagnostic tools' LIKE patterns. All of this text is interpolated into a statement, so only
/// validated identifiers and quoted literals may reach it.
/// </summary>
public sealed class TestInsertOptions
{
    [Theory]
    [InlineData(null, "error")]
    [InlineData("", "error")]
    [InlineData("ERROR", "error")]
    [InlineData("do_nothing", "do_nothing")]
    [InlineData("Do Nothing", "do_nothing")]
    [InlineData("upsert", "upsert")]
    [InlineData(" update ", "upsert")]
    public void ParseConflictAction_NormalizesAcceptedValues(string? input, string expected)
    {
        Assert.Equal(expected, CamusDbTools.ParseConflictAction(input));
    }

    [Fact]
    public void ParseConflictAction_RejectsUnknownValue()
    {
        CamusException ex = Assert.Throws<CamusException>(() => CamusDbTools.ParseConflictAction("replace"));
        Assert.Equal("CADB0400", ex.Code);
    }

    [Fact]
    public void BuildConflictClause_ErrorAndPrimaryKeyUpsertHaveNoClause()
    {
        Assert.Equal("", CamusDbTools.BuildConflictClause("error", ["id", "name"], []));
        Assert.Equal("", CamusDbTools.BuildConflictClause("upsert", ["id", "name"], []));
    }

    [Fact]
    public void BuildConflictClause_DoNothing()
    {
        Assert.Equal(" ON CONFLICT DO NOTHING", CamusDbTools.BuildConflictClause("do_nothing", ["id", "email"], []));
        Assert.Equal(" ON CONFLICT (email) DO NOTHING", CamusDbTools.BuildConflictClause("do_nothing", ["id", "email"], ["email"]));
    }

    [Fact]
    public void BuildConflictClause_UpsertOnArbiterUpdatesTheOtherColumns()
    {
        Assert.Equal(
            " ON CONFLICT (EMAIL) DO UPDATE SET name = excluded.name, age = excluded.age",
            CamusDbTools.BuildConflictClause("upsert", ["email", "name", "age"], ["EMAIL"]));
        Assert.Equal(
            " ON CONFLICT (a, b) DO UPDATE SET c = excluded.c",
            CamusDbTools.BuildConflictClause("upsert", ["a", "b", "c"], ["a", "b"]));
    }

    [Fact]
    public void BuildConflictClause_UpsertWithOnlyArbiterColumnsDoesNothing()
    {
        Assert.Equal(" ON CONFLICT (email) DO NOTHING", CamusDbTools.BuildConflictClause("upsert", ["email"], ["email"]));
    }

    [Fact]
    public void BuildReturningClause_ComposesColumnsAndStar()
    {
        Assert.Equal("", CamusDbTools.BuildReturningClause(null));
        Assert.Equal("", CamusDbTools.BuildReturningClause([]));
        Assert.Equal(" RETURNING id, name", CamusDbTools.BuildReturningClause(["id", "name"]));
        Assert.Equal(" RETURNING *", CamusDbTools.BuildReturningClause(["*"]));
    }

    [Theory]
    [InlineData("id * 2")]
    [InlineData("nextval('s')")]
    [InlineData("id; DROP TABLE users")]
    [InlineData("t.*")]
    [InlineData("")]
    public void BuildReturningClause_RejectsExpressions(string item)
    {
        CamusException ex = Assert.Throws<CamusException>(() => CamusDbTools.BuildReturningClause([item]));
        Assert.Equal("CADB0400", ex.Code);
    }

    [Theory]
    [InlineData("%orders%", "'%orders%'")]
    [InlineData("it's", "'it''s'")]
    [InlineData("'; DROP TABLE users; --", "'''; DROP TABLE users; --'")]
    [InlineData(@"a\'b", @"'a\''b'")]
    public void QuoteLiteral_DoublesQuotes(string value, string expected)
    {
        Assert.Equal(expected, CamusDbTools.QuoteLiteral(value, "like pattern"));
    }

    [Theory]
    [InlineData("a\nb")]
    [InlineData("a\0b")]
    [InlineData("a\tb")]
    public void QuoteLiteral_RejectsControlCharacters(string value)
    {
        CamusException ex = Assert.Throws<CamusException>(() => CamusDbTools.QuoteLiteral(value, "like pattern"));
        Assert.Equal("CADB0400", ex.Code);
    }
}
