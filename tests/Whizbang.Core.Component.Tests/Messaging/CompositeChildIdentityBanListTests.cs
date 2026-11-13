// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Core.Component.Tests.Messaging;

/// <summary>
/// Reads the repository's ban list (a file on disk, so not a unit test) to lock that the raw composite-child derivation
/// and the versioned type-name renderings stay banned for library code (#2266, #2267).
/// </summary>
/// <docs>fundamentals/messaging/composite-events#deterministic-child-ids</docs>
[Category("Component")]
public class CompositeChildIdentityBanListTests {
  /// <summary>
  /// The guard (#2267): library code cannot call <see cref="CompositeChildIdentity.Derive"/>, nor render a type's versioned
  /// assembly-qualified name, because both are banned for it at compile time (RS0030). This reads the ban list the
  /// production projects compile against, so removing an entry fails here before a second rendering can creep back.
  /// </summary>
  [Test]
  public async Task TheBanList_ForbidsTheRawDerivation_AndTheVersionedTypeNamesAsync() {
    var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "BannedSymbols.txt"));
    var banned = (await File.ReadAllLinesAsync(path)).Select(l => l.Split(';')[0].Trim()).ToHashSet(StringComparer.Ordinal);

    await Assert.That(banned).Contains("M:Whizbang.Core.Messaging.CompositeChildIdentity.Derive(System.Guid,System.Int32,System.String)");
    await Assert.That(banned).Contains("M:Whizbang.Core.TypeNameFormatter.AssemblyQualifiedName(System.Type)");
    await Assert.That(banned).Contains("M:Whizbang.Core.TypeNameFormatter.AssemblyQualifiedNameOrNull(System.Type)");
    await Assert.That(banned).Contains("M:Whizbang.Core.TypeNameFormatter.AssemblyQualifiedNameOrDisplay(System.Type)");
  }
}
