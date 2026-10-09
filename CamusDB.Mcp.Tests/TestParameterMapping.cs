
/**
 * This file is part of CamusDB
 *
 * For the full copyright and license information, please view the LICENSE.txt
 * file that was distributed with this source code.
 */

using System.Text.Json;
using CamusDB.Client;
using CamusDB.Mcp.Client;
using Xunit;

namespace CamusDB.Mcp.Tests;

/// <summary>
/// Tests for the JSON → CamusDB parameter mapping used by <c>select_query</c> and
/// <c>insert_rows</c>. A number must never lose digits on the way to the server, and a JSON array
/// must bind as a typed array parameter.
/// </summary>
public sealed class TestParameterMapping
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Theory]
    [InlineData("\"hello\"", ColumnType.String)]
    [InlineData("true", ColumnType.Bool)]
    [InlineData("false", ColumnType.Bool)]
    [InlineData("null", ColumnType.Null)]
    [InlineData("42", ColumnType.Integer64)]
    [InlineData("-9223372036854775808", ColumnType.Integer64)]
    [InlineData("0.1", ColumnType.Float64)]
    [InlineData("1.5e10", ColumnType.Float64)]
    [InlineData("123456789012.345", ColumnType.Float64)]
    public void JsonElementToColumn_MapsScalars(string json, ColumnType expected)
    {
        (ColumnType type, _) = CamusClient.JsonElementToColumn(Parse(json));
        Assert.Equal(expected, type);
    }

    [Theory]
    [InlineData("12345678901234567890123")]
    [InlineData("12345678901234567890.123456789")]
    [InlineData("0.1234567890123456789")]
    public void JsonElementToColumn_BindsWideNumbersAsExactNumeric(string json)
    {
        (ColumnType type, object? value) = CamusClient.JsonElementToColumn(Parse(json));
        Assert.Equal(ColumnType.Numeric, type);
        Assert.Equal(json, value);
    }

    [Fact]
    public void JsonArrayToColumn_InfersElementType()
    {
        Assert.Equal(ColumnType.Integer64, CamusClient.JsonArrayToColumn("p", Parse("[1, 2, null]")).ElementType);
        Assert.Equal(ColumnType.Float64, CamusClient.JsonArrayToColumn("p", Parse("[1, 2.5]")).ElementType);
        Assert.Equal(ColumnType.String, CamusClient.JsonArrayToColumn("p", Parse("[\"a\", \"b\"]")).ElementType);
        Assert.Equal(ColumnType.Bool, CamusClient.JsonArrayToColumn("p", Parse("[true, false]")).ElementType);
        Assert.Equal(ColumnType.Null, CamusClient.JsonArrayToColumn("p", Parse("[]")).ElementType);
        Assert.Equal(ColumnType.String, CamusClient.JsonArrayToColumn("p", Parse("[null]")).ElementType);
    }

    [Fact]
    public void JsonArrayToColumn_ConvertsValues()
    {
        (_, object?[] floats) = CamusClient.JsonArrayToColumn("p", Parse("[1, 2.5, null]"));
        Assert.Equal([1.0, 2.5, null], floats);

        (_, object?[] longs) = CamusClient.JsonArrayToColumn("p", Parse("[1, 2]"));
        Assert.Equal([1L, 2L], longs);
    }

    [Theory]
    [InlineData("[1, \"a\"]")]
    [InlineData("[true, 1]")]
    [InlineData("[[1], [2]]")]
    [InlineData("[{\"a\": 1}]")]
    public void JsonArrayToColumn_RejectsMixedOrNestedElements(string json)
    {
        CamusException ex = Assert.Throws<CamusException>(() => CamusClient.JsonArrayToColumn("p", Parse(json)));
        Assert.Equal("CADB0400", ex.Code);
    }
}
