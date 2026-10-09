
/**
 * This file is part of CamusDB
 *
 * For the full copyright and license information, please view the LICENSE.txt
 * file that was distributed with this source code.
 */

using System.Text.RegularExpressions;
using CamusDB.Client;

namespace CamusDB.Mcp.Tools;

/// <summary>
/// Classifies a SQL statement by its leading keyword and enforces the statement-kind allow-list
/// before any execution. This is the security boundary of the MCP server: a caller must not be
/// able to smuggle a mutating statement through a read-only tool.
///
/// The classifier is self-contained (no SQL parser dependency). It first strips SQL comments —
/// both line comments (<c>-- … end-of-line</c>) and block comments (<c>/* … */</c>) — because
/// CamusDB supports comments and a leading comment must not hide the real first keyword. It then
/// reads the first identifier token and matches it case-insensitively against the allow-list. The
/// token is the maximal leading identifier, so <c>SELECTED</c> or <c>SHOWROOM</c> never
/// false-match <c>SELECT</c>/<c>SHOW</c>.
///
/// A leading keyword alone does not make a statement read-only: a <c>SELECT</c> with no
/// <c>FROM</c> may call <c>nextval</c> or <c>setval</c>, and both move a sequence counter (the
/// server authorizes them as writes). The read-only checks therefore also refuse any call to
/// either function.
/// </summary>
public static partial class StatementGuard
{
    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex BlockCommentRegex();

    [GeneratedRegex(@"--[^\r\n]*")]
    private static partial Regex LineCommentRegex();

    [GeneratedRegex(@"^[a-zA-Z_][a-zA-Z0-9_]*")]
    private static partial Regex LeadingKeywordRegex();

    // An optional quote or backtick around the name covers a quoted function name.
    [GeneratedRegex(@"(?<![a-zA-Z0-9_])[`""]?(nextval|setval)[`""]?\s*\(", RegexOptions.IgnoreCase)]
    private static partial Regex SequenceWriteCallRegex();

    /// <summary>
    /// Returns the name of a sequence-mutating function (<c>nextval</c> or <c>setval</c>) the
    /// statement calls, or null when it calls neither. The search runs over the raw text and over
    /// the comment-stripped text, and a match in either counts: the raw text catches a call that a
    /// comment-like string literal (<c>'/*'</c>) would make the stripper remove, and the stripped
    /// text catches a call split by a comment (<c>setval/**/(</c>). A literal that merely contains
    /// the text <c>nextval(</c> is a false positive, which is the safe direction.
    /// </summary>
    public static string? SequenceWriteCall(string sql)
    {
        if (string.IsNullOrEmpty(sql))
            return null;

        Match match = SequenceWriteCallRegex().Match(sql);
        if (!match.Success)
        {
            string stripped = LineCommentRegex().Replace(BlockCommentRegex().Replace(sql, " "), " ");
            match = SequenceWriteCallRegex().Match(stripped);
        }

        return match.Success ? match.Groups[1].Value.ToLowerInvariant() : null;
    }

    private static void AssertNoSequenceWrite(string sql, string tool)
    {
        if (SequenceWriteCall(sql) is { } function)
            throw new CamusException(
                "CADB0400",
                $"{tool} does not accept statements that call {function}(): it moves a sequence counter, so it is a write.");
    }

    /// <summary>
    /// Removes block and line comments, then returns the uppercased leading keyword of the
    /// statement (empty string when the statement has no leading identifier). Block comments are
    /// stripped before line comments so a <c>/* … */</c> spanning newlines cannot leave a dangling
    /// fragment that hides the keyword.
    /// </summary>
    public static string FirstKeyword(string sql)
    {
        if (sql is null)
            return "";

        string stripped = BlockCommentRegex().Replace(sql, " ");
        stripped = LineCommentRegex().Replace(stripped, " ");
        stripped = stripped.TrimStart();

        Match match = LeadingKeywordRegex().Match(stripped);
        return match.Success ? match.Value.ToUpperInvariant() : "";
    }

    /// <summary>
    /// Asserts the statement is read-only: its first keyword must be <c>SELECT</c>, <c>SHOW</c>, or
    /// <c>DESCRIBE</c>/<c>DESC</c> (the CamusDB aliases of <c>SHOW COLUMNS FROM</c>). Throws <see cref="CamusException"/> with code <c>CADB0400</c> for anything else (INSERT,
    /// UPDATE, DELETE, DROP, CREATE, ALTER, RENAME, EXPLAIN, …). Call before executing via
    /// <c>select_query</c> so no mutating SQL ever reaches the engine.
    /// </summary>
    public static void AssertReadOnly(string sql)
    {
        string keyword = FirstKeyword(sql);
        if (keyword is "SELECT" or "SHOW" or "DESCRIBE" or "DESC")
        {
            AssertNoSequenceWrite(sql, "select_query");
            return;
        }

        throw new CamusException(
            "CADB0400",
            $"select_query only accepts SELECT, SHOW and DESCRIBE statements; got '{(keyword.Length == 0 ? "<empty>" : keyword)}'. " +
            "Use the appropriate mutating tool instead.");
    }

    /// <summary>
    /// Asserts the statement is explainable: its first keyword must be <c>EXPLAIN</c> or a bare
    /// <c>SELECT</c> (which the tool prefixes with EXPLAIN). Throws <see cref="CamusException"/>
    /// with code <c>CADB0400</c> for mutating statements. Returns the uppercased first keyword so
    /// the caller can tell an already-EXPLAIN statement from a bare SELECT.
    /// </summary>
    public static string AssertExplain(string sql)
    {
        string keyword = FirstKeyword(sql);
        if (keyword is "EXPLAIN" or "SELECT")
        {
            AssertNoSequenceWrite(sql, "explain_query");
            return keyword;
        }

        throw new CamusException(
            "CADB0400",
            $"explain_query only accepts EXPLAIN statements or SELECT statements; got '{(keyword.Length == 0 ? "<empty>" : keyword)}'.");
    }
}
