// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

#pragma warning disable CA1707

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Tests.Workers;

namespace Whizbang.Core.Tests.Lifecycle;

/// <summary>
/// Fold-before-discard's ordering lock: the close is about to truncate a stream's pointers, so its
/// apply path must fold into the persisted signatures STRICTLY BEFORE the truncate — fold-after
/// would fold a path the close already destroyed. The stream dies; its shape survives.
/// </summary>
/// <docs>operations/observability/apply-stacks#folding</docs>
public class StreamPrefixTruncatorFoldOrderTests {
  // No receptor registry: the closer runs no destruction-stage receptor and reads nothing for them
  private static readonly IServiceScopeFactory _noServices = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

  private sealed class OrderRecordingCoordinator : NoOpWorkCoordinator, IWorkCoordinator {
    public List<string> Calls { get; } = [];

    public Task<int> FoldStreamApplyPathsAsync(
        IReadOnlyCollection<Guid> streamIds, CancellationToken cancellationToken = default) {
      Calls.Add("fold");
      return Task.FromResult(1);
    }

    public Task<StreamTruncateResult> TruncateStreamPrefixAsync(
        Guid streamId, long throughVersion, bool archive = false, CancellationToken cancellationToken = default) {
      Calls.Add("close");
      return Task.FromResult(new StreamTruncateResult("truncated", 3));
    }
  }

  [Test]
  public async Task Truncate_FoldsTheApplyPath_BeforeTheTruncateAsync() {
    var coordinator = new OrderRecordingCoordinator();
    var closer = new StreamPrefixTruncator(coordinator, NullLogger<StreamPrefixTruncator>.Instance, NoOpDestructionHook.Instance, _noServices, archiveStore: null);

    var result = await closer.TruncateAsync(Guid.NewGuid(), throughVersion: 3);

    await Assert.That(result.Status).IsEqualTo("truncated");
    await Assert.That(coordinator.Calls).IsEquivalentTo(["fold", "close"])
      .Because("fold-before-discard: folding after the truncate would fold a path the close already "
             + "destroyed — the order IS the guarantee");
  }
}
