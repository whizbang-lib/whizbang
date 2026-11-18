// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Archive.AzureBlob.Tests;

/// <summary>
/// #2367: the orphan sweep deletes an object only once storage shows it was created at least the deletion delay ago. An
/// object storage reports no creation time for cannot be shown to have outlived the delay, so it is kept.
/// </summary>
/// <docs>operations/deployment/restoring-an-environment-with-an-archive#the-orphan-sweep</docs>
/// <code-under-test>src/Whizbang.Archive.AzureBlob/AzureBlobArchiveStore.cs</code-under-test>
public class AzureBlobArchiveOrphanAgeTests {
  private static readonly DateTimeOffset _cutoff = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

  [Test]
  public async Task AnObjectCreatedAtOrBeforeTheCutoff_IsPastTheDelayAsync() {
    await Assert.That(AzureBlobArchiveStore.IsPastDelay(_cutoff.AddDays(-1), _cutoff)).IsTrue();
    await Assert.That(AzureBlobArchiveStore.IsPastDelay(_cutoff, _cutoff)).IsTrue();
  }

  [Test]
  public async Task AnObjectCreatedAfterTheCutoff_IsNotPastTheDelayAsync() {
    await Assert.That(AzureBlobArchiveStore.IsPastDelay(_cutoff.AddSeconds(1), _cutoff)).IsFalse();
  }

  [Test]
  public async Task AnObjectWithNoCreationTime_IsNeverPastTheDelayAsync() {
    await Assert.That(AzureBlobArchiveStore.IsPastDelay(null, _cutoff)).IsFalse()
      .Because("the sweep cannot show such an object outlived the delay, so it keeps it");
  }
}
