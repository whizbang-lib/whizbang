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
using Whizbang.Core.Tests.Capabilities;
using Whizbang.Core.Workers;

#pragma warning disable CA1707 // Test method names use underscores by convention

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The online table rewrite the maintenance cycle drives through pg_squeeze (#1743): requested tables from the
/// <c>pending_table_rewrites</c> queue, largest bloat ratio first, within <c>OnlineRewriteMaxBytesPerCycle</c>, only
/// when <c>TableRewriteStrategy = PgSqueeze</c> and pg_squeeze is Ready, and a request cleared only when the
/// re-measure shows the rewrite improved the table.
/// </summary>
/// <docs>operations/infrastructure/storage-maintenance#online-rewrite</docs>
/// <code-under-test>src/Whizbang.Core/Workers/OnlineTableRewriteStep.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/PgSqueezeTableRewriter.cs</code-under-test>
public class OnlineTableRewriteStepTests {
  private const long MIB = 1024 * 1024;

  /// <summary>A coordinator that offers candidates and answers each squeeze with the ratio set for the table.</summary>
  private sealed class SqueezeCoordinator : IWorkCoordinator {
    public List<TableRewriteCandidate> Candidates { get; init; } = [];
    public Dictionary<string, double?> RatioAfter { get; init; } = new(StringComparer.Ordinal);
    public HashSet<string> Throws { get; init; } = new(StringComparer.Ordinal);
    public Exception? Failure { get; init; }
    public List<string> Squeezed { get; } = [];
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

    public Task ClearTableRewriteRequestAsync(string tableName, CancellationToken cancellationToken = default) {
      Cleared.Add(tableName);
      return Task.CompletedTask;
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
    await Assert.That(_messages(logger)).Contains("Online rewrite: wh_outbox rewritten through pg_squeeze, bloat 4x -> 1.25x");
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
    await using var provider = services.BuildServiceProvider();

    await Assert.That(provider.GetServices<ITableRewriter>().Select(r => r.Strategy))
      .Contains(TableRewriteStrategies.PG_SQUEEZE);
    await Assert.That(provider.GetServices<IMaintenanceStep>().OfType<OnlineTableRewriteStep>().Count()).IsEqualTo(1);
  }
}
