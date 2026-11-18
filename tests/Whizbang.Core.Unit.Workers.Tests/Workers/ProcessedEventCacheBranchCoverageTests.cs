// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// <see cref="ProcessedEventCache.AddRange"/> with <c>startTtl</c>: entries added that way start
/// Retained, so they age out after the retention window, while default entries stay InFlight and
/// never age out on their own.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/ProcessedEventCache.cs</code-under-test>
[Category("Workers")]
public class ProcessedEventCacheBranchCoverageTests {
  private static readonly TimeSpan _retention = TimeSpan.FromMinutes(5);

  [Test]
  public async Task AddRange_StartTtl_EntryExpiresAfterTheRetentionWindowAsync() {
    var time = new FakeTimeProvider(new DateTimeOffset(2026, 5, 2, 12, 0, 0, TimeSpan.Zero));
    var cache = new ProcessedEventCache(_retention, NullProcessedEventCacheObserver.Instance, time);
    var retained = Guid.CreateVersion7();
    var inFlight = Guid.CreateVersion7();

    cache.AddRange([retained], startTtl: true);
    cache.AddRange([inFlight]);

    time.Advance(_retention - TimeSpan.FromSeconds(1));
    await Assert.That(cache.Contains(retained)).IsTrue()
      .Because("inside its window a Retained entry still dedups");

    time.Advance(TimeSpan.FromSeconds(2));
    await Assert.That(cache.Contains(retained)).IsFalse()
      .Because("startTtl stamps the entry Retained at add time, so it ages out once the window has passed");
    await Assert.That(cache.Contains(inFlight)).IsTrue()
      .Because("a default entry is InFlight with no TTL and stays present however much time passes");
  }
}
