// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Serialization;

namespace Whizbang.Core.Tests.Archival;

/// <summary>
/// #1760: the scheduled-close types are named for what they do, a prefix truncate, so "close" is left to closing the books.
/// A schedule stores its occurrence's type name, so the former name stays a pinned-type ledger alias: an occurrence stored
/// under it still loads, as the renamed type.
/// </summary>
/// <docs>fundamentals/events/ephemeral-events#scheduling-a-prefix-truncate</docs>
public class ScheduledPrefixTruncateRenameTests {
  private const string FORMER = "Whizbang.Core.Archival.ScheduledStreamClose, Whizbang.Core";
  private const string CURRENT = "Whizbang.Core.Archival.ScheduledPrefixTruncate, Whizbang.Core";

  [Test]
  public async Task TheOccurrenceType_IsNamedForThePrefixTruncate_AndItsFormerName_StillResolvesToItAsync() {
    var options = JsonContextRegistry.CreateCombinedOptions();

    var current = JsonContextRegistry.GetTypeInfoByName(CURRENT, options);
    var former = JsonContextRegistry.GetTypeInfoByName(FORMER, options);

    await Assert.That(current).IsNotNull().Because("the occurrence type is named for the prefix truncate it asks for");
    await Assert.That(former).IsNotNull().Because("a schedule stored before the rename names the former type, and must still load");
    await Assert.That(former!.Type).IsEqualTo(current!.Type);
  }
}
