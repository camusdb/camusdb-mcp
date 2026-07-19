
/**
 * This file is part of CamusDB
 *
 * For the full copyright and license information, please view the LICENSE.txt
 * file that was distributed with this source code.
 */

using Xunit;

namespace CamusDB.Mcp.Tests;

/// <summary>
/// Binds <see cref="CamusServerFixture"/> to the "CamusServer" collection so every integration
/// test class shares a single connection and one startup reachability probe.
/// </summary>
[CollectionDefinition("CamusServer")]
public sealed class CamusServerCollection : ICollectionFixture<CamusServerFixture>;
