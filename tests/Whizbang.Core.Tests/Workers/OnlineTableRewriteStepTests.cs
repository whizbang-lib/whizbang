// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Capabilities;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Processes;
using Whizbang.Core.Tests.Capabilities;
using Whizbang.Core.Workers;

#pragma warning disable CA1707 // Test method names use underscores by convention

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The online table rewrite the maintenance cycle drives through pg_squeeze (#1743) or pg_repack in process (#1808):
/// requested tables from the <c>pending_table_rewrites</c> queue, largest bloat ratio first, within
/// <c>OnlineRewriteMaxBytesPerCycle</c>, only when the strategy's prerequisites are Ready (pg_squeeze; or the pg_repack
/// extension and its client program), and a request cleared only when the re-measure shows the rewrite improved the table.
/// </summary>
/// <docs>operations/infrastructure/storage-maintenance#online-rewrite</docs>
/// <code-under-test>src/Whizbang.Core/Workers/OnlineTableRewriteStep.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/PgSqueezeTableRewriter.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/PgRepackTableRewriter.cs</code-under-test>
public class OnlineTableRewriteStepTests {
  private const long MIB = 1024 * 1024;

  /// <summary>A coordinator that offers candidates and answers each squeeze with the ratio set for the table.</summary>
  private sealed class SqueezeCoordinator : IWorkCoordinator {
    public List<TableRewriteCandidate> Candidates { get; init; } = [];
    public Dictionary<string, double?> RatioAfter { get; init; } = new(StringComparer.Ordinal);
    public HashSet<string> Throws { get; init; } = new(StringComparer.Ordinal);
    public Exception? Failure { get; init; }
    public List<string> Squeezed { get; } = [];
    public List<string> Repacked { get; } = [];
    public List<string> Cleared { get; } = [];
    public int Scans { get; private set; }

    public Task<IReadOnlyList<TableRewriteCandidate>> GetTablesNeedingRewriteAsync(CancellationToken cancellationToken = default) {
      Scans++;
      return Task.FromResult<IReadOnlyList<TableRewriteCandidate>>(Candidates);
    }

    public Task<double?> SqueezeTableAsync(string tableName, CancellationToken cancellationToken = default) {
      Squeezed.Add(tableName);
      if (Throws.Contains(tableName)) {
        throw Failure ?? new InvalidOperationException($"squeeze of {tableName} failed");
      }
      return Task.FromResult(RatioAfter.GetValueOrDefault(tableName, 1.0));
    }

    public Task<double?> RewriteTableAsync(string tableName, CancellationToken cancellationToken = default) =>
      throw new NotSupportedException("the online rewrite never takes the blocking path");

    /// <summary>Hands the pg_repack rewriter a target, as the real coordinator does, and re-measures when it ran.</summary>
    public async Task<double?> RewriteTableWithAsync(
        string tableName, Func<TableRewriteTarget, CancellationToken, Task<bool>> rewrite, CancellationToken cancellationToken = default) {
      Repacked.Add(tableName);
      if (Throws.Contains(tableName)) {
        throw Failure ?? new InvalidOperationException($"pg_repack of {tableName} failed");
      }
      var target = new TableRewriteTarget($"\"public\".{tableName}", new Dictionary<string, string> { ["PGPASSWORD"] = "s3cret" });
      return await rewrite(target, cancellationToken) ? RatioAfter.GetValueOrDefault(tableName, 1.0) : null;
    }

    public Task ClearTableRewriteRequestAsync(string tableName, CancellationToken cancellationToken = default) {
      Cleared.Add(tableName);
      return Task.CompletedTask;
    }

    /// <summary>Each rewrite recorded as it was cleared (#2301).</summary>
    public List<TableRewriteOutcome> Completed { get; } = [];

    public Task CompleteTableRewriteAsync(TableRewriteOutcome outcome, CancellationToken cancellationToken = default) {
      Completed.Add(outcome);
      Cleared.Add(outcome.TableName);
      return Task.CompletedTask;
    }

    /// <summary>The ratio pgstattuple_approx measures after the rewrite, per table.</summary>
    public Dictionary<string, double?> ApproxAfter { get; init; } = new(StringComparer.Ordinal);

    /// <summary>When set, every measurement throws this.</summary>
    public Exception? MeasureThrows { get; init; }

    public List<(string Table, Whizbang.Core.Observability.BloatMeasurementMethod Method)> Measured { get; } = [];

    public Task<double?> MeasureTableBloatAsync(
        string tableName, Whizbang.Core.Observability.BloatMeasurementMethod method, CancellationToken cancellationToken = default) {
      Measured.Add((tableName, method));
      return MeasureThrows is not null
        ? Task.FromException<double?>(MeasureThrows)
        : Task.FromResult(ApproxAfter.GetValueOrDefault(tableName));
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

  private static TableRewriteCandidate _requested(string table, double ratio, long sizeBytes = MIB) =>
    new(table, ratio, Requested: true) { SizeBytes = sizeBytes };

  /// <summary>A registry whose pg_squeeze reads the given state on each database.</summary>
  private static async Task<CapabilityRegistry> _squeezeAsync(params CapabilityState[] perDatabase) {
    var provider = new FakeCapabilityProvider("postgres", [.. perDatabase.Select((_, i) => $"db-{i}")],
      FakeCapabilityProvider.Describe("postgres", "pg_squeeze"));
    var registry = new CapabilityRegistry([provider], NullLogger<CapabilityRegistry>.Instance);
    await registry.RefreshAsync(CancellationToken.None);
    registry.Record([.. registry.Statuses.Select((status, i) => status with { State = perDatabase[i] })]);
    return registry;
  }

  private static async Task<(SqueezeCoordinator Coordinator, FakeLogger<OnlineTableRewriteStep> Logger)> _runAsync(
      SqueezeCoordinator coordinator,
      long budgetBytes = 10 * 1024 * MIB,
      TableRewriteStrategy strategy = TableRewriteStrategy.PgSqueeze,
      ICapabilityRegistry? registry = null,
      bool noRegistry = false,
      IEnumerable<ITableRewriter>? rewriters = null) {
    var logger = new FakeLogger<OnlineTableRewriteStep>();
    var step = new OnlineTableRewriteStep(
      rewriters ?? [new PgSqueezeTableRewriter()],
      Options.Create(new MaintenanceWorkerOptions { TableRewriteStrategy = strategy, OnlineRewriteMaxBytesPerCycle = budgetBytes }),
      noRegistry ? null : registry ?? await _squeezeAsync(CapabilityState.Ready),
      logger);
    var services = new ServiceCollection().AddSingleton<IWorkCoordinator>(coordinator).BuildServiceProvider();
    await step.RunAsync(services, CancellationToken.None);
    return (coordinator, logger);
  }

  /// <summary>Starts nothing: answers every pg_repack run as a clean exit, and records it.</summary>
  private sealed class CleanExitRunner : IExternalProcessRunner {
    public List<ExternalProcessRequest> Requests { get; } = [];

    public Task<ExternalProcessResult> RunAsync(ExternalProcessRequest request, CancellationToken cancellationToken) {
      Requests.Add(request);
      return Task.FromResult(new ExternalProcessResult(Started: true, ExitCode: 0, StandardOutput: "", StandardError: ""));
    }
  }

  /// <summary>
  /// A registry holding the pg_repack extension's state on one database and the client program's on this host; a null
  /// client state means the client was never probed.
  /// </summary>
  private static async Task<CapabilityRegistry> _repackAsync(
      CapabilityState extension = CapabilityState.Ready, CapabilityState? client = CapabilityState.Ready) {
    var postgres = new FakeCapabilityProvider("postgres", ["db-0"], FakeCapabilityProvider.Describe("postgres", "pg_repack"));
    postgres.States["pg_repack"] = extension;
    var program = new FakeCapabilityProvider("program", client is null ? [] : ["pg_repack"],
      FakeCapabilityProvider.Describe("program", "pg_repack", provisionable: false));
    program.States["pg_repack"] = client ?? CapabilityState.Ready;
    var registry = new CapabilityRegistry([postgres, program], NullLogger<CapabilityRegistry>.Instance);
    await registry.RefreshAsync(CancellationToken.None);
    return registry;
  }

  private static async Task<(SqueezeCoordinator Coordinator, FakeLogger<OnlineTableRewriteStep> Logger, CleanExitRunner Runner)> _repackRunAsync(
      SqueezeCoordinator coordinator,
      long budgetBytes = 10 * 1024 * MIB,
      PgRepackMode mode = PgRepackMode.InProcess,
      ICapabilityRegistry? registry = null,
      bool noRegistry = false,
      bool noRepackRewriter = false) {
    var runner = new CleanExitRunner();
    var settings = new MaintenanceWorkerOptions { TableRewriteStrategy = TableRewriteStrategy.PgRepack, OnlineRewriteMaxBytesPerCycle = budgetBytes };
    settings.TableRewrite.PgRepack.Mode = mode;
    var options = Options.Create(settings);
    var capabilities = noRegistry ? null : registry ?? await _repackAsync();
    ITableRewriter[] rewriters = noRepackRewriter
      ? [new PgSqueezeTableRewriter()]
      : [new PgSqueezeTableRewriter(), new PgRepackTableRewriter(options, runner, capabilities, NullLogger<PgRepackTableRewriter>.Instance)];
    var logger = new FakeLogger<OnlineTableRewriteStep>();
    var step = new OnlineTableRewriteStep(rewriters, options, capabilities, logger);
    var services = new ServiceCollection().AddSingleton<IWorkCoordinator>(coordinator).BuildServiceProvider();
    await step.RunAsync(services, CancellationToken.None);
    return (coordinator, logger, runner);
  }

  private static IReadOnlyList<string> _messages(FakeLogger<OnlineTableRewriteStep> logger) =>
    [.. logger.Collector.GetSnapshot().Select(r => r.Message)];

  // ---- ordering ----

  [Test]
  public async Task Rewrites_TheLargestBloatRatioFirstAsync() {
    var (coordinator, _) = await _runAsync(new SqueezeCoordinator {
      Candidates = { _requested("wh_inbox", 3.5), _requested("wh_outbox", 9.0), _requested("wh_event_store", 5.25) },
    });

    await Assert.That(string.Join(",", coordinator.Squeezed)).IsEqualTo("wh_outbox,wh_event_store,wh_inbox");
  }

  [Test]
  public async Task Rewrites_TablesOfEqualRatio_InNameOrderAsync() {
    var (coordinator, _) = await _runAsync(new SqueezeCoordinator {
      Candidates = { _requested("wh_outbox", 4.0), _requested("wh_inbox", 4.0) },
    });

    await Assert.That(string.Join(",", coordinator.Squeezed)).IsEqualTo("wh_inbox,wh_outbox")
      .Because("ties are broken by name, so two cycles over the same queue rewrite in the same order");
  }

  [Test]
  public async Task Rewrites_OnlyTablesInTheQueueAsync() {
    var (coordinator, _) = await _runAsync(new SqueezeCoordinator {
      Candidates = { _requested("wh_outbox", 4.0), new TableRewriteCandidate("wh_inbox", 9.0, Requested: false) { SizeBytes = MIB } },
    });

    await Assert.That(coordinator.Squeezed).IsEquivalentTo(["wh_outbox"])
      .Because("the cycle drains pending_table_rewrites; detection records a table there before it is rewritten");
  }

  // ---- budget ----

  [Test]
  public async Task Budget_SkipsATableThatWouldOverrunIt_AndTakesASmallerOneThatFitsAsync() {
    var (coordinator, logger) = await _runAsync(new SqueezeCoordinator {
      Candidates = {
        _requested("wh_outbox", 9.0, 600 * MIB),
        _requested("wh_event_store", 7.0, 500 * MIB),
        _requested("wh_inbox", 4.0, 300 * MIB),
      },
    }, budgetBytes: 1000 * MIB);

    await Assert.That(string.Join(",", coordinator.Squeezed)).IsEqualTo("wh_outbox,wh_inbox")
      .Because("600 + 500 MiB overruns 1000 MiB; 600 + 300 MiB does not");
    await Assert.That(_messages(logger)).Contains(
      "Online rewrite: wh_event_store (524288000 bytes) is deferred to a later cycle: it does not fit the 419430400 bytes left "
      + "of Whizbang:Workers:Maintenance:OnlineRewriteMaxBytesPerCycle (1048576000)");
  }

  [Test]
  public async Task Budget_ExactlyFilled_RewritesBothAsync() {
    var (coordinator, _) = await _runAsync(new SqueezeCoordinator {
      Candidates = { _requested("wh_outbox", 9.0, 600 * MIB), _requested("wh_inbox", 4.0, 400 * MIB) },
    }, budgetBytes: 1000 * MIB);

    await Assert.That(string.Join(",", coordinator.Squeezed)).IsEqualTo("wh_outbox,wh_inbox");
  }

  [Test]
  public async Task Budget_ATableLargerThanTheWholeBudget_IsNeverRewritten_AndSaysWhatToRaiseAsync() {
    var (coordinator, logger) = await _runAsync(new SqueezeCoordinator {
      Candidates = { _requested("wh_event_store", 9.0, 2048 * MIB) },
    }, budgetBytes: 1024 * MIB);

    await Assert.That(coordinator.Squeezed).IsEmpty();
    await Assert.That(coordinator.Cleared).IsEmpty().Because("the request stays queued");
    await Assert.That(_messages(logger).Single(m => m.Contains("wh_event_store", StringComparison.Ordinal))).IsEqualTo(
      "Online rewrite: wh_event_store (2147483648 bytes) is larger than Whizbang:Workers:Maintenance:OnlineRewriteMaxBytesPerCycle "
      + "(1073741824), so no cycle rewrites it until the budget is raised");
  }

  [Test]
  public async Task Budget_OfZero_RewritesNothingAsync() {
    var (coordinator, _) = await _runAsync(new SqueezeCoordinator {
      Candidates = { _requested("wh_outbox", 9.0, 1) },
    }, budgetBytes: 0);

    await Assert.That(coordinator.Squeezed).IsEmpty();
  }

  // ---- a request is cleared only when the rewrite improved the table ----

  [Test]
  public async Task Improved_ClearsTheRequestAsync() {
    var (coordinator, logger) = await _runAsync(new SqueezeCoordinator {
      Candidates = { _requested("wh_outbox", 4.0) },
      RatioAfter = { ["wh_outbox"] = 1.25 },
    });

    await Assert.That(coordinator.Cleared).IsEquivalentTo(["wh_outbox"]);
    await Assert.That(_messages(logger)).Contains("Online rewrite: wh_outbox rewritten through pg_squeeze, bloat 4x (estimate) -> 1.25x (estimate)");
  }

  /// <summary>
  /// #2300: a table offered only because its fillfactor was lowered is compact, and rewriting it at the lower
  /// fillfactor makes it larger, not smaller. The request is honored by the rewrite itself, so it is cleared once the
  /// rewrite ran, whatever the re-measure says, and the next cycle does not rewrite it again.
  /// </summary>
  [Test]
  [Arguments(TableRewriteStrategy.PgSqueeze)]
  [Arguments(TableRewriteStrategy.PgRepack)]
  public async Task AFillFactorDecrease_IsClearedOnceRewritten_ThoughTheTableGrewAsync(TableRewriteStrategy strategy) {
    var coordinator = new SqueezeCoordinator {
      Candidates = { _requested("wh_per_order", 1.1) with { Reason = TableRewriteReason.FillFactorDecrease } },
      RatioAfter = { ["wh_per_order"] = 1.25 },
    };

    var logger = strategy == TableRewriteStrategy.PgSqueeze
      ? (await _runAsync(coordinator)).Logger
      : (await _repackRunAsync(coordinator)).Logger;

    await Assert.That(coordinator.Squeezed.Concat(coordinator.Repacked)).IsEquivalentTo(["wh_per_order"]);
    await Assert.That(coordinator.Cleared).IsEquivalentTo(["wh_per_order"])
      .Because("the rewrite applied the lowered fillfactor to every page, which is what the request asked for");
    await Assert.That(_messages(logger).Any(m => m.Contains("wh_per_order", StringComparison.Ordinal)
        && m.Contains("lowered fillfactor", StringComparison.Ordinal))).IsTrue();
    await Assert.That(_messages(logger).Any(m => m.Contains("did not reduce bloat", StringComparison.Ordinal))).IsFalse();
  }

  /// <summary>#2300: a fillfactor-decrease rewrite that did not run (the coordinator answered nothing) stays queued.</summary>
  [Test]
  public async Task AFillFactorDecrease_ThatWasNotRewritten_StaysQueuedAsync() {
    var (coordinator, _) = await _runAsync(new SqueezeCoordinator {
      Candidates = { _requested("wh_per_order", 1.1) with { Reason = TableRewriteReason.FillFactorDecrease } },
      RatioAfter = { ["wh_per_order"] = null },
    });

    await Assert.That(coordinator.Squeezed).IsEquivalentTo(["wh_per_order"]);
    await Assert.That(coordinator.Cleared).IsEmpty();
  }

  [Test]
  [Arguments(4.0)]
  [Arguments(4.5)]
  public async Task NotImproved_LeavesTheRequestQueuedAsync(double after) {
    var (coordinator, logger) = await _runAsync(new SqueezeCoordinator {
      Candidates = { _requested("wh_outbox", 4.0) },
      RatioAfter = { ["wh_outbox"] = after },
    });

    await Assert.That(coordinator.Squeezed).IsEquivalentTo(["wh_outbox"]);
    await Assert.That(coordinator.Cleared).IsEmpty()
      .Because("a rewrite that did not bring the ratio down did not do its job, and the next cycle retries it");
    await Assert.That(_messages(logger).Any(m => m.Contains("did not reduce bloat", StringComparison.Ordinal))).IsTrue();
  }

  [Test]
  public async Task NotRewritten_LeavesTheRequestQueuedAsync() {
    var (coordinator, _) = await _runAsync(new SqueezeCoordinator {
      Candidates = { _requested("wh_outbox", 4.0), _requested("wh_inbox", 3.5) },
      RatioAfter = { ["wh_outbox"] = null, ["wh_inbox"] = 1.0 },
    });

    await Assert.That(string.Join(",", coordinator.Squeezed)).IsEqualTo("wh_outbox,wh_inbox");
    await Assert.That(coordinator.Cleared).IsEquivalentTo(["wh_inbox"])
      .Because("a prerequisite the coordinator found unmet means nothing was rewritten, so only the other table is cleared");
  }

  [Test]
  public async Task AFailedRewrite_IsLoggedByTable_LeftQueued_AndTheNextTableStillRunsAsync() {
    var (coordinator, logger) = await _runAsync(new SqueezeCoordinator {
      Candidates = { _requested("wh_outbox", 9.0), _requested("wh_inbox", 4.0) },
      Throws = { "wh_outbox" },
    });

    await Assert.That(string.Join(",", coordinator.Squeezed)).IsEqualTo("wh_outbox,wh_inbox");
    await Assert.That(coordinator.Cleared).IsEquivalentTo(["wh_inbox"]);
    var failure = logger.Collector.GetSnapshot().Single(r => r.Level == LogLevel.Warning && r.Exception is not null);
    await Assert.That(failure.Message).IsEqualTo("Online rewrite of wh_outbox through pg_squeeze failed; the request stays queued");
  }

  [Test]
  public async Task Cancellation_IsNotSwallowedAsync() {
    var coordinator = new SqueezeCoordinator {
      Candidates = { _requested("wh_outbox", 9.0), _requested("wh_inbox", 4.0) },
      Throws = { "wh_outbox" },
      Failure = new OperationCanceledException(),
    };

    await Assert.That(async () => await _runAsync(coordinator)).Throws<OperationCanceledException>();
    await Assert.That(coordinator.Squeezed).IsEquivalentTo(["wh_outbox"]).Because("shutdown stops the cycle");
  }

  [Test]
  public async Task Summary_CountsWhatWasRewrittenAndWhatStaysQueuedAsync() {
    var (_, logger) = await _runAsync(new SqueezeCoordinator {
      Candidates = { _requested("wh_outbox", 9.0), _requested("wh_inbox", 4.0, 20 * 1024 * MIB) },
    });

    await Assert.That(_messages(logger)).Contains("Online rewrite: 1 of 2 queued table(s) rewritten this cycle; 1 left queued");
  }

  [Test]
  public async Task NothingQueued_RewritesNothing_AndLogsNothingAsync() {
    var (coordinator, logger) = await _runAsync(new SqueezeCoordinator {
      Candidates = { new TableRewriteCandidate("wh_outbox", 9.0, Requested: false) { SizeBytes = MIB } },
    });

    await Assert.That(coordinator.Scans).IsEqualTo(1);
    await Assert.That(coordinator.Squeezed).IsEmpty();
    await Assert.That(logger.Collector.Count).IsEqualTo(0);
  }

  // ---- prerequisites ----

  [Test]
  [Arguments(TableRewriteStrategy.Auto)]
  [Arguments(TableRewriteStrategy.VacuumFull)]
  [Arguments(TableRewriteStrategy.None)]
  public async Task OtherStrategies_DoNothing_NotEvenScanAsync(TableRewriteStrategy strategy) {
    var (coordinator, logger) = await _runAsync(new SqueezeCoordinator { Candidates = { _requested("wh_outbox", 9.0) } },
      strategy: strategy);

    await Assert.That(coordinator.Scans).IsEqualTo(0);
    await Assert.That(coordinator.Squeezed).IsEmpty();
    await Assert.That(logger.Collector.Count).IsEqualTo(0);
  }

  [Test]
  public async Task PgSqueezeNotReadyOnEveryDatabase_RewritesNothing_AndSaysWhereAsync() {
    var (coordinator, logger) = await _runAsync(new SqueezeCoordinator { Candidates = { _requested("wh_outbox", 9.0) } },
      registry: await _squeezeAsync(CapabilityState.Ready, CapabilityState.Provisionable));

    await Assert.That(coordinator.Scans).IsEqualTo(0);
    await Assert.That(_messages(logger).Single()).IsEqualTo(
      "Online rewrite skipped: pg_squeeze is Provisionable on 'db-1', not Ready; requested tables stay queued");
  }

  [Test]
  public async Task NoCapabilityRegistry_RewritesNothing_AndSaysWhyAsync() {
    var (coordinator, logger) = await _runAsync(new SqueezeCoordinator { Candidates = { _requested("wh_outbox", 9.0) } },
      noRegistry: true);

    await Assert.That(coordinator.Scans).IsEqualTo(0);
    await Assert.That(_messages(logger).Single()).IsEqualTo(
      "Online rewrite skipped: no capability registry is registered, so whether pg_squeeze is Ready is not known; requested tables stay queued");
  }

  [Test]
  public async Task ARegistryThatNeverProbedPgSqueeze_RewritesNothingAsync() {
    var registry = new CapabilityRegistry([new FakeCapabilityProvider("broker", ["vhost-a"], FakeCapabilityProvider.Describe("broker", "x"))],
      NullLogger<CapabilityRegistry>.Instance);
    await registry.RefreshAsync(CancellationToken.None);

    var (coordinator, logger) = await _runAsync(new SqueezeCoordinator { Candidates = { _requested("wh_outbox", 9.0) } },
      registry: registry);

    await Assert.That(coordinator.Scans).IsEqualTo(0);
    await Assert.That(_messages(logger).Single()).IsEqualTo(
      "Online rewrite skipped: pg_squeeze has not been probed on any database; requested tables stay queued");
  }

  [Test]
  public async Task NoPgSqueezeRewriterRegistered_RewritesNothingAsync() {
    var (coordinator, logger) = await _runAsync(new SqueezeCoordinator { Candidates = { _requested("wh_outbox", 9.0) } },
      rewriters: [new VacuumFullTableRewriter(Options.Create(new MaintenanceWorkerOptions { AllowTableRewrite = true }))]);

    await Assert.That(coordinator.Scans).IsEqualTo(0);
    await Assert.That(_messages(logger).Single()).IsEqualTo(
      "Online rewrite skipped: no table rewriter is registered for PgSqueeze; requested tables stay queued");
  }

  [Test]
  public async Task NoWorkCoordinator_DoesNothingAsync() {
    var step = new OnlineTableRewriteStep([new PgSqueezeTableRewriter()],
      Options.Create(new MaintenanceWorkerOptions { TableRewriteStrategy = TableRewriteStrategy.PgSqueeze }),
      await _squeezeAsync(CapabilityState.Ready), NullLogger<OnlineTableRewriteStep>.Instance);

    await step.RunAsync(new ServiceCollection().BuildServiceProvider(), CancellationToken.None);

    await Assert.That(step.Name).IsEqualTo("online-table-rewrite");
  }

  [Test]
  public async Task Constructor_RejectsMissingCollaboratorsAsync() {
    var options = Options.Create(new MaintenanceWorkerOptions());
    var logger = NullLogger<OnlineTableRewriteStep>.Instance;

    await Assert.That(() => new OnlineTableRewriteStep(null!, options, null, logger)).Throws<ArgumentNullException>();
    await Assert.That(() => new OnlineTableRewriteStep([], null!, null, logger)).Throws<ArgumentNullException>();
    await Assert.That(() => new OnlineTableRewriteStep([], options, null, null!)).Throws<ArgumentNullException>();
    await Assert.That(async () => await new OnlineTableRewriteStep([], options, null, logger).RunAsync(null!, CancellationToken.None))
      .Throws<ArgumentNullException>();
  }

  // ---- pg_repack in process (#1808): the same budgeted step ----

  [Test]
  public async Task PgRepack_RewritesTheLargestBloatRatioFirst_WithinTheBudgetAsync() {
    var (coordinator, logger, runner) = await _repackRunAsync(new SqueezeCoordinator {
      Candidates = {
        _requested("wh_inbox", 4.0, 300 * MIB),
        _requested("wh_outbox", 9.0, 600 * MIB),
        _requested("wh_event_store", 7.0, 500 * MIB),
      },
    }, budgetBytes: 1000 * MIB);

    await Assert.That(string.Join(",", coordinator.Repacked)).IsEqualTo("wh_outbox,wh_inbox")
      .Because("largest ratio first, and 600 + 500 MiB overruns 1000 MiB where 600 + 300 MiB does not");
    await Assert.That(coordinator.Squeezed).IsEmpty().Because("the pg_repack strategy never calls pg_squeeze");
    await Assert.That(runner.Requests.Select(r => r.Arguments[^1])).IsEquivalentTo(
      ["--table=\"public\".wh_outbox", "--table=\"public\".wh_inbox"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    foreach (var request in runner.Requests) {
      await Assert.That(string.Join(" ", request.Arguments)).DoesNotContain("s3cret")
        .Because("the connection reaches the client through its environment, never its arguments");
    }
    await Assert.That(_messages(logger)).Contains(
      "Online rewrite: wh_event_store (524288000 bytes) is deferred to a later cycle: it does not fit the 419430400 bytes left "
      + "of Whizbang:Workers:Maintenance:OnlineRewriteMaxBytesPerCycle (1048576000)");
  }

  [Test]
  public async Task PgRepack_ClearsOnlyWhatImprovedAsync() {
    var (coordinator, logger, _) = await _repackRunAsync(new SqueezeCoordinator {
      Candidates = { _requested("wh_outbox", 9.0), _requested("wh_inbox", 4.0) },
      RatioAfter = { ["wh_outbox"] = 1.5, ["wh_inbox"] = 4.0 },
    });

    await Assert.That(string.Join(",", coordinator.Repacked)).IsEqualTo("wh_outbox,wh_inbox");
    await Assert.That(coordinator.Cleared).IsEquivalentTo(["wh_outbox"])
      .Because("a rewrite that did not bring the ratio down stays queued for the next cycle");
    await Assert.That(_messages(logger)).Contains("Online rewrite: wh_outbox rewritten through pg_repack, bloat 9x (estimate) -> 1.5x (estimate)");
    await Assert.That(_messages(logger)).Contains(
      "Online rewrite: wh_inbox did not reduce bloat (4x (estimate) -> 4x (estimate)); the request stays queued");
  }

  [Test]
  public async Task PgRepack_ExtensionNotReady_RewritesNothing_AndSaysWhyAsync() {
    var (coordinator, logger, runner) = await _repackRunAsync(new SqueezeCoordinator { Candidates = { _requested("wh_outbox", 9.0) } },
      registry: await _repackAsync(extension: CapabilityState.Provisionable));

    await Assert.That(coordinator.Scans).IsEqualTo(0);
    await Assert.That(runner.Requests).IsEmpty();
    await Assert.That(_messages(logger).Single()).IsEqualTo(
      "Online rewrite skipped: pg_repack is Provisionable on 'db-0', not Ready; requested tables stay queued");
  }

  [Test]
  [Arguments(CapabilityState.Missing)]
  [Arguments(CapabilityState.Unknown)]
  public async Task PgRepack_ClientNotReady_RewritesNothing_AndSaysWhyAsync(CapabilityState client) {
    var (coordinator, logger, runner) = await _repackRunAsync(new SqueezeCoordinator { Candidates = { _requested("wh_outbox", 9.0) } },
      registry: await _repackAsync(client: client));

    await Assert.That(coordinator.Scans).IsEqualTo(0);
    await Assert.That(runner.Requests).IsEmpty().Because("the client is started only once its probe says Ready");
    await Assert.That(_messages(logger).Single()).IsEqualTo(
      $"Online rewrite skipped: the pg_repack client (program/pg_repack) is {client} on 'pg_repack', not Ready; requested tables stay queued");
  }

  [Test]
  public async Task PgRepack_ClientNeverProbed_RewritesNothing_AndSaysWhyAsync() {
    var (coordinator, logger, runner) = await _repackRunAsync(new SqueezeCoordinator { Candidates = { _requested("wh_outbox", 9.0) } },
      registry: await _repackAsync(client: null));

    await Assert.That(coordinator.Scans).IsEqualTo(0);
    await Assert.That(runner.Requests).IsEmpty();
    await Assert.That(_messages(logger).Single()).IsEqualTo(
      "Online rewrite skipped: the pg_repack client (program/pg_repack) has not been probed on this host; requested tables stay queued");
  }

  [Test]
  public async Task PgRepack_NoCapabilityRegistry_RewritesNothing_AndSaysWhyAsync() {
    var (coordinator, logger, _) = await _repackRunAsync(new SqueezeCoordinator { Candidates = { _requested("wh_outbox", 9.0) } },
      noRegistry: true);

    await Assert.That(coordinator.Scans).IsEqualTo(0);
    await Assert.That(_messages(logger).Single()).IsEqualTo(
      "Online rewrite skipped: no capability registry is registered, so whether pg_repack is Ready is not known; requested tables stay queued");
  }

  [Test]
  public async Task PgRepack_NoRewriterRegistered_RewritesNothingAsync() {
    var (coordinator, logger, _) = await _repackRunAsync(new SqueezeCoordinator { Candidates = { _requested("wh_outbox", 9.0) } },
      noRepackRewriter: true);

    await Assert.That(coordinator.Scans).IsEqualTo(0);
    await Assert.That(_messages(logger).Single()).IsEqualTo(
      "Online rewrite skipped: no table rewriter is registered for PgRepack; requested tables stay queued");
  }

  [Test]
  public async Task PgRepack_External_DoesNothing_NotEvenScan_AndStartsNothingAsync() {
    var (coordinator, logger, runner) = await _repackRunAsync(new SqueezeCoordinator { Candidates = { _requested("wh_outbox", 9.0) } },
      mode: PgRepackMode.External);

    await Assert.That(coordinator.Scans).IsEqualTo(0).Because("in External mode the operator's job drains the queue");
    await Assert.That(coordinator.Repacked).IsEmpty();
    await Assert.That(runner.Requests).IsEmpty();
    await Assert.That(logger.Collector.Count).IsEqualTo(0).Because("the boot step reports External once; the cycle does not repeat it");
  }

  [Test]
  public async Task PgRepack_AFailedRewrite_IsLoggedThroughPgRepack_AndLeftQueuedAsync() {
    var (coordinator, logger, _) = await _repackRunAsync(new SqueezeCoordinator {
      Candidates = { _requested("wh_outbox", 9.0) },
      Throws = { "wh_outbox" },
    });

    await Assert.That(coordinator.Cleared).IsEmpty();
    var failure = logger.Collector.GetSnapshot().Single(r => r.Level == LogLevel.Warning && r.Exception is not null);
    await Assert.That(failure.Message).IsEqualTo("Online rewrite of wh_outbox through pg_repack failed; the request stays queued");
  }

  // ---- the rewriter, the option and the registration ----

  [Test]
  public async Task PgSqueezeTableRewriter_SqueezesThroughTheCoordinatorAsync() {
    var rewriter = new PgSqueezeTableRewriter();
    var coordinator = new SqueezeCoordinator { RatioAfter = { ["wh_outbox"] = 1.5 } };

    var after = await rewriter.RewriteAsync(coordinator, "wh_outbox", CancellationToken.None);

    await Assert.That(after).IsEqualTo(1.5);
    await Assert.That(coordinator.Squeezed).IsEquivalentTo(["wh_outbox"]);
    await Assert.That(rewriter.Strategy).IsEqualTo(TableRewriteStrategies.PG_SQUEEZE);
    await Assert.That(rewriter.CanRun).IsTrue();
    await Assert.That(rewriter.CannotRunReason).Contains("capability verdict");
    await Assert.That(async () => await rewriter.RewriteAsync(null!, "wh_outbox", CancellationToken.None)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task TheDefaultCoordinator_SqueezesNothingAsync() {
    IWorkCoordinator coordinator = new MinimalCoordinator();

    await Assert.That(await coordinator.SqueezeTableAsync("wh_outbox")).IsNull();
  }

  private sealed class MinimalCoordinator : IWorkCoordinator {
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

  [Test]
  public async Task TheBudget_DefaultsToOneGibibyte_AndBindsFromConfigurationAsync() {
    await Assert.That(new MaintenanceWorkerOptions().OnlineRewriteMaxBytesPerCycle).IsEqualTo(1024 * MIB);
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
      ["Whizbang:Workers:Maintenance:OnlineRewriteMaxBytesPerCycle"] = "5368709120",
    }).Build());
    services.AddWhizbangWorkers();
    await using var provider = services.BuildServiceProvider();

    await Assert.That(provider.GetRequiredService<IOptions<MaintenanceWorkerOptions>>().Value.OnlineRewriteMaxBytesPerCycle)
      .IsEqualTo(5 * 1024 * MIB);
  }

  [Test]
  public async Task AddWhizbangWorkers_RegistersTheRewriterAndTheMaintenanceStepAsync() {
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddLogging();
    services.AddWhizbangWorkers();
    services.AddWhizbangWorkers();
    await using var provider = services.BuildServiceProvider();

    await Assert.That(provider.GetServices<ITableRewriter>().Select(r => r.Strategy))
      .Contains(TableRewriteStrategies.PG_SQUEEZE);
    await Assert.That(provider.GetServices<IMaintenanceStep>().OfType<OnlineTableRewriteStep>().Count()).IsEqualTo(1);
  }

  // ---- the re-measure after a rewrite uses pgstattuple_approx while it is Ready (#1807) ----

  /// <summary>A registry with pg_squeeze Ready and pgstattuple in <paramref name="pgstattuple"/> on one database.</summary>
  private static async Task<CapabilityRegistry> _squeezeWithPgstattupleAsync(CapabilityState pgstattuple) {
    var provider = new FakeCapabilityProvider("postgres", ["db-0"],
      FakeCapabilityProvider.Describe("postgres", "pg_squeeze"), FakeCapabilityProvider.Describe("postgres", "pgstattuple"));
    provider.States["pgstattuple"] = pgstattuple;
    var registry = new CapabilityRegistry([provider], NullLogger<CapabilityRegistry>.Instance);
    await registry.RefreshAsync(CancellationToken.None);
    return registry;
  }

  private static TableRewriteCandidate _measured(string table, double ratio) =>
    _requested(table, ratio) with { Method = Whizbang.Core.Observability.BloatMeasurementMethod.Approx };

  [Test]
  public async Task WhenPgstattupleIsReady_TheReMeasureIsApprox_AndItDecidesTheClearAsync() {
    var (coordinator, logger) = await _runAsync(new SqueezeCoordinator {
      Candidates = { _measured("wh_outbox", 4.0), _measured("wh_inbox", 4.0) },
      // The catalog estimate after the squeeze says both improved; the measurement says only one did.
      RatioAfter = { ["wh_outbox"] = 1.2, ["wh_inbox"] = 1.2 },
      ApproxAfter = { ["wh_outbox"] = 1.1, ["wh_inbox"] = 4.3 },
    }, registry: await _squeezeWithPgstattupleAsync(CapabilityState.Ready));

    await Assert.That(coordinator.Measured).IsEquivalentTo([
      ("wh_outbox", Whizbang.Core.Observability.BloatMeasurementMethod.Approx),
      ("wh_inbox", Whizbang.Core.Observability.BloatMeasurementMethod.Approx),
    ]).Because("did it shrink is answered by the same measurement that queued it, never the exact full scan");
    await Assert.That(coordinator.Cleared).IsEquivalentTo(["wh_outbox"]);
    await Assert.That(_messages(logger)).Contains(
      "Online rewrite: wh_outbox rewritten through pg_squeeze, bloat 4x (approx) -> 1.1x (approx)");
    await Assert.That(_messages(logger)).Contains(
      "Online rewrite: wh_inbox did not reduce bloat (4x (approx) -> 4.3x (approx)); the request stays queued");
  }

  [Test]
  public async Task WhilePgstattupleIsNotReady_TheReMeasureIsTheEstimateAsync() {
    var (coordinator, logger) = await _runAsync(new SqueezeCoordinator {
      Candidates = { _measured("wh_outbox", 4.0) },
      RatioAfter = { ["wh_outbox"] = 1.2 },
    }, registry: await _squeezeWithPgstattupleAsync(CapabilityState.NotPermitted));

    await Assert.That(coordinator.Measured).IsEmpty();
    await Assert.That(coordinator.Cleared).IsEquivalentTo(["wh_outbox"]);
    await Assert.That(_messages(logger)).Contains(
      "Online rewrite: wh_outbox rewritten through pg_squeeze, bloat 4x (approx) -> 1.2x (estimate)");
  }

  [Test]
  public async Task WhenTheApproxReMeasureFails_TheEstimateStandsIn_AndTheFailureIsLoggedAsync() {
    var (coordinator, logger) = await _runAsync(new SqueezeCoordinator {
      Candidates = { _measured("wh_outbox", 4.0) },
      RatioAfter = { ["wh_outbox"] = 1.2 },
      MeasureThrows = new InvalidOperationException("permission denied for function pgstattuple_approx"),
    }, registry: await _squeezeWithPgstattupleAsync(CapabilityState.Ready));

    await Assert.That(coordinator.Cleared).IsEquivalentTo(["wh_outbox"]);
    await Assert.That(_messages(logger)).Contains(
      "Measuring the bloat of wh_outbox with pgstattuple_approx failed; the estimate stands in for it");
    await Assert.That(_messages(logger)).Contains(
      "Online rewrite: wh_outbox rewritten through pg_squeeze, bloat 4x (approx) -> 1.2x (estimate)");
  }

  [Test]
  public async Task WhenNothingWasRewritten_NothingIsMeasuredAsync() {
    var (coordinator, _) = await _runAsync(new SqueezeCoordinator {
      Candidates = { _measured("wh_outbox", 4.0) },
      RatioAfter = { ["wh_outbox"] = null },
      ApproxAfter = { ["wh_outbox"] = 1.0 },
    }, registry: await _squeezeWithPgstattupleAsync(CapabilityState.Ready));

    await Assert.That(coordinator.Measured).IsEmpty()
      .Because("a rewrite that did not run left nothing to re-measure, and a scan would only cost");
    await Assert.That(coordinator.Cleared).IsEmpty();
  }

  // ---- #2301: Online picks pg_squeeze, then pg_repack, then a blue-green rebuild of a perspective table ----

  private const string ORDER_PERSPECTIVE = "Tests.OrderPerspective";

  /// <summary>A server on which pg_squeeze, the pg_repack extension and its client read the given states.</summary>
  private static async Task<CapabilityRegistry> _serverAsync(
      CapabilityState squeeze, CapabilityState repack = CapabilityState.NotPermitted, CapabilityState client = CapabilityState.Ready) {
    var postgres = new FakeCapabilityProvider("postgres", ["db-0"],
      FakeCapabilityProvider.Describe("postgres", "pg_squeeze"), FakeCapabilityProvider.Describe("postgres", "pg_repack"));
    postgres.States["pg_squeeze"] = squeeze;
    postgres.States["pg_repack"] = repack;
    var program = new FakeCapabilityProvider("program", ["pg_repack"], FakeCapabilityProvider.Describe("program", "pg_repack", provisionable: false));
    program.States["pg_repack"] = client;
    var registry = new CapabilityRegistry([postgres, program], NullLogger<CapabilityRegistry>.Instance);
    await registry.RefreshAsync(CancellationToken.None);
    return registry;
  }

  /// <summary>Which perspective writes each table, and which tables another rebuild holds.</summary>
  private sealed class RebuildSwapper : IPerspectiveTableSwapper {
    public Dictionary<string, string> Perspectives { get; init; } = new(StringComparer.Ordinal);
    public HashSet<string> Held { get; init; } = new(StringComparer.Ordinal);
    public List<string> Guarded { get; } = [];

    public Task<string?> FindPerspectiveAsync(string table, CancellationToken cancellationToken) =>
      Task.FromResult(Perspectives.GetValueOrDefault(table));

    public Task<IAsyncDisposable?> TryLockRebuildAsync(string liveTable, CancellationToken cancellationToken) {
      if (Held.Contains(liveTable)) {
        return Task.FromResult<IAsyncDisposable?>(null);
      }
      Guarded.Add(liveTable);
      return Task.FromResult<IAsyncDisposable?>(NoRebuildGuard.Instance);
    }

    public Task<string?> FindTableAsync(string modelTypeName, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<string> CreateShadowAsync(string liveTable, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task DeleteRowsAsync(string table, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<string?> SwapAsync(PerspectiveTableSwap swap, Func<CancellationToken, Task> underWriteLock, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task DropAsync(string table, CancellationToken cancellationToken) => throw new NotSupportedException();
  }

  /// <summary>Records each blue-green rebuild it is asked for; fails or reports in flight the perspectives named.</summary>
  private sealed class RecordingRebuilder : IPerspectiveRebuilder {
    public List<(string Perspective, RebuildTrigger Trigger)> Rebuilt { get; } = [];
    public HashSet<string> Fails { get; init; } = new(StringComparer.Ordinal);
    public HashSet<string> InFlight { get; init; } = new(StringComparer.Ordinal);

    public Task<RebuildResult> RebuildBlueGreenAsync(string perspectiveName, RebuildOrigin origin, CancellationToken ct = default) {
      Rebuilt.Add((perspectiveName, origin.Trigger));
      return Task.FromResult(new RebuildResult(perspectiveName, 3, 9, TimeSpan.FromSeconds(1), !Fails.Contains(perspectiveName),
        Fails.Contains(perspectiveName) ? "the replay failed" : null));
    }

    public Task<RebuildResult> RebuildBlueGreenAsync(string perspectiveName, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<RebuildStatus?> GetRebuildStatusAsync(string perspectiveName, CancellationToken ct = default) =>
      Task.FromResult(InFlight.Contains(perspectiveName)
        ? new RebuildStatus(perspectiveName, RebuildMode.BlueGreen, 10, 4, DateTimeOffset.UnixEpoch)
        : null);

    public Task<RebuildResult> RebuildInPlaceAsync(string perspectiveName, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<RebuildResult> RebuildStreamsAsync(string perspectiveName, IEnumerable<Guid> streamIds, CancellationToken ct = default) => throw new NotSupportedException();
  }

  private static async Task<(SqueezeCoordinator Coordinator, FakeLogger<OnlineTableRewriteStep> Logger)> _onlineAsync(
      SqueezeCoordinator coordinator, ICapabilityRegistry registry, RebuildSwapper? swapper, RecordingRebuilder? rebuilder,
      int maxRebuildsPerCycle = 1, TableRewriteStrategy strategy = TableRewriteStrategy.Online,
      PgRepackMode mode = PgRepackMode.InProcess, long budgetBytes = 10 * 1024 * MIB,
      FakeLogger<BlueGreenTableRewriter>? rewriterLogger = null) {
    var settings = new MaintenanceWorkerOptions {
      TableRewriteStrategy = strategy,
      BlueGreenRewriteMaxPerCycle = maxRebuildsPerCycle,
      OnlineRewriteMaxBytesPerCycle = budgetBytes,
    };
    settings.TableRewrite.PgRepack.Mode = mode;
    var options = Options.Create(settings);
    var collection = new ServiceCollection().AddSingleton<IWorkCoordinator>(coordinator);
    if (swapper is not null) {
      collection.AddSingleton<IPerspectiveTableSwapper>(swapper);
    }
    if (rebuilder is not null) {
      collection.AddSingleton<IPerspectiveRebuilder>(rebuilder);
    }
    var services = collection.BuildServiceProvider();
    var logger = new FakeLogger<OnlineTableRewriteStep>();
    var step = new OnlineTableRewriteStep(
      [new PgSqueezeTableRewriter(),
       new PgRepackTableRewriter(options, new CleanExitRunner(), registry, NullLogger<PgRepackTableRewriter>.Instance),
       new BlueGreenTableRewriter(services.GetRequiredService<IServiceScopeFactory>(),
         rewriterLogger ?? (ILogger<BlueGreenTableRewriter>)NullLogger<BlueGreenTableRewriter>.Instance)],
      options, registry, logger);
    await step.RunAsync(services, CancellationToken.None);
    return (coordinator, logger);
  }

  private static RebuildSwapper _ordersSwapper(params string[] tables) =>
    new() { Perspectives = tables.ToDictionary(t => t, t => ORDER_PERSPECTIVE + "." + t, StringComparer.Ordinal) };

  [Test]
  public async Task Online_WithPgSqueezeReady_RewritesThroughIt_AndRebuildsNothingAsync() {
    var rebuilder = new RecordingRebuilder();
    var (coordinator, _) = await _onlineAsync(new SqueezeCoordinator { Candidates = { _requested("wh_per_order", 4.0) } },
      await _serverAsync(CapabilityState.Ready), _ordersSwapper("wh_per_order"), rebuilder);

    await Assert.That(coordinator.Squeezed).IsEquivalentTo(["wh_per_order"]);
    await Assert.That(rebuilder.Rebuilt).IsEmpty().Because("a rebuild replays every stream; pg_squeeze copies rows, so it goes first");
    await Assert.That(coordinator.Completed.Single().Strategy).IsEqualTo(TableRewriteStrategies.PG_SQUEEZE);
  }

  [Test]
  public async Task Online_WithPgSqueezeNotReady_AndPgRepackReady_RepacksAsync() {
    var rebuilder = new RecordingRebuilder();
    var (coordinator, _) = await _onlineAsync(new SqueezeCoordinator { Candidates = { _requested("wh_per_order", 4.0) } },
      await _serverAsync(CapabilityState.NotPermitted, CapabilityState.Ready), _ordersSwapper("wh_per_order"), rebuilder);

    await Assert.That(coordinator.Squeezed).IsEmpty();
    await Assert.That(coordinator.Repacked).IsEquivalentTo(["wh_per_order"]);
    await Assert.That(rebuilder.Rebuilt).IsEmpty();
    await Assert.That(coordinator.Completed.Single().Strategy).IsEqualTo(TableRewriteStrategies.PG_REPACK);
  }

  /// <summary>
  /// #2301: with neither extension usable, a perspective table is rewritten by a blue-green rebuild of the perspective
  /// that writes it, recorded as such, and a framework table, which no rebuild can recreate from events, waits and the
  /// cycle says so.
  /// </summary>
  [Test]
  public async Task Online_WithNoExtensionUsable_RebuildsThePerspectiveTable_AndAFrameworkTableWaitsAsync() {
    var rebuilder = new RecordingRebuilder();
    var (coordinator, logger) = await _onlineAsync(new SqueezeCoordinator {
      Candidates = { _requested("wh_outbox", 6.0), _requested("wh_per_order", 1.1) with { Reason = TableRewriteReason.FillFactorDecrease } },
      ApproxAfter = { ["wh_per_order"] = 1.3 },
    }, await _serverAsync(CapabilityState.NotPermitted, CapabilityState.NotPermitted), _ordersSwapper("wh_per_order"), rebuilder);

    await Assert.That(rebuilder.Rebuilt).IsEquivalentTo([(ORDER_PERSPECTIVE + ".wh_per_order", RebuildTrigger.TableRewrite)]);
    await Assert.That(coordinator.Squeezed.Concat(coordinator.Repacked)).IsEmpty();
    var outcome = coordinator.Completed.Single();
    await Assert.That(outcome).IsEqualTo(new TableRewriteOutcome(
      "wh_per_order", TableRewriteStrategies.BLUE_GREEN, TableRewriteReason.FillFactorDecrease, 1.1, 1.3));
    await Assert.That(coordinator.Cleared).DoesNotContain("wh_outbox");
    var waiting = _messages(logger).Where(m => m.Contains("wh_outbox", StringComparison.Ordinal)).ToList();
    await Assert.That(waiting).Count().IsEqualTo(1);
    await Assert.That(waiting[0]).Contains("not a perspective table").And.Contains("pg_squeeze").And.Contains("pg_repack");
  }

  /// <summary>#2301: at most <c>BlueGreenRewriteMaxPerCycle</c> perspectives are rebuilt in one cycle, largest bloat first.</summary>
  [Test]
  [Arguments(1, "wh_per_a")]
  [Arguments(2, "wh_per_a,wh_per_c")]
  [Arguments(0, "")]
  public async Task Online_RebuildsAtMostTheCycleLimit_LargestBloatFirstAsync(int limit, string expected) {
    var rebuilder = new RecordingRebuilder();
    var (coordinator, logger) = await _onlineAsync(new SqueezeCoordinator {
      Candidates = { _requested("wh_per_a", 9.0), _requested("wh_per_b", 4.0), _requested("wh_per_c", 6.0) },
      ApproxAfter = { ["wh_per_a"] = 1.0, ["wh_per_b"] = 1.0, ["wh_per_c"] = 1.0 },
    }, await _serverAsync(CapabilityState.NotPermitted), _ordersSwapper("wh_per_a", "wh_per_b", "wh_per_c"), rebuilder, limit);

    await Assert.That(string.Join(",", rebuilder.Rebuilt.Select(r => r.Perspective[(ORDER_PERSPECTIVE.Length + 1)..]))).IsEqualTo(expected);
    await Assert.That(string.Join(",", coordinator.Cleared)).IsEqualTo(expected);
    await Assert.That(_messages(logger).Any(m => m.Contains("Whizbang:Workers:Maintenance:BlueGreenRewriteMaxPerCycle", StringComparison.Ordinal)))
      .IsEqualTo(limit < 3).Because("a table left for the next cycle by the limit is named with the setting that holds it back");
  }

  /// <summary>
  /// #2301: a table another blue-green rebuild holds, on any instance, is not rebuilt again; the request stays queued
  /// for a later cycle, and the next table is taken in its place, since a held table does not spend the limit.
  /// </summary>
  [Test]
  public async Task Online_ATableAnotherRebuildHolds_IsNotRebuiltAgain_AndStaysQueuedAsync() {
    var rebuilder = new RecordingRebuilder();
    var swapper = _ordersSwapper("wh_per_a", "wh_per_b");
    swapper.Held.Add("wh_per_a");
    var (coordinator, logger) = await _onlineAsync(new SqueezeCoordinator {
      Candidates = { _requested("wh_per_a", 9.0), _requested("wh_per_b", 4.0) },
      ApproxAfter = { ["wh_per_b"] = 1.0 },
    }, await _serverAsync(CapabilityState.NotPermitted), swapper, rebuilder);

    await Assert.That(rebuilder.Rebuilt.Select(r => r.Perspective)).IsEquivalentTo([ORDER_PERSPECTIVE + ".wh_per_b"]);
    await Assert.That(coordinator.Cleared).IsEquivalentTo(["wh_per_b"]);
    await Assert.That(_messages(logger).Any(m => m.Contains("wh_per_a", StringComparison.Ordinal)
      && m.Contains("in flight", StringComparison.Ordinal))).IsTrue();
  }

  /// <summary>#2301: a rebuild of the perspective already running in this process satisfies the request for now.</summary>
  [Test]
  public async Task Online_APerspectiveAlreadyRebuildingHere_IsNotRebuiltAgainAsync() {
    var rebuilder = new RecordingRebuilder { InFlight = { ORDER_PERSPECTIVE + ".wh_per_a" } };
    var (coordinator, _) = await _onlineAsync(new SqueezeCoordinator { Candidates = { _requested("wh_per_a", 9.0) } },
      await _serverAsync(CapabilityState.NotPermitted), _ordersSwapper("wh_per_a"), rebuilder);

    await Assert.That(rebuilder.Rebuilt).IsEmpty();
    await Assert.That(coordinator.Cleared).IsEmpty();
  }

  /// <summary>#2301: a rebuild that fails leaves the request queued, as a failed rewrite of any strategy does.</summary>
  [Test]
  public async Task Online_ARebuildThatFails_LeavesTheRequestQueuedAsync() {
    var rebuilder = new RecordingRebuilder { Fails = { ORDER_PERSPECTIVE + ".wh_per_a" } };
    var rewriterLogger = new FakeLogger<BlueGreenTableRewriter>();
    var (coordinator, _) = await _onlineAsync(new SqueezeCoordinator { Candidates = { _requested("wh_per_a", 9.0) } },
      await _serverAsync(CapabilityState.NotPermitted), _ordersSwapper("wh_per_a"), rebuilder, rewriterLogger: rewriterLogger);

    await Assert.That(rebuilder.Rebuilt).Count().IsEqualTo(1);
    await Assert.That(coordinator.Cleared).IsEmpty();
    await Assert.That(rewriterLogger.Collector.GetSnapshot().Any(r => r.Message.Contains("wh_per_a", StringComparison.Ordinal)
      && r.Message.Contains("the replay failed", StringComparison.Ordinal))).IsTrue();
  }

  /// <summary>#2301: a host with no swapper or no rebuilder can rebuild nothing, so every table waits.</summary>
  [Test]
  [Arguments(false, true)]
  [Arguments(true, false)]
  public async Task Online_WithNoSwapperOrRebuilder_EveryTableWaitsAsync(bool withSwapper, bool withRebuilder) {
    var rebuilder = new RecordingRebuilder();
    var (coordinator, _) = await _onlineAsync(new SqueezeCoordinator { Candidates = { _requested("wh_per_a", 9.0) } },
      await _serverAsync(CapabilityState.NotPermitted), withSwapper ? _ordersSwapper("wh_per_a") : null,
      withRebuilder ? rebuilder : null);

    await Assert.That(rebuilder.Rebuilt).IsEmpty();
    await Assert.That(coordinator.Cleared).IsEmpty();
  }

  /// <summary>#2301: under Online, pg_repack run by an external job is not Whizbang's to start, so the cycle rebuilds.</summary>
  [Test]
  public async Task Online_WithPgRepackExternal_FallsThroughToTheRebuildAsync() {
    var rebuilder = new RecordingRebuilder();
    var (coordinator, _) = await _onlineAsync(new SqueezeCoordinator { Candidates = { _requested("wh_per_a", 9.0) }, ApproxAfter = { ["wh_per_a"] = 1.0 } },
      await _serverAsync(CapabilityState.NotPermitted, CapabilityState.Ready), _ordersSwapper("wh_per_a"), rebuilder,
      mode: PgRepackMode.External);

    await Assert.That(coordinator.Repacked).IsEmpty();
    await Assert.That(rebuilder.Rebuilt).Count().IsEqualTo(1);
  }

  /// <summary>
  /// #2301: only <c>Online</c> rebuilds. <c>Auto</c> and <c>None</c> start no online rewrite of any kind, and an
  /// explicit <c>PgRepack</c> whose client is not Ready rewrites nothing, as before, rather than rebuilding.
  /// </summary>
  [Test]
  [Arguments(TableRewriteStrategy.Auto)]
  [Arguments(TableRewriteStrategy.None)]
  [Arguments(TableRewriteStrategy.VacuumFull)]
  [Arguments(TableRewriteStrategy.PgRepack)]
  public async Task OnlyOnline_EverRebuildsAsync(TableRewriteStrategy strategy) {
    var rebuilder = new RecordingRebuilder();
    var (coordinator, _) = await _onlineAsync(new SqueezeCoordinator { Candidates = { _requested("wh_per_a", 9.0) } },
      await _serverAsync(CapabilityState.NotPermitted, CapabilityState.Ready, CapabilityState.Missing), _ordersSwapper("wh_per_a"),
      rebuilder, strategy: strategy);

    await Assert.That(rebuilder.Rebuilt).IsEmpty();
    await Assert.That(coordinator.Cleared).IsEmpty();
  }

  /// <summary>#2301: every strategy records which one carried the request out, as the request is cleared.</summary>
  [Test]
  public async Task TheStrategyThatCarriedOutTheRequest_IsRecordedAsync() {
    var (coordinator, _) = await _runAsync(new SqueezeCoordinator {
      Candidates = { _requested("wh_outbox", 4.0) },
      RatioAfter = { ["wh_outbox"] = 1.25 },
    });

    await Assert.That(coordinator.Completed).IsEquivalentTo([
      new TableRewriteOutcome("wh_outbox", TableRewriteStrategies.PG_SQUEEZE, TableRewriteReason.Bloat, 4.0, 1.25)]);
  }

  /// <summary>
  /// #2301: called directly, the blue-green rewriter rewrites nothing for a table no single rebuild can recreate, and
  /// says it can always run, since it needs nothing beyond Whizbang.
  /// </summary>
  [Test]
  public async Task BlueGreenRewriter_OfATableNoRebuildCanRecreate_RewritesNothingAsync() {
    var rebuilder = new RecordingRebuilder();
    var services = new ServiceCollection()
      .AddSingleton<IPerspectiveTableSwapper>(_ordersSwapper("wh_per_a"))
      .AddSingleton<IPerspectiveRebuilder>(rebuilder)
      .BuildServiceProvider();
    var rewriter = new BlueGreenTableRewriter(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<BlueGreenTableRewriter>.Instance);

    var ratio = await rewriter.RewriteAsync(new SqueezeCoordinator(), "wh_outbox", CancellationToken.None);

    await Assert.That(ratio).IsNull();
    await Assert.That(rebuilder.Rebuilt).IsEmpty();
    await Assert.That(rewriter.CanRun).IsTrue();
    await Assert.That(rewriter.CannotRunReason).StartsWith("none");
    await Assert.That(rewriter.Strategy).IsEqualTo(TableRewriteStrategies.BLUE_GREEN);
    await Assert.That(() => rewriter.RewriteAsync(null!, "wh_per_a", CancellationToken.None)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task BlueGreenRewriteMaxPerCycle_DefaultsToOne_AndBindsFromConfigurationAsync() {
    await Assert.That(new MaintenanceWorkerOptions().BlueGreenRewriteMaxPerCycle).IsEqualTo(1);
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
      ["Whizbang:Workers:Maintenance:BlueGreenRewriteMaxPerCycle"] = "3",
    }).Build());
    services.AddWhizbangWorkers();
    await using var provider = services.BuildServiceProvider();

    await Assert.That(provider.GetRequiredService<IOptions<MaintenanceWorkerOptions>>().Value.BlueGreenRewriteMaxPerCycle).IsEqualTo(3);
  }

  [Test]
  public async Task AddWhizbangWorkers_RegistersTheBlueGreenRewriterAsync() {
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddLogging();
    services.AddWhizbangWorkers();
    await using var provider = services.BuildServiceProvider();

    await Assert.That(provider.GetServices<ITableRewriter>().Count(r => r.Strategy == TableRewriteStrategies.BLUE_GREEN)).IsEqualTo(1);
  }
}
