
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
/// Validates SQL identifiers (database, table, column names) before interpolating them into
/// composed SQL text. Parameters (values) are not interpolated — they go through the
/// CamusDB parameterized placeholder channel.
/// </summary>
public static partial class IdentifierValidator
{
    [GeneratedRegex(@"^[a-zA-Z_][a-zA-Z0-9_]*$")]
    private static partial Regex ValidIdentifierRegex();

    /// <summary>
    /// Throws <see cref="CamusException"/> if <paramref name="name"/> is not a valid SQL
    /// identifier. Valid: starts with a letter or underscore, followed by letters, digits, or
    /// underscores. This prevents injection through identifier positions.
    /// </summary>
    public static string Validate(string name, string role = "identifier")
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new CamusException("CADB0400", $"Empty {role} is not allowed.");
        if (!ValidIdentifierRegex().IsMatch(name))
            throw new CamusException("CADB0400",
                $"Invalid {role} '{name}': must match [a-zA-Z_][a-zA-Z0-9_]*.");
        return name;
    }

    /// <summary>Validates all items in <paramref name="names"/>.</summary>
    public static IReadOnlyList<string> ValidateAll(IEnumerable<string> names, string role = "identifier")
    {
        List<string> list = [];
        foreach (string n in names)
            list.Add(Validate(n, role));
        return list;
    }
}
