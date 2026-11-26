// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Workers;

#pragma warning disable CA1707 // Test method names use underscores by convention

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The maintenance step that rebuilds a bloated index on a queue table that churns to empty with
/// <c>REINDEX INDEX CONCURRENTLY</c> (#1746): one index per cycle, fleet-wide, the one with the most bytes to give back,
/// only when it is over <c>ReindexBloatThreshold</c> and at least the size floor, and only after every invalid leftover
/// of an interrupted rebuild is dropped.
/// </summary>
/// <docs>operations/infrastructure/storage-maintenance#queue-index-rebuild</docs>
/// <code-under-test>src/Whizbang.Core/Workers/QueueIndexReindexStep.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/MaintenanceWorkerOptionsValidator.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Messaging/IWorkCoordinator.cs</code-under-test>
[Category("Workers")]
public class QueueIndexReindexStepTests {
  private const long MIB = 1024 * 1024;
  private const string KEY = "Whizbang:Workers:Maintenance:ReindexBloatThreshold";
  private const string LOCK_TIMEOUT_KEY = "Whizbang:Workers:Maintenance:ReindexLockTimeout";

  // A fixed instant: the claim window is read from the step's clock, never the host's.
  private static readonly DateTimeOffset _noon = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

  private static QueueIndexBloat _index(string name, long sizeBytes, long? expectedBytes, string table = "wh_outbox") =>
    new(name, table, sizeBytes, expectedBytes) { Valid = true, Method = BloatMeasurementMethod.Estimate };

  private static QueueIndexBloat _leftover(string name, string table = "wh_outbox") =>
    new(name, table, 4 * MIB, 64 * 1024) { Valid = false, Method = BloatMeasurementMethod.Estimate };

  /// <summary>A coordinator that answers the survey and records each drop and rebuild, in order.</summary>
  private sealed class ReindexCoordinator : IWorkCoordinator {
    public List<QueueIndexBloat> Survey { get; init; } = [];
    public Dictionary<string, long?> SizeAfter { get; init; } = new(StringComparer.Ordinal);
    public HashSet<string> NotDropped { get; init; } = new(StringComparer.Ordinal);
    public HashSet<string> Throws { get; init; } = new(StringComparer.Ordinal);
    public HashSet<string> Cancels { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, QueueIndexRebuild> GivesUp { get; init; } = new(StringComparer.Ordinal);
    public List<QueueIndexBloat>? SurveyAfterRebuild { get; init; }
    public List<string> Calls { get; } = [];
    public List<TimeSpan> LockTimeouts { get; } = [];
    public int Scans { get; private set; }
    private bool _rebuilt;

    public Task<IReadOnlyList<QueueIndexBloat>> GetQueueIndexBloatAsync(CancellationToken cancellationToken = default) {
      Scans++;
      return Task.FromResult<IReadOnlyList<QueueIndexBloat>>(_rebuilt && SurveyAfterRebuild is { } after ? after : Survey);
    }

    public async Task<QueueIndexRebuild> RebuildQueueIndexAsync(
        string indexName, TimeSpan lockTimeout, CancellationToken cancellationToken = default) {
      LockTimeouts.Add(lockTimeout);
      _rebuilt = true;
      if (GivesUp.TryGetValue(indexName, out var gaveUp)) {
        Calls.Add("reindex:" + indexName);
        return gaveUp;
      }
      return new QueueIndexRebuild(await ReindexQueueIndexAsync(indexName, cancellationToken));
    }

    public Task<bool> DropInvalidQueueIndexAsync(string indexName, CancellationToken cancellationToken = default) {
      Calls.Add("drop:" + indexName);
      if (Cancels.Contains(indexName)) {
        throw new OperationCanceledException();
      }
      if (Throws.Contains(indexName)) {
        throw new InvalidOperationException($"drop of {indexName} failed");
      }
      return Task.FromResult(!NotDropped.Contains(indexName));
    }

    public Task<bool> DropInvalidQueueIndexAsync(string indexName, TimeSpan lockTimeout, CancellationToken cancellationToken = default) {
      LockTimeouts.Add(lockTimeout);
      return DropInvalidQueueIndexAsync(indexName, cancellationToken);
    }

    public Task<long?> ReindexQueueIndexAsync(string indexName, CancellationToken cancellationToken = default) {
      Calls.Add("reindex:" + indexName);
      if (Throws.Contains(indexName)) {
        throw new InvalidOperationException($"reindex of {indexName} failed");
      }
      return Task.FromResult(SizeAfter.TryGetValue(indexName, out var after) ? after : 16 * 1024);
    }

    // Unused surface for this test.
    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PartitionRecomputeResult> RecomputePartitionNumbersAsync(int partitionCount, CancellationToken cancellationToken = default) => Task.FromResult(new PartitionRecomputeResult());
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) => Task.FromResult<PerspectiveCursorInfo?>(null);
    public Task<IReadOnlyList<MaintenanceResult>> PerformMaintenanceAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MaintenanceResult>>([]);
  }

  /// <summary>A claim store that grants or refuses every key, and records the keys asked for.</summary>
  private sealed class Claims(bool grants = true) : IClaimedEmissionStore {
    public List<string> Keys { get; } = [];
    public Task<bool> TryClaimAsync(string claimKey, Guid claimedByEventId, CancellationToken cancellationToken) {
      Keys.Add(claimKey);
      return Task.FromResult(grants);
    }
  }

  private static async Task<(ReindexCoordinator Coordinator, FakeLogger<QueueIndexReindexStep> Logger)> _runAsync(
      ReindexCoordinator coordinator,
      double threshold = 3.0,
      Claims? claims = null,
      bool noClaimStore = false,
      bool noCoordinator = false,
      TimeSpan? lockTimeout = null,
      MaintenanceMetrics? metrics = null) {
    var logger = new FakeLogger<QueueIndexReindexStep>();
    var options = new MaintenanceWorkerOptions { ReindexBloatThreshold = threshold };
    if (lockTimeout is { } timeout) {
      options.ReindexLockTimeout = timeout;
    }
    var step = new QueueIndexReindexStep(Options.Create(options), logger, new FakeTimeProvider(_noon));
    var services = new ServiceCollection();
    if (metrics is not null) {
      services.AddSingleton(metrics);
    }
    if (!noCoordinator) {
      services.AddSingleton<IWorkCoordinator>(coordinator);
    }
    if (!noClaimStore) {
      services.AddSingleton<IClaimedEmissionStore>(claims ?? new Claims());
    }
    await using var provider = services.BuildServiceProvider();
    await step.RunAsync(provider, CancellationToken.None);
    return (coordinator, logger);
  }

  private static IReadOnlyList<string> _messages(FakeLogger<QueueIndexReindexStep> logger) =>
    [.. logger.Collector.GetSnapshot().Select(r => r.Message)];

  // ── The threshold ───────────────────────────────────────────────────────

  /// <summary>Over the threshold is rebuilt; exactly at it is not: the setting is a line to cross, not to reach.</summary>
  /// <docs>operations/infrastructure/storage-maintenance#reindex-threshold</docs>
  [Test]
  [Arguments(3.01, true)]
  [Arguments(3.0, false)]
  [Arguments(2.99, false)]
  public async Task Choose_AnIndexIsChosenOnlyWhenItsRatioExceedsTheThresholdAsync(double ratio, bool chosen) {
    var expected = 10 * MIB;
    var index = _index("idx_outbox_a", (long)(expected * ratio), expected);

    var choice = QueueIndexReindexStep.Choose([index], threshold: 3.0);

    await Assert.That(choice is not null).IsEqualTo(chosen);
  }

  /// <summary>The threshold is the option's: the same index is chosen under a lower one and not under a higher one.</summary>
  /// <docs>operations/infrastructure/storage-maintenance#reindex-threshold</docs>
  [Test]
  [Arguments(1.5, true)]
  [Arguments(5.0, false)]
  public async Task Run_TheThresholdIsTheOptionsAsync(double threshold, bool rebuilt) {
    var coordinator = new ReindexCoordinator { Survey = [_index("idx_outbox_a", 40 * MIB, 10 * MIB)] };

    await _runAsync(coordinator, threshold);

    await Assert.That(coordinator.Calls.Contains("reindex:idx_outbox_a")).IsEqualTo(rebuilt);
  }

  /// <summary>Zero turns the rebuild off: the step does not even survey the indexes, or ask for the claim.</summary>
  /// <docs>operations/infrastructure/storage-maintenance#reindex-threshold</docs>
  [Test]
  public async Task Run_AThresholdOfZero_DoesNothing_NotEvenASurveyAsync() {
    var claims = new Claims();
    var coordinator = new ReindexCoordinator { Survey = [_index("idx_outbox_a", 400 * MIB, MIB)] };

    await _runAsync(coordinator, threshold: 0, claims: claims);

    await Assert.That(coordinator.Scans).IsEqualTo(0);
    await Assert.That(coordinator.Calls).IsEmpty();
    await Assert.That(claims.Keys).IsEmpty();
  }

  /// <summary>The default is 3.0, the line a table is queued for rewrite at, and the key binds from configuration.</summary>
  /// <docs>operations/infrastructure/storage-maintenance#reindex-threshold</docs>
  [Test]
  public async Task TheThreshold_DefaultsToThree_AndBindsFromConfigurationAsync() {
    await Assert.That(new MaintenanceWorkerOptions().ReindexBloatThreshold).IsEqualTo(3.0);
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
      [KEY] = "5.5",
    }).Build());
    services.AddWhizbangWorkers();
    await using var provider = services.BuildServiceProvider();

    await Assert.That(provider.GetRequiredService<IOptions<MaintenanceWorkerOptions>>().Value.ReindexBloatThreshold).IsEqualTo(5.5);
  }

  /// <summary>
  /// Zero and anything finite above 1 are accepted. A value at or below 1 (but not zero) is refused, because a freshly
  /// built index measures about 1 and would be rebuilt every cycle; a negative or non-finite value is refused too. The
  /// failure names the key.
  /// </summary>
  /// <docs>operations/infrastructure/storage-maintenance#reindex-threshold</docs>
  [Test]
  [Arguments(0.0, true)]
  [Arguments(1.0001, true)]
  [Arguments(3.0, true)]
  [Arguments(100.0, true)]
  [Arguments(1.0, false)]
  [Arguments(0.5, false)]
  [Arguments(-1.0, false)]
  [Arguments(double.NaN, false)]
  [Arguments(double.PositiveInfinity, false)]
  public async Task TheThreshold_IsValidatedAsync(double threshold, bool valid) {
    var result = new MaintenanceWorkerOptionsValidator().Validate(null, new MaintenanceWorkerOptions { ReindexBloatThreshold = threshold });

    await Assert.That(result.Succeeded).IsEqualTo(valid);
    if (!valid) {
      await Assert.That(result.FailureMessage).Contains(KEY);
      await Assert.That(result.FailureMessage).Contains("0 to turn the rebuild off, or a finite value greater than 1");
    }
  }

  /// <summary>An invalid threshold in configuration stops start, as every maintenance option the validator covers does.</summary>
  /// <docs>operations/infrastructure/storage-maintenance#reindex-threshold</docs>
  [Test]
  public async Task AnInvalidThresholdInConfiguration_FailsWhenTheOptionsAreReadAsync() {
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
      [KEY] = "0.9",
    }).Build());
    services.AddWhizbangWorkers();
    await using var provider = services.BuildServiceProvider();

    var thrown = Assert.Throws<OptionsValidationException>(
      () => _ = provider.GetRequiredService<IOptions<MaintenanceWorkerOptions>>().Value);

    await Assert.That(thrown.Message).Contains(KEY);
  }

  // ── Which index ─────────────────────────────────────────────────────────

  /// <summary>
  /// An index under the size floor is never rebuilt, however bloated it reads: a few pages are noise in the estimate and
  /// nothing worth a rebuild. At the floor it is.
  /// </summary>
  /// <docs>operations/infrastructure/storage-maintenance#reindex-size-floor</docs>
  [Test]
  [Arguments(1024L * 1024, true)]
  [Arguments((1024L * 1024) - 8192, false)]
  public async Task Choose_TheSizeFloorAsync(long sizeBytes, bool chosen) {
    var index = _index("idx_outbox_a", sizeBytes, 16 * 1024);

    var choice = QueueIndexReindexStep.Choose([index], threshold: 3.0);
    var floor = QueueIndexReindexStep.MIN_REINDEX_BYTES;

    await Assert.That(floor).IsEqualTo(MIB);
    await Assert.That(choice is not null).IsEqualTo(chosen);
  }

  /// <summary>
  /// Of several over the line, the one with the most bytes to give back is rebuilt, not the highest ratio: a nearly empty
  /// queue makes every small index read as hugely bloated.
  /// </summary>
  /// <docs>operations/infrastructure/storage-maintenance#reindex-most-bytes-first</docs>
  [Test]
  public async Task Choose_TheMostReclaimableBytesFirst_NotTheHighestRatioAsync() {
    var smallButSteep = _index("idx_outbox_steep", 2 * MIB, 32 * 1024);         // 64x, about 2 MiB back
    var largeButMild = _index("idx_inbox_large", 400 * MIB, 100 * MIB, "wh_inbox"); // 4x, 300 MiB back

    var choice = QueueIndexReindexStep.Choose([smallButSteep, largeButMild], threshold: 3.0);

    await Assert.That(choice!.Value.IndexName).IsEqualTo("idx_inbox_large");
  }

  /// <summary>Equal bytes to give back are taken by name, so the choice does not depend on the survey's order.</summary>
  /// <docs>operations/infrastructure/storage-maintenance#reindex-most-bytes-first</docs>
  [Test]
  public async Task Choose_TiesAreTakenByNameAsync() {
    var b = _index("idx_b", 40 * MIB, 10 * MIB);
    var a = _index("idx_a", 40 * MIB, 10 * MIB);

    await Assert.That(QueueIndexReindexStep.Choose([b, a], 3.0)!.Value.IndexName).IsEqualTo("idx_a");
    await Assert.That(QueueIndexReindexStep.Choose([a, b], 3.0)!.Value.IndexName).IsEqualTo("idx_a");
  }

  /// <summary>
  /// An index whose expected size the catalog cannot give is not judged, and an invalid one is never rebuilt (a leftover
  /// is dropped instead); the valid, known index beside them is.
  /// </summary>
  /// <docs>operations/infrastructure/storage-maintenance#reindex-expected-size</docs>
  [Test]
  public async Task Choose_SkipsAnUnknownRatioAndAnInvalidIndexAsync() {
    var unknown = _index("idx_unknown", 900 * MIB, null);
    var invalid = _index("idx_invalid", 900 * MIB, MIB) with { Valid = false };
    var known = _index("idx_known", 40 * MIB, 10 * MIB);

    var choice = QueueIndexReindexStep.Choose([unknown, invalid, known], threshold: 3.0);

    await Assert.That(choice!.Value.IndexName).IsEqualTo("idx_known");
    await Assert.That(QueueIndexReindexStep.Choose([unknown, invalid], threshold: 3.0)).IsNull();
  }

  /// <summary>The ratio is the size over the expected size, and an expected size of zero or none gives no ratio.</summary>
  /// <docs>operations/infrastructure/storage-maintenance#reindex-expected-size</docs>
  [Test]
  public async Task TheRatio_IsSizeOverExpected_AndUnknownWithoutAnExpectedSizeAsync() {
    await Assert.That(_index("i", 30 * MIB, 10 * MIB).Ratio).IsEqualTo(3.0);
    await Assert.That(_index("i", 30 * MIB, 0).Ratio).IsNull();
    await Assert.That(_index("i", 30 * MIB, null).Ratio).IsNull();
  }

  // ── One per cycle, fleet-wide ───────────────────────────────────────────

  /// <summary>Only one index is rebuilt per cycle, however many are over the line.</summary>
  /// <docs>operations/infrastructure/storage-maintenance#reindex-one-per-cycle</docs>
  [Test]
  public async Task Run_RebuildsOneIndexPerCycleAsync() {
    var coordinator = new ReindexCoordinator {
      Survey = [_index("idx_a", 400 * MIB, 10 * MIB), _index("idx_b", 300 * MIB, 10 * MIB), _index("idx_c", 200 * MIB, 10 * MIB)],
    };

    await _runAsync(coordinator);

    await Assert.That(coordinator.Calls).IsEquivalentTo(["reindex:idx_a"]);
  }

  /// <summary>An instance that loses the window's claim does nothing: another instance has this cycle's rebuild.</summary>
  /// <docs>operations/infrastructure/storage-maintenance#reindex-one-per-cycle</docs>
  [Test]
  public async Task Run_WhenTheClaimIsLost_NothingIsSurveyedOrRebuiltAsync() {
    var claims = new Claims(grants: false);
    var coordinator = new ReindexCoordinator { Survey = [_index("idx_a", 400 * MIB, 10 * MIB), _leftover("idx_a_ccnew")] };

    await _runAsync(coordinator, claims: claims);

    await Assert.That(claims.Keys.Count).IsEqualTo(1);
    await Assert.That(coordinator.Scans).IsEqualTo(0);
    await Assert.That(coordinator.Calls).IsEmpty();
  }

  /// <summary>A host with no claim store skips the step rather than have every instance rebuild, and says so.</summary>
  /// <docs>operations/infrastructure/storage-maintenance#reindex-one-per-cycle</docs>
  [Test]
  public async Task Run_WithNoClaimStore_SkipsAndSaysWhyAsync() {
    var coordinator = new ReindexCoordinator { Survey = [_index("idx_a", 400 * MIB, 10 * MIB)] };

    var (_, logger) = await _runAsync(coordinator, noClaimStore: true);

    await Assert.That(coordinator.Scans).IsEqualTo(0);
    await Assert.That(_messages(logger)).Contains(
      "Queue index rebuild skipped: no claim store is registered to run it on one instance");
  }

  /// <summary>A host with no work coordinator has nothing to survey.</summary>
  [Test]
  public async Task Run_WithNoCoordinator_DoesNothingAsync() {
    var claims = new Claims();

    var (coordinator, _) = await _runAsync(new ReindexCoordinator(), claims: claims, noCoordinator: true);

    await Assert.That(coordinator.Scans).IsEqualTo(0);
    await Assert.That(claims.Keys).IsEmpty();
  }

  /// <summary>
  /// The claim key names the window the step's clock is in. The window is half the maintenance interval, as the
  /// integrity audit's claim is: instances whose cycles fall together share one, and an instance's next cycle is never
  /// blocked by its own last claim.
  /// </summary>
  /// <docs>operations/infrastructure/storage-maintenance#reindex-one-per-cycle</docs>
  [Test]
  [Arguments(10, 300)]
  [Arguments(1, 30)]
  [Arguments(0, 30)]
  public async Task TheClaimWindow_IsHalfTheMaintenanceIntervalAsync(int intervalMinutes, int windowSeconds) {
    var window = QueueIndexReindexStep.ClaimWindow(new MaintenanceWorkerOptions { IntervalMinutes = intervalMinutes });

    await Assert.That(window).IsEqualTo(TimeSpan.FromSeconds(windowSeconds));
  }

  /// <summary>The key is the step's own prefix and the window's start in ticks.</summary>
  /// <docs>operations/infrastructure/storage-maintenance#reindex-one-per-cycle</docs>
  [Test]
  public async Task TheClaimKey_NamesTheWindowAsync() {
    var claims = new Claims(grants: false);

    await _runAsync(new ReindexCoordinator(), claims: claims);

    var window = TimeSpan.FromMinutes(5).Ticks;
    await Assert.That(claims.Keys).IsEquivalentTo([$"whizbang:queue-reindex:{_noon.UtcTicks - (_noon.UtcTicks % window)}"]);
  }

  // ── Leftovers first ─────────────────────────────────────────────────────

  /// <summary>Every invalid leftover of an interrupted rebuild is dropped, and only then is an index rebuilt.</summary>
  /// <docs>operations/infrastructure/storage-maintenance#queue-index-leftovers</docs>
  [Test]
  public async Task Run_DropsEveryInvalidLeftoverFirst_ThenRebuildsAsync() {
    var coordinator = new ReindexCoordinator {
      Survey = [_index("idx_a", 400 * MIB, 10 * MIB), _leftover("idx_a_ccnew"), _leftover("idx_b_ccold1", "wh_inbox")],
    };

    var (_, logger) = await _runAsync(coordinator);

    await Assert.That(coordinator.Calls).IsEquivalentTo(["drop:idx_a_ccnew", "drop:idx_b_ccold1", "reindex:idx_a"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That(_messages(logger)).Contains(
      "Queue index rebuild: dropped idx_b_ccold1 on wh_inbox, an invalid leftover of an interrupted REINDEX CONCURRENTLY");
  }

  /// <summary>
  /// Only an invalid index named as a concurrent rebuild names its copies is a leftover. A valid index with that name, and
  /// an invalid index with any other name, are left alone: the step drops nothing it could not have made.
  /// </summary>
  /// <docs>operations/infrastructure/storage-maintenance#reindex-leftover-names</docs>
  [Test]
  public async Task Run_DropsOnlyInvalidIndexesNamedAsALeftoverAsync() {
    var coordinator = new ReindexCoordinator {
      Survey = [
        _index("idx_valid_ccnew", 64 * 1024, 64 * 1024),
        _index("idx_failed_build", 64 * 1024, 64 * 1024) with { Valid = false },
        _leftover("idx_real_ccnew"),
      ],
    };

    await _runAsync(coordinator);

    await Assert.That(coordinator.Calls).IsEquivalentTo(["drop:idx_real_ccnew"]);
  }

  /// <summary>The names a concurrent rebuild gives: <c>_ccnew</c> or <c>_ccold</c>, optionally followed by digits.</summary>
  /// <docs>operations/infrastructure/storage-maintenance#reindex-leftover-names</docs>
  [Test]
  [Arguments("idx_outbox_ccnew", true)]
  [Arguments("idx_outbox_ccnew12", true)]
  [Arguments("idx_outbox_ccold", true)]
  [Arguments("idx_outbox_ccold3", true)]
  [Arguments("_ccnew", true)]
  [Arguments("idx_outbox", false)]
  [Arguments("idx_ccnewer", false)]
  [Arguments("idx_outbox_ccnew_x", false)]
  [Arguments("ccnew", false)]
  [Arguments("123", false)]
  [Arguments("", false)]
  [Arguments(null, false)]
  public async Task TheLeftoverName_IsWhatAConcurrentRebuildNamesItsCopiesAsync(string? name, bool leftover) {
    await Assert.That(QueueIndexNames.IsReindexLeftoverName(name)).IsEqualTo(leftover);
  }

  /// <summary>
  /// While an index build is running on a queue table, the cycle drops nothing and rebuilds nothing: an invalid index is
  /// then that build's work in progress, and a second rebuild would wait on it.
  /// </summary>
  /// <docs>operations/infrastructure/storage-maintenance#reindex-build-in-progress</docs>
  [Test]
  public async Task Run_WhileABuildIsInProgress_NothingIsDroppedOrRebuiltAsync() {
    var coordinator = new ReindexCoordinator {
      Survey = [_index("idx_a", 400 * MIB, 10 * MIB) with { BuildInProgress = true }, _leftover("idx_a_ccnew") with { BuildInProgress = true }],
    };

    var (_, logger) = await _runAsync(coordinator);

    await Assert.That(coordinator.Calls).IsEmpty();
    await Assert.That(_messages(logger)).Contains(
      "Queue index rebuild skipped this cycle: an index build is running on wh_outbox, so an invalid index there may be its work in progress");
  }

  /// <summary>A leftover that was not dropped, or whose drop failed, is logged, and the rebuild still runs.</summary>
  /// <docs>operations/infrastructure/storage-maintenance#queue-index-leftovers</docs>
  [Test]
  public async Task Run_ALeftoverThatIsNotDropped_IsLogged_AndTheRebuildStillRunsAsync() {
    var coordinator = new ReindexCoordinator {
      Survey = [_index("idx_a", 400 * MIB, 10 * MIB), _leftover("idx_x_ccnew"), _leftover("idx_y_ccold")],
      NotDropped = { "idx_x_ccnew" },
      Throws = { "idx_y_ccold" },
    };

    var (_, logger) = await _runAsync(coordinator);

    await Assert.That(coordinator.Calls).Contains("reindex:idx_a");
    await Assert.That(_messages(logger)).Contains(
      "Queue index rebuild: the invalid leftover idx_x_ccnew on wh_outbox was not dropped (the coordinator logged why)");
    var failure = logger.Collector.GetSnapshot().Single(r => r.Exception is not null);
    await Assert.That(failure.Level).IsEqualTo(LogLevel.Warning);
    await Assert.That(failure.Message).IsEqualTo("Queue index rebuild: dropping the invalid leftover idx_y_ccold on wh_outbox failed");
  }

  // ── The rebuild ─────────────────────────────────────────────────────────

  /// <summary>A rebuild logs the index's size before and after, and the ratio it was chosen at with how it was obtained.</summary>
  /// <docs>operations/infrastructure/storage-maintenance#reindex-estimate-only</docs>
  [Test]
  public async Task Run_ARebuild_IsLoggedWithTheSizesAndTheEstimateAsync() {
    var coordinator = new ReindexCoordinator {
      Survey = [_index("idx_a", 40 * MIB, 8 * MIB)],
      SizeAfter = { ["idx_a"] = 8 * MIB },
    };

    var (_, logger) = await _runAsync(coordinator);

    var messages = _messages(logger);
    await Assert.That(messages).Contains(
      "Queue index rebuild: rebuilding idx_a on wh_outbox; it measured 5x its expected 8388608 bytes (estimate), over "
      + "Whizbang:Workers:Maintenance:ReindexBloatThreshold (3)");
    await Assert.That(messages).Contains("Queue index rebuild: idx_a on wh_outbox rebuilt concurrently, 41943040 -> 8388608 bytes");
  }

  /// <summary>A rebuild the coordinator did not run, or one that failed, is logged and does not fail the cycle.</summary>
  /// <docs>operations/infrastructure/storage-maintenance#queue-index-rebuild</docs>
  [Test]
  public async Task Run_ARebuildThatDidNotRunOrFailed_IsLoggedAsync() {
    var notRun = new ReindexCoordinator { Survey = [_index("idx_a", 40 * MIB, 8 * MIB)], SizeAfter = { ["idx_a"] = null } };
    var failed = new ReindexCoordinator { Survey = [_index("idx_b", 40 * MIB, 8 * MIB)], Throws = { "idx_b" } };

    var (_, notRunLog) = await _runAsync(notRun);
    var (_, failedLog) = await _runAsync(failed);

    await Assert.That(_messages(notRunLog)).Contains(
      "Queue index rebuild: idx_a on wh_outbox was not rebuilt (the coordinator logged why)");
    var failure = failedLog.Collector.GetSnapshot().Single(r => r.Exception is not null);
    await Assert.That(failure.Level).IsEqualTo(LogLevel.Warning);
    await Assert.That(failure.Message).IsEqualTo(
      "Queue index rebuild of idx_b on wh_outbox failed; its invalid copy, if one was left, is dropped now");
  }

  /// <summary>
  /// A rebuild that fails leaves an invalid copy behind; the step surveys again at once and drops it, rather than leaving
  /// it to be written on every insert until the next cycle (#2611).
  /// </summary>
  /// <docs>operations/infrastructure/storage-maintenance#a-rebuild-never-waits-for-hours</docs>
  [Test]
  public async Task Run_ARebuildThatFailed_DropsTheCopyItLeft_RightAwayAsync() {
    var coordinator = new ReindexCoordinator {
      Survey = [_index("idx_b", 40 * MIB, 8 * MIB)],
      SurveyAfterRebuild = [_index("idx_b", 40 * MIB, 8 * MIB), _leftover("idx_b_ccnew"), _leftover("idx_other_ccold", "wh_inbox")],
      Throws = { "idx_b" },
    };

    await _runAsync(coordinator);

    await Assert.That(coordinator.Calls).IsEquivalentTo(["reindex:idx_b", "drop:idx_b_ccnew"], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("the copy the failed rebuild left on its table is dropped now; another table's leftover waits for the next cycle's sweep");
  }

  // ── The bounded wait (#2611) ────────────────────────────────────────────

  /// <summary>The lock timeout defaults to one minute and binds from configuration.</summary>
  /// <docs>operations/infrastructure/storage-maintenance#a-rebuild-never-waits-for-hours</docs>
  [Test]
  public async Task TheLockTimeout_DefaultsToOneMinute_AndBindsFromConfigurationAsync() {
    await Assert.That(new MaintenanceWorkerOptions().ReindexLockTimeout).IsEqualTo(TimeSpan.FromMinutes(1));
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
      [LOCK_TIMEOUT_KEY] = "00:00:05",
    }).Build());
    services.AddWhizbangWorkers();
    await using var provider = services.BuildServiceProvider();

    await Assert.That(provider.GetRequiredService<IOptions<MaintenanceWorkerOptions>>().Value.ReindexLockTimeout)
      .IsEqualTo(TimeSpan.FromSeconds(5));
  }

  /// <summary>
  /// A lock timeout that is not positive is refused, naming the key: zero is Postgres's "wait forever", the very wait the
  /// bound exists to end, and a negative one is meaningless.
  /// </summary>
  /// <docs>operations/infrastructure/storage-maintenance#a-rebuild-never-waits-for-hours</docs>
  [Test]
  [Arguments(1, true)]
  [Arguments(60_000, true)]
  [Arguments(0, false)]
  [Arguments(-1, false)]
  public async Task Validate_TheLockTimeoutMustBePositiveAsync(int milliseconds, bool valid) {
    var result = new MaintenanceWorkerOptionsValidator().Validate(
      null, new MaintenanceWorkerOptions { ReindexLockTimeout = TimeSpan.FromMilliseconds(milliseconds) });

    await Assert.That(result.Succeeded).IsEqualTo(valid);
    if (!valid) {
      await Assert.That(result.FailureMessage).Contains(LOCK_TIMEOUT_KEY);
    }
  }

  /// <summary>The configured lock timeout reaches the leftover's drop and the rebuild alike.</summary>
  /// <docs>operations/infrastructure/storage-maintenance#a-rebuild-never-waits-for-hours</docs>
  [Test]
  public async Task Run_TheLockTimeoutReachesTheDropAndTheRebuildAsync() {
    var coordinator = new ReindexCoordinator { Survey = [_index("idx_a", 400 * MIB, 10 * MIB), _leftover("idx_a_ccnew")] };

    await _runAsync(coordinator, lockTimeout: TimeSpan.FromSeconds(7));

    await Assert.That(coordinator.LockTimeouts).IsEquivalentTo([TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(7)]);
  }

  /// <summary>
  /// A rebuild that gave up behind an old transaction warns naming the index, each session it waited on with how long its
  /// transaction has been open, and any it could not see; counts on <c>whizbang.maintenance.queue_index_rebuild.gave_up</c>
  /// by index and table; and drops the invalid copy it left right away. It is tried again on a later cycle.
  /// </summary>
  /// <docs>operations/infrastructure/storage-maintenance#a-rebuild-never-waits-for-hours</docs>
  [Test]
  public async Task Run_ARebuildThatGaveUp_WarnsNamingTheBlockers_CountsIt_AndDropsItsCopyAsync() {
    var metrics = new MaintenanceMetrics(new WhizbangMetrics(
      new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>()));
    var coordinator = new ReindexCoordinator {
      Survey = [_index("idx_a", 400 * MIB, 10 * MIB, "wh_inbox_state")],
      SurveyAfterRebuild = [_index("idx_a", 400 * MIB, 10 * MIB, "wh_inbox_state"), _leftover("idx_a_ccnew", "wh_inbox_state")],
      GivesUp = {
        ["idx_a"] = new QueueIndexRebuild(null) {
          GaveUp = true,
          Blockers = [new QueueIndexBlocker(4242, new TimeSpan(3, 17, 5)), new QueueIndexBlocker(77, null)],
          HiddenSessions = 2,
        },
      },
    };

    var (_, logger) = await _runAsync(coordinator, lockTimeout: TimeSpan.FromSeconds(30), metrics: metrics);

    var warning = logger.Collector.GetSnapshot().Single(r => r.Level == LogLevel.Warning);
    await Assert.That(warning.Message).IsEqualTo(
      "Queue index rebuild of idx_a on wh_inbox_state gave up: a lock wait passed " + LOCK_TIMEOUT_KEY + " (00:00:30). "
      + "It was waiting on pid 4242 (transaction open 03:17:05), pid 77 (transaction age not visible to this role); "
      + "2 other session(s) in the database are not visible to this role (grant pg_monitor to see them). "
      + "It is tried again on a later cycle");
    await Assert.That(coordinator.Calls).IsEquivalentTo(["reindex:idx_a", "drop:idx_a_ccnew"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That(_gaveUp(metrics)).IsEquivalentTo([(1L, (string?)"idx_a", (string?)"wh_inbox_state")]);
  }

  /// <summary>Gave up with nothing still to name: the warning says the sessions it waited on have gone.</summary>
  /// <docs>operations/infrastructure/storage-maintenance#a-rebuild-never-waits-for-hours</docs>
  [Test]
  public async Task Run_ARebuildThatGaveUp_WithNoBlockerLeft_SaysSo_AndRunsWithoutMetricsAsync() {
    var coordinator = new ReindexCoordinator {
      Survey = [_index("idx_a", 400 * MIB, 10 * MIB)],
      GivesUp = { ["idx_a"] = new QueueIndexRebuild(null) { GaveUp = true } },
    };

    var (_, logger) = await _runAsync(coordinator);

    var warning = logger.Collector.GetSnapshot().Single(r => r.Level == LogLevel.Warning);
    await Assert.That(warning.Message).IsEqualTo(
      "Queue index rebuild of idx_a on wh_outbox gave up: a lock wait passed " + LOCK_TIMEOUT_KEY + " (00:01:00). "
      + "The sessions it waited on had ended by the time it looked. It is tried again on a later cycle");
  }

  private static List<(long, string?, string?)> _gaveUp(MaintenanceMetrics metrics) {
    var cells = new List<(long, string?, string?)>();
    using var listener = new System.Diagnostics.Metrics.MeterListener();
    listener.InstrumentPublished = (instrument, l) => {
      if (ReferenceEquals(instrument.Meter, metrics.QueueIndexRebuildsGaveUp.Meter)
          && instrument.Name == "whizbang.maintenance.queue_index_rebuild.gave_up") {
        l.EnableMeasurementEvents(instrument);
      }
    };
    listener.SetMeasurementEventCallback<long>((_, value, tags, _) => {
      string? index = null;
      string? table = null;
      foreach (var tag in tags) {
        if (tag.Key == MaintenanceMetrics.INDEX_TAG) {
          index = tag.Value as string;
        } else if (tag.Key == MaintenanceMetrics.TABLE_TAG) {
          table = tag.Value as string;
        }
      }
      if (value > 0) {
        cells.Add((value, index, table));
      }
    });
    listener.Start();
    listener.RecordObservableInstruments();
    return cells;
  }

  /// <summary>Nothing over the line is a quiet cycle: no rebuild, and a debug line naming the line.</summary>
  /// <docs>operations/infrastructure/storage-maintenance#queue-index-rebuild</docs>
  [Test]
  public async Task Run_NothingOverTheLine_RebuildsNothingAsync() {
    var coordinator = new ReindexCoordinator { Survey = [_index("idx_a", 20 * MIB, 10 * MIB)] };

    var (_, logger) = await _runAsync(coordinator);

    await Assert.That(coordinator.Calls).IsEmpty();
    await Assert.That(_messages(logger)).Contains(
      "Queue index rebuild: no index on a queue table is over Whizbang:Workers:Maintenance:ReindexBloatThreshold (3) and at least 1048576 bytes");
  }

  /// <summary>Cancellation is shutdown and propagates out of a rebuild, rather than being logged as its failure.</summary>
  [Test]
  public async Task Run_CancellationPropagatesAsync() {
    var coordinator = new CancelingCoordinator();
    var step = new QueueIndexReindexStep(
      Options.Create(new MaintenanceWorkerOptions()), new FakeLogger<QueueIndexReindexStep>(), new FakeTimeProvider(_noon));
    await using var provider = new ServiceCollection()
      .AddSingleton<IWorkCoordinator>(coordinator)
      .AddSingleton<IClaimedEmissionStore>(new Claims())
      .BuildServiceProvider();

    await Assert.That(async () => await step.RunAsync(provider, CancellationToken.None)).Throws<OperationCanceledException>();
  }

  /// <summary>Cancellation during a leftover's drop is shutdown too: it propagates, and nothing is rebuilt after it.</summary>
  [Test]
  public async Task Run_CancellationDuringADrop_PropagatesAsync() {
    var coordinator = new ReindexCoordinator {
      Survey = [_index("idx_a", 400 * MIB, 10 * MIB), _leftover("idx_a_ccnew")],
      Cancels = { "idx_a_ccnew" },
    };

    await Assert.That(async () => await _runAsync(coordinator)).Throws<OperationCanceledException>();
    await Assert.That(coordinator.Calls).IsEquivalentTo(["drop:idx_a_ccnew"]);
  }

  /// <summary>The step is registered with the workers, once however often they are added.</summary>
  [Test]
  public async Task AddWhizbangWorkers_RegistersTheStepOnceAsync() {
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddLogging();
    services.AddWhizbangWorkers();
    services.AddWhizbangWorkers();
    await using var provider = services.BuildServiceProvider();

    var steps = provider.GetServices<IMaintenanceStep>().OfType<QueueIndexReindexStep>().ToArray();

    await Assert.That(steps.Length).IsEqualTo(1);
    await Assert.That(steps[0].Name).IsEqualTo("queue-index-reindex");
  }

  private sealed class CancelingCoordinator : IWorkCoordinator {
    public Task<IReadOnlyList<QueueIndexBloat>> GetQueueIndexBloatAsync(CancellationToken cancellationToken = default) =>
      Task.FromResult<IReadOnlyList<QueueIndexBloat>>([_index("idx_a", 400 * MIB, 10 * MIB)]);

    public Task<long?> ReindexQueueIndexAsync(string indexName, CancellationToken cancellationToken = default) =>
      throw new OperationCanceledException();

    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PartitionRecomputeResult> RecomputePartitionNumbersAsync(int partitionCount, CancellationToken cancellationToken = default) => Task.FromResult(new PartitionRecomputeResult());
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) => Task.FromResult<PerspectiveCursorInfo?>(null);
    public Task<IReadOnlyList<MaintenanceResult>> PerformMaintenanceAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MaintenanceResult>>([]);
  }
}
