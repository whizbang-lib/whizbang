// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Generated;
using Whizbang.Core.Minting;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Keeps <see cref="StreamResolutionParityTests"/> complete (#1798): every composite type the framework defines must
/// have a row in the parity table. The list is derived by machine (reflection over the framework assembly) and checked
/// against an independent one (the compile-time message catalog's composite stamp), so neither can silently come up
/// short: a new composite type fails here until the table covers it.
/// </summary>
/// <remarks>
/// The source-level half, every site that reads the stream hop or builds an inbox or outbox row, is
/// <c>StreamResolutionSourceGuardTests</c> in the component suite (it reads the source tree).
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Messaging/StreamIdentity.cs</code-under-test>
[Category("Messaging")]
public class StreamResolutionSitesTests {
  private static IReadOnlyList<Type> _compositeTypesByReflection() =>
    [.. typeof(ICompositeEvent).Assembly.GetTypes()
      .Where(t => typeof(ICompositeEvent).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false })
      .OrderBy(t => t.FullName, StringComparer.Ordinal)];

  /// <summary>The two enumerations of the framework's composite types agree, and neither is empty.</summary>
  [Test]
  public async Task CompositeTypes_ReflectionAgreesWithTheCompileTimeCatalogAsync() {
    var byReflection = _compositeTypesByReflection();
    var byCatalog = new GeneratedMessageTypeCatalog().GetAll()
      .Where(e => e.IsComposite)
      .Select(e => e.Type)
      .OrderBy(t => t.FullName, StringComparer.Ordinal)
      .ToList();

    // Compared by name: an equivalence assertion over Type instances walks their members.
    var reflected = byReflection.Select(t => t.FullName!).ToList();
    await Assert.That(reflected).Contains(typeof(RedeliveryComposite).FullName!)
      .Because("the enumeration must find at least the composites known to exist, or it is looking in the wrong place");
    await Assert.That(reflected).IsEquivalentTo(byCatalog.ConvertAll(t => t.FullName!))
      .Because("reflection and the generator's composite stamp are independent; a type one of them misses is a gap in both");
  }

  /// <summary>Every composite type the framework defines has a row in the parity table.</summary>
  [Test]
  public async Task CompositeTypes_EachHasARowInTheParityTableAsync() {
    var uncovered = _compositeTypesByReflection().Where(t => !StreamResolutionParityTests.Covered.Contains(t)).Select(t => t.FullName!).ToList();

    await Assert.That(uncovered).IsEmpty()
      .Because("a composite files its children under some stream; add a row for it to StreamResolutionParityTests that "
             + "asserts the receiver stores each child where its producer stored it");
  }
}
