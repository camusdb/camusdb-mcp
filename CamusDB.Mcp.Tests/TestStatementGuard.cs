
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
    public void AssertReadOnly_RejectsInsert()
    {
        CamusException ex = Assert.Throws<CamusException>(() =>
            StatementGuard.AssertReadOnly("INSERT INTO users (name) VALUES ('Alice')"));
        Assert.Contains("select_query only accepts SELECT and SHOW", ex.Message);
    }

    [Fact]
    public void AssertReadOnly_RejectsUpdate()
    {
        CamusException ex = Assert.Throws<CamusException>(() =>
            StatementGuard.AssertReadOnly("UPDATE users SET name = 'Bob' WHERE id = '1'"));
        Assert.Contains("select_query only accepts SELECT and SHOW", ex.Message);
    }

    [Fact]
    public void AssertReadOnly_RejectsDelete()
    {
        CamusException ex = Assert.Throws<CamusException>(() =>
            StatementGuard.AssertReadOnly("DELETE FROM users WHERE id = '1'"));
        Assert.Contains("select_query only accepts SELECT and SHOW", ex.Message);
    }

    [Fact]
    public void AssertReadOnly_RejectsDropTable()
    {
        CamusException ex = Assert.Throws<CamusException>(() =>
            StatementGuard.AssertReadOnly("DROP TABLE users"));
        Assert.Contains("select_query only accepts SELECT and SHOW", ex.Message);
    }

    [Fact]
    public void AssertReadOnly_RejectsCreateTable()
    {
        CamusException ex = Assert.Throws<CamusException>(() =>
            StatementGuard.AssertReadOnly("CREATE TABLE foo (id oid PRIMARY KEY)"));
        Assert.Contains("select_query only accepts SELECT and SHOW", ex.Message);
    }

    [Fact]
    public void AssertReadOnly_RejectsCreateDatabase()
    {
        CamusException ex = Assert.Throws<CamusException>(() =>
            StatementGuard.AssertReadOnly("CREATE DATABASE mydb"));
        Assert.Contains("select_query only accepts SELECT and SHOW", ex.Message);
    }

    // -------------------------------------------------------------------------
    // Comment-smuggling: a leading comment must not hide the real first keyword
    // -------------------------------------------------------------------------

    [Fact]
    public void AssertReadOnly_RejectsInsertHiddenBehindBlockComment()
    {
        CamusException ex = Assert.Throws<CamusException>(() =>
            StatementGuard.AssertReadOnly("/* SELECT */ INSERT INTO users (name) VALUES ('x')"));
        Assert.Contains("select_query only accepts SELECT and SHOW", ex.Message);
    }

    [Fact]
    public void AssertReadOnly_RejectsInsertHiddenBehindLineComment()
    {
        CamusException ex = Assert.Throws<CamusException>(() =>
            StatementGuard.AssertReadOnly("-- SELECT everything\nINSERT INTO users (name) VALUES ('x')"));
        Assert.Contains("select_query only accepts SELECT and SHOW", ex.Message);
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
        Assert.Contains("select_query only accepts SELECT and SHOW", ex.Message);
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
