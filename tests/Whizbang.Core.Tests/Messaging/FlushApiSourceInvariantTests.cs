using System.IO;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Source-level invariant guards around the IWorkCoordinatorStrategy flush API.
/// These catch regressions that behavior tests miss — e.g., a future refactor that
/// wires the Dispatcher's cascade path back to the force-flush method would slip past
/// any mock-based pinning test if the mock was updated in sync, but could not slip
/// past a file-text check. Reads source files directly, no reflection (AOT-safe).
/// </summary>
/// <docs>data/work-coordinator-strategies</docs>
[Category("Core")]
[Category("Messaging")]
public class FlushApiSourceInvariantTests {
  private static readonly string _repoRoot = _findRepoRoot();

  [Test]
  public async Task FlushMode_EnumFullyRemovedFromSourceTreeAsync() {
    var hits = new List<string>();
    foreach (var path in Directory.EnumerateFiles(Path.Combine(_repoRoot, "src"), "*.cs", SearchOption.AllDirectories)) {
      var text = await File.ReadAllTextAsync(path);
      if (text.Contains("FlushMode", StringComparison.Ordinal)) {
        hits.Add(Path.GetRelativePath(_repoRoot, path));
      }
    }
    await Assert.That(hits).IsEmpty()
      .Because("FlushMode was intentionally deleted in favor of the two-method API. If this fires, someone reintroduced it — use FlushAsync (fire-and-forget) or FlushAndGetBatchAsync (force + return) instead.");
  }

  [Test]
  public async Task Dispatcher_AllFlushSites_UseFireAndForgetFlushAsync_NeverFlushAndGetBatchAsyncAsync() {
    var dispatcher = await File.ReadAllTextAsync(Path.Combine(_repoRoot, "src/Whizbang.Core/Dispatcher.cs"));
    var forceFlushHits = _countOccurrences(dispatcher, "strategy.FlushAndGetBatchAsync(");
    await Assert.That(forceFlushHits).IsEqualTo(0)
      .Because("Dispatcher cascade/publish/send paths are fire-and-forget — they never consume the WorkBatch. Forcing a synchronous flush here bypasses Interval/Batch batching (the 2026-03-12 regression). Use strategy.FlushAsync(flags) instead.");

    var fireAndForgetHits = _countOccurrences(dispatcher, "strategy.FlushAsync(WorkBatchOptions");
    await Assert.That(fireAndForgetHits).IsGreaterThanOrEqualTo(5)
      .Because("Dispatcher has five flush call sites (cascade, publish-dynamic, send-via-scope, send-via-scope<T>, send-many). All five must use fire-and-forget FlushAsync.");
  }

  [Test]
  public async Task InboxDedupSites_UseFlushAndGetBatchAsync_NotFireAndForgetFlushAsyncAsync() {
    // TransportConsumerWorker is no longer in this list: the pump-then-process refactor
    // moved its inbox-dedup responsibility into the batch pipeline, and the leftover
    // in-worker dedup method (never called) was deleted in v0.813. Until then this
    // invariant was satisfied vacuously by that dead code — ServiceBusConsumerWorker
    // holds the only live in-worker dedup site.
    foreach (var workerFile in new[] {
      "src/Whizbang.Core/Workers/ServiceBusConsumerWorker.cs"
    }) {
      var worker = await File.ReadAllTextAsync(Path.Combine(_repoRoot, workerFile));
      // Each consumer worker has exactly one inbox-dedup flush that consumes the WorkBatch
      // to filter by MessageId. That site must use FlushAndGetBatchAsync.
      var dedupHits = _countOccurrences(worker, "strategy.FlushAndGetBatchAsync(");
      await Assert.That(dedupHits).IsGreaterThanOrEqualTo(1)
        .Because($"{Path.GetFileName(workerFile)} must flush via FlushAndGetBatchAsync for inbox dedup — it needs the returned WorkBatch to filter its own work by MessageId. Switching to fire-and-forget would break deduplication.");
    }
  }

  [Test]
  public async Task IWorkFlusher_AllImplementations_DelegateToFlushAndGetBatchAsyncAsync() {
    // IWorkFlusher is the middleware-facing interface (end-of-request flush). It must
    // force-flush — deferring to a strategy's batching window would leave messages
    // unpersisted past the HTTP response. Each strategy's IWorkFlusher.FlushAsync impl
    // must therefore call FlushAndGetBatchAsync, not the new fire-and-forget FlushAsync.
    foreach (var strategyFile in new[] {
      "src/Whizbang.Core/Messaging/ImmediateWorkCoordinatorStrategy.cs",
      "src/Whizbang.Core/Messaging/ScopedWorkCoordinatorStrategy.cs",
      "src/Whizbang.Core/Messaging/IntervalWorkCoordinatorStrategy.cs",
      "src/Whizbang.Core/Messaging/BatchWorkCoordinatorStrategy.cs",
      "src/Whizbang.Core/Messaging/NonDisposingStrategyAdapter.cs"
    }) {
      var source = await File.ReadAllTextAsync(Path.Combine(_repoRoot, strategyFile));
      // Find the IWorkFlusher.FlushAsync body and assert it mentions FlushAndGetBatchAsync.
      var idx = source.IndexOf("IWorkFlusher.FlushAsync(CancellationToken", StringComparison.Ordinal);
      await Assert.That(idx).IsGreaterThan(0)
        .Because($"{Path.GetFileName(strategyFile)} must implement IWorkFlusher.FlushAsync");

      // Inspect the 400 chars after the method signature — small enough to be the method body.
      var body = source.Substring(idx, Math.Min(400, source.Length - idx));
      await Assert.That(body).Contains("FlushAndGetBatchAsync")
        .Because($"{Path.GetFileName(strategyFile)}'s IWorkFlusher.FlushAsync must delegate to FlushAndGetBatchAsync — end-of-request middleware cannot defer to a batching window.");
    }
  }

  // Find the repo root by walking up until we see a `src/` directory sibling.
  private static string _findRepoRoot() {
    var dir = AppContext.BaseDirectory;
    while (dir is not null) {
      if (Directory.Exists(Path.Combine(dir, "src", "Whizbang.Core"))) {
        return dir;
      }
      dir = Path.GetDirectoryName(dir);
    }
    throw new InvalidOperationException("Could not locate whizbang repo root from AppContext.BaseDirectory");
  }

  private static int _countOccurrences(string haystack, string needle) {
    var count = 0;
    var idx = 0;
    while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0) {
      count++;
      idx += needle.Length;
    }
    return count;
  }
}
