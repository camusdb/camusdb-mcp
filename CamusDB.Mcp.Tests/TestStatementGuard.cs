
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
/// Tests for <see cref="StatementGuard"/> and <see cref="IdentifierValidator"/>.
/// These are security-critical: they enforce that read tools cannot execute mutating SQL
/// and that identifier injection is blocked before SQL composition. The guard is a
/// comment-stripping first-keyword classifier (no SQL parser), so comment-smuggling cases are
/// covered explicitly.
/// </summary>
public sealed class TestStatementGuard
{
    // -------------------------------------------------------------------------
    // select_query guard
    // -------------------------------------------------------------------------

    [Fact]
    public void AssertReadOnly_AllowsSelect()
    {
        StatementGuard.AssertReadOnly("SELECT 1");
        StatementGuard.AssertReadOnly("SELECT * FROM users");
        StatementGuard.AssertReadOnly("SELECT id, name FROM orders WHERE id = @id");
    }

    [Fact]
    public void AssertReadOnly_AllowsShowStatements()
    {
        StatementGuard.AssertReadOnly("SHOW DATABASES");
        StatementGuard.AssertReadOnly("SHOW TABLES");
        StatementGuard.AssertReadOnly("SHOW COLUMNS FROM users");
        StatementGuard.AssertReadOnly("SHOW INDEXES FROM users");
    }

    [Fact]
    public void AssertReadOnly_AllowsDescribe()
    {
        StatementGuard.AssertReadOnly("DESCRIBE users");
        StatementGuard.AssertReadOnly("desc users");
        StatementGuard.AssertReadOnly("SHOW CREATE TABLE users");
        StatementGuard.AssertReadOnly("SHOW STATISTICS FOR TABLE users");
    }

    [Theory]
    [InlineData("SELECT nextval('order_seq')")]
    [InlineData("SELECT setval('order_seq', 1)")]
    [InlineData("select NEXTVAL ('order_seq')")]
    [InlineData("SELECT \"setval\"('order_seq', 1)")]
    [InlineData("SELECT setval/**/('order_seq', 1)")]
    [InlineData("SELECT '/*', setval('order_seq', 1), '*/'")]
    public void AssertReadOnly_RejectsSequenceWrites(string sql)
    {
        CamusException ex = Assert.Throws<CamusException>(() => StatementGuard.AssertReadOnly(sql));
        Assert.Equal("CADB0400", ex.Code);
    }

    [Fact]
    public void AssertReadOnly_AllowsSequenceReadsAndLookalikeNames()
    {
        StatementGuard.AssertReadOnly("SELECT currval('order_seq')");
        StatementGuard.AssertReadOnly("SELECT lastval()");
        StatementGuard.AssertReadOnly("SELECT my_nextval(1) FROM t");
        StatementGuard.AssertReadOnly("SELECT nextval_count FROM t");
    }

    [Fact]
    public void AssertReadOnly_AllowsSetOperations()
    {
        StatementGuard.AssertReadOnly("SELECT a FROM t UNION ALL SELECT b FROM u");
        StatementGuard.AssertReadOnly("(SELECT a FROM t ORDER BY a LIMIT 5) UNION ALL SELECT b FROM u ORDER BY 1");
        StatementGuard.AssertReadOnly("/* both */ ( ( SELECT a FROM t) INTERSECT SELECT b FROM u)");
    }

    [Fact]
    public void AssertReadOnly_RejectsWriteBehindParenthesis()
    {
        CamusException ex = Assert.Throws<CamusException>(() =>
            StatementGuard.AssertReadOnly("(DELETE FROM users)"));
        Assert.Contains("select_query only accepts SELECT, SHOW and DESCRIBE", ex.Message);
    }

    [Theory]
    [InlineData("SELECT id FROM jobs WHERE state = 'ready' FOR UPDATE")]
    [InlineData("SELECT id FROM jobs LIMIT 10 FOR UPDATE SKIP LOCKED")]
    [InlineData("select id from jobs for no key update nowait")]
    [InlineData("SELECT id FROM jobs FOR UPDATE OF jobs")]
    [InlineData("SELECT id FROM jobs FOR SHARE")]
    [InlineData("SELECT id FROM jobs FOR KEY SHARE")]
    [InlineData("SELECT id FROM jobs FOR/**/UPDATE")]
    [InlineData("SELECT id FROM jobs FOR\n  UPDATE")]
    public void AssertReadOnly_RejectsLockingReads(string sql)
    {
        CamusException ex = Assert.Throws<CamusException>(() => StatementGuard.AssertReadOnly(sql));
        Assert.Equal("CADB0400", ex.Code);
        Assert.Contains("row-locking", ex.Message);
    }

    [Fact]
    public void AssertReadOnly_AllowsLookalikeLockingWords()
    {
        StatementGuard.AssertReadOnly("SELECT for_update FROM t");
        StatementGuard.AssertReadOnly("SELECT updated FROM t");
        StatementGuard.AssertReadOnly("SHOW STATISTICS FOR TABLE users");
        StatementGuard.AssertReadOnly("SHOW GRANTS FOR alice");
    }

    [Fact]
    public void AssertExplain_AllowsLockingReadInPlanModes()
    {
        Assert.Equal("SELECT", StatementGuard.AssertExplain("SELECT id FROM jobs FOR UPDATE"));
        Assert.Equal("SELECT", StatementGuard.AssertExplain("SELECT id FROM jobs FOR UPDATE", "physical"));
        Assert.Equal("EXPLAIN", StatementGuard.AssertExplain("EXPLAIN SELECT id FROM jobs FOR UPDATE SKIP LOCKED"));
        Assert.Equal("EXPLAIN", StatementGuard.AssertExplain("EXPLAIN (LOGICAL) SELECT id FROM jobs FOR UPDATE"));
    }

    [Theory]
    [InlineData("SELECT id FROM jobs FOR UPDATE", "analyze")]
    [InlineData("SELECT id FROM jobs FOR UPDATE", "ANALYZE")]
    [InlineData("EXPLAIN (ANALYZE) SELECT id FROM jobs FOR UPDATE", null)]
    [InlineData("explain ( analyze ) select id from jobs for update nowait", "plan")]
    [InlineData("EXPLAIN (/**/ANALYZE) SELECT id FROM jobs FOR UPDATE", null)]
    public void AssertExplain_RejectsLockingReadUnderAnalyze(string sql, string? mode)
    {
        CamusException ex = Assert.Throws<CamusException>(() => StatementGuard.AssertExplain(sql, mode));
        Assert.Equal("CADB0400", ex.Code);
        Assert.Contains("row-locking", ex.Message);
    }

    [Fact]
    public void AssertExplain_AllowsAnalyzeOfPlainSelect()
    {
        Assert.Equal("SELECT", StatementGuard.AssertExplain("SELECT id FROM jobs", "analyze"));
        Assert.Equal("EXPLAIN", StatementGuard.AssertExplain("EXPLAIN (ANALYZE) SELECT id FROM jobs"));
    }

    [Fact]
    public void AssertExplain_RejectsSequenceWrites()
    {
        Assert.Throws<CamusException>(() => StatementGuard.AssertExplain("EXPLAIN SELECT nextval('order_seq')"));
    }

    [Fact]
    public void AssertReadOnly_RejectsInsert()
    {
        CamusException ex = Assert.Throws<CamusException>(() =>
            StatementGuard.AssertReadOnly("INSERT INTO users (name) VALUES ('Alice')"));
        Assert.Contains("select_query only accepts SELECT, SHOW and DESCRIBE", ex.Message);
    }

    [Fact]
    public void AssertReadOnly_RejectsUpdate()
    {
        CamusException ex = Assert.Throws<CamusException>(() =>
            StatementGuard.AssertReadOnly("UPDATE users SET name = 'Bob' WHERE id = '1'"));
        Assert.Contains("select_query only accepts SELECT, SHOW and DESCRIBE", ex.Message);
    }

    [Fact]
    public void AssertReadOnly_RejectsDelete()
    {
        CamusException ex = Assert.Throws<CamusException>(() =>
            StatementGuard.AssertReadOnly("DELETE FROM users WHERE id = '1'"));
        Assert.Contains("select_query only accepts SELECT, SHOW and DESCRIBE", ex.Message);
    }

    [Fact]
    public void AssertReadOnly_RejectsDropTable()
    {
        CamusException ex = Assert.Throws<CamusException>(() =>
            StatementGuard.AssertReadOnly("DROP TABLE users"));
        Assert.Contains("select_query only accepts SELECT, SHOW and DESCRIBE", ex.Message);
    }

    [Fact]
    public void AssertReadOnly_RejectsCreateTable()
    {
        CamusException ex = Assert.Throws<CamusException>(() =>
            StatementGuard.AssertReadOnly("CREATE TABLE foo (id oid PRIMARY KEY)"));
        Assert.Contains("select_query only accepts SELECT, SHOW and DESCRIBE", ex.Message);
    }

    [Fact]
    public void AssertReadOnly_RejectsCreateDatabase()
    {
        CamusException ex = Assert.Throws<CamusException>(() =>
            StatementGuard.AssertReadOnly("CREATE DATABASE mydb"));
        Assert.Contains("select_query only accepts SELECT, SHOW and DESCRIBE", ex.Message);
    }

    // -------------------------------------------------------------------------
    // Comment-smuggling: a leading comment must not hide the real first keyword
    // -------------------------------------------------------------------------

    [Fact]
    public void AssertReadOnly_RejectsInsertHiddenBehindBlockComment()
    {
        CamusException ex = Assert.Throws<CamusException>(() =>
            StatementGuard.AssertReadOnly("/* SELECT */ INSERT INTO users (name) VALUES ('x')"));
        Assert.Contains("select_query only accepts SELECT, SHOW and DESCRIBE", ex.Message);
    }

    [Fact]
    public void AssertReadOnly_RejectsInsertHiddenBehindLineComment()
    {
        CamusException ex = Assert.Throws<CamusException>(() =>
            StatementGuard.AssertReadOnly("-- SELECT everything\nINSERT INTO users (name) VALUES ('x')"));
        Assert.Contains("select_query only accepts SELECT, SHOW and DESCRIBE", ex.Message);
    }

    [Fact]
    public void AssertReadOnly_AllowsSelectAfterLeadingComments()
    {
        // A genuine SELECT preceded by comments must still be accepted.
        StatementGuard.AssertReadOnly("-- fetch users\nSELECT * FROM users");
        StatementGuard.AssertReadOnly("/* fetch users */ SELECT * FROM users");
    }

    [Fact]
    public void AssertReadOnly_DoesNotFalseMatchSelectPrefixedIdentifier()
    {
        // "SELECTED" is not "SELECT": the maximal leading identifier must be matched whole.
        CamusException ex = Assert.Throws<CamusException>(() =>
            StatementGuard.AssertReadOnly("SELECTED FROM users"));
        Assert.Contains("select_query only accepts SELECT, SHOW and DESCRIBE", ex.Message);
    }

    // -------------------------------------------------------------------------
    // explain_query guard
    // -------------------------------------------------------------------------

    [Fact]
    public void AssertExplain_AllowsExplainForms()
    {
        Assert.Equal("EXPLAIN", StatementGuard.AssertExplain("EXPLAIN SELECT * FROM users"));
        // LOGICAL and PHYSICAL use parenthesized syntax per the CamusDB grammar
        Assert.Equal("EXPLAIN", StatementGuard.AssertExplain("EXPLAIN (LOGICAL) SELECT * FROM users"));
        Assert.Equal("EXPLAIN", StatementGuard.AssertExplain("EXPLAIN (PHYSICAL) SELECT * FROM users"));
    }

    [Fact]
    public void AssertExplain_AllowsBareSelect()
    {
        Assert.Equal("SELECT", StatementGuard.AssertExplain("SELECT * FROM users"));
    }

    [Fact]
    public void AssertExplain_RejectsInsert()
    {
        CamusException ex = Assert.Throws<CamusException>(() =>
            StatementGuard.AssertExplain("INSERT INTO users (name) VALUES ('x')"));
        Assert.Contains("explain_query only accepts EXPLAIN statements", ex.Message);
    }

    [Fact]
    public void AssertExplain_RejectsDropTable()
    {
        CamusException ex = Assert.Throws<CamusException>(() =>
            StatementGuard.AssertExplain("DROP TABLE users"));
        Assert.Contains("explain_query only accepts EXPLAIN statements", ex.Message);
    }

    [Fact]
    public void AssertExplain_RejectsInsertHiddenBehindBlockComment()
    {
        CamusException ex = Assert.Throws<CamusException>(() =>
            StatementGuard.AssertExplain("/* EXPLAIN */ INSERT INTO users (name) VALUES ('x')"));
        Assert.Contains("explain_query only accepts EXPLAIN statements", ex.Message);
    }

    // -------------------------------------------------------------------------
    // IdentifierValidator
    // -------------------------------------------------------------------------

    [Fact]
    public void IdentifierValidator_AcceptsValidNames()
    {
        IdentifierValidator.Validate("users");
        IdentifierValidator.Validate("_private");
        IdentifierValidator.Validate("MyTable123");
        IdentifierValidator.Validate("col_name");
    }

    [Fact]
    public void IdentifierValidator_RejectsInvalidNames()
    {
        Assert.Throws<CamusException>(() => IdentifierValidator.Validate(""));
        Assert.Throws<CamusException>(() => IdentifierValidator.Validate("   "));
        Assert.Throws<CamusException>(() => IdentifierValidator.Validate("1invalid"));
        Assert.Throws<CamusException>(() => IdentifierValidator.Validate("drop table;--"));
        Assert.Throws<CamusException>(() => IdentifierValidator.Validate("a b"));
        Assert.Throws<CamusException>(() => IdentifierValidator.Validate("a.b"));
        Assert.Throws<CamusException>(() => IdentifierValidator.Validate("'; DROP TABLE users; --"));
    }

    [Fact]
    public void IdentifierValidator_RejectsInjectionAttempts()
    {
        // Classic SQL injection attempts that should never reach the SQL string
        Assert.Throws<CamusException>(() => IdentifierValidator.Validate("users; DROP TABLE users; --"));
        Assert.Throws<CamusException>(() => IdentifierValidator.Validate("users' OR '1'='1"));
        Assert.Throws<CamusException>(() => IdentifierValidator.Validate("users--"));
    }
}
