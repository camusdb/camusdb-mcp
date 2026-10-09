
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
/// Tests for the <c>create_table</c> column type check. The type text is interpolated into the
/// CREATE TABLE statement, so the check must accept every type the CamusDB grammar parses and
/// refuse anything that could carry more than a type.
/// </summary>
public sealed class TestColumnTypes
{
    [Theory]
    [InlineData("oid")]
    [InlineData("object_id")]
    [InlineData("int64")]
    [InlineData("INT")]
    [InlineData("smallint")]
    [InlineData("float64")]
    [InlineData("float32")]
    [InlineData("float")]
    [InlineData("real")]
    [InlineData("numeric")]
    [InlineData("DECIMAL")]
    [InlineData("string")]
    [InlineData("string(100)")]
    [InlineData("varchar(255)")]
    [InlineData("text")]
    [InlineData("char( 10 )")]
    [InlineData("bool")]
    [InlineData("boolean")]
    [InlineData("bytes")]
    [InlineData("bytes(16)")]
    [InlineData("blob")]
    [InlineData("date")]
    [InlineData("datetime")]
    [InlineData("timestamp")]
    [InlineData("uuid")]
    [InlineData("guid")]
    [InlineData("array(int64)")]
    [InlineData("array(string(20))")]
    [InlineData("ARRAY (numeric)")]
    [InlineData("serial")]
    [InlineData("bigserial")]
    public void ValidateSqlType_AcceptsGrammarTypes(string type)
    {
        CamusDbTools.ValidateSqlType(type);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("int32")]
    [InlineData("numeric(10, 2)")]
    [InlineData("array(array(int64))")]
    [InlineData("array(serial)")]
    [InlineData("int64) ; DROP TABLE users; --")]
    [InlineData("string(10) DEFAULT('x')")]
    [InlineData("int64\n")]
    public void ValidateSqlType_RejectsOtherText(string type)
    {
        CamusException ex = Assert.Throws<CamusException>(() => CamusDbTools.ValidateSqlType(type));
        Assert.Equal("CADB0400", ex.Code);
    }
}
