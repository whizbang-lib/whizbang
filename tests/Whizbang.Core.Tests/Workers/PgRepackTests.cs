// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Capabilities;
using Whizbang.Core.Messaging;
using Whizbang.Core.Processes;
using Whizbang.Core.Startup;
using Whizbang.Core.Workers;

#pragma warning disable CA1707 // Test method names use underscores by convention

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// #1753: pg_repack as a managed capability with two run modes. <c>InProcess</c>: the instance holding the
/// maintainer duty runs the <c>pg_repack</c> client program for each queued table, so the client is a probed
/// prerequisite (<c>program/pg_repack</c>). <c>External</c>: Whizbang never starts a process; it keeps the queue and
/// reports, and an operator's job runs the client. The connection's credentials reach the client through its
/// environment, never through its arguments. Every process start goes through <see cref="IExternalProcessRunner"/>,
/// so these tests need no client installed.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/PgRepackTableRewriter.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/PgRepackClient.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/PgRepackOptions.cs</code-under-test>
/// <docs>operations/infrastructure/storage-maintenance#pg-repack</docs>
[Category("Startup")]
public sealed class PgRepackTests {
  private const string MODE_KEY = "Whizbang:Workers:Maintenance:TableRewrite:PgRepack:Mode";
  private const string STRATEGY_KEY = "Whizbang:Workers:Maintenance:TableRewriteStrategy";
  private const string PASSWORD = "s3cret-Pa55";
  private const string USER = "table_owner";
  private const string HOST = "db.internal.example";
  private const string DATABASE = "orders";

  /// <summary>Records every process start and answers with the result it is given.</summary>
  private sealed class RecordingRunner : IExternalProcessRunner {
    public List<ExternalProcessRequest> Requests { get; } = [];
    public ExternalProcessResult Result { get; set; } = new(Started: true, ExitCode: 0, StandardOutput: "pg_repack 1.5.2\n", StandardError: "");

    public Task<ExternalProcessResult> RunAsync(ExternalProcessRequest request, CancellationToken cancellationToken) {
      Requests.Add(request);
      return Task.FromResult(Result);
    }
  }

  /// <summary>A coordinator that hands the rewriter the target the real one would, and reports its ratio.</summary>
  private sealed class TargetCoordinator : IWorkCoordinator {
    public double? RatioAfterRewrite { get; init; } = 1.1;
    public List<bool> Outcomes { get; } = [];

    public async Task<double?> RewriteTableWithAsync(
        string tableName, Func<TableRewriteTarget, CancellationToken, Task<bool>> rewrite, CancellationToken cancellationToken = default) {
      var target = new TableRewriteTarget($"\"public\".{tableName}", new Dictionary<string, string> {
        ["PGHOST"] = HOST,
        ["PGPORT"] = "5432",
        ["PGDATABASE"] = DATABASE,
        ["PGUSER"] = USER,
        ["PGPASSWORD"] = PASSWORD,
      });
      var done = await rewrite(target, cancellationToken);
      Outcomes.Add(done);
      return done ? RatioAfterRewrite : null;
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

  private static IOptions<MaintenanceWorkerOptions> _options(
      PgRepackMode mode, TableRewriteStrategy strategy = TableRewriteStrategy.PgRepack, string clientPath = "pg_repack") {
    var options = new MaintenanceWorkerOptions { TableRewriteStrategy = strategy };
    options.TableRewrite.PgRepack.Mode = mode;
    options.TableRewrite.PgRepack.ClientPath = clientPath;
    return Options.Create(options);
  }

  private static IConfiguration _configuration(params (string Key, string Value)[] settings) =>
    new ConfigurationBuilder()
      .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
      .Build();

  private static async Task<CapabilityRegistry> _probedAsync(PgRepackClientCapabilityProvider provider) {
    var registry = new CapabilityRegistry([provider], NullLogger<CapabilityRegistry>.Instance);
    await registry.RefreshAsync(CancellationToken.None);
    return registry;
  }

  private static ExternalProcessResult _notFound => new(Started: false, ExitCode: -1, StandardOutput: "",
    StandardError: "An error occurred trying to start process 'pg_repack': No such file or directory");

  // ---- options ----

  [Test]
  public async Task Options_DefaultToInProcess_WithTheClientFromThePathAsync() {
    var options = new MaintenanceWorkerOptions();

    await Assert.That(options.TableRewrite.PgRepack.Mode).IsEqualTo(PgRepackMode.InProcess);
    await Assert.That(options.TableRewrite.PgRepack.ClientPath).IsEqualTo("pg_repack");
  }

  [Test]
  public async Task Options_BindFromConfigurationThroughAddWhizbangWorkersAsync() {
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddSingleton(_configuration(
      (STRATEGY_KEY, "PgRepack"),
      (MODE_KEY, "External"),
      ("Whizbang:Workers:Maintenance:TableRewrite:PgRepack:ClientPath", "/opt/pg_repack/bin/pg_repack")));
    services.AddWhizbangWorkers();
    await using var provider = services.BuildServiceProvider();

    var options = provider.GetRequiredService<IOptions<MaintenanceWorkerOptions>>().Value;

    await Assert.That(options.TableRewriteStrategy).IsEqualTo(TableRewriteStrategy.PgRepack);
    await Assert.That(options.TableRewrite.PgRepack.Mode).IsEqualTo(PgRepackMode.External);
    await Assert.That(options.TableRewrite.PgRepack.ClientPath).IsEqualTo("/opt/pg_repack/bin/pg_repack");
    await Assert.That(provider.GetServices<ITableRewriter>().Select(r => r.Strategy)).Contains(TableRewriteStrategies.PG_REPACK)
      .Because("AddWhizbangWorkers wires the pg_repack rewriter beside VACUUM FULL");
    await Assert.That(provider.GetServices<ICapabilityProvider>().OfType<PgRepackClientCapabilityProvider>()).Count().IsEqualTo(1);
    await Assert.That(provider.GetServices<ICapabilityRequirementSource>().OfType<PgRepackClientRequirementSource>()).Count().IsEqualTo(1);
    await Assert.That(provider.GetRequiredService<IExternalProcessRunner>()).IsTypeOf<ExternalProcessRunner>();
  }

  [Test]
  public async Task Strategy_IsPgRepackAsync() {
    await Assert.That(TableRewriteStrategies.PG_REPACK).IsEqualTo("PgRepack")
      .Because("the name is the configuration value an operator writes");
    var rewriter = new PgRepackTableRewriter(_options(PgRepackMode.InProcess), new RecordingRunner(), null, NullLogger<PgRepackTableRewriter>.Instance);
    await Assert.That(rewriter.Strategy).IsEqualTo(TableRewriteStrategies.PG_REPACK);
  }

  [Test]
  public async Task Resolve_PgRepack_IsTheExplicitChoice_WhateverElseHoldsAsync() {
    foreach (var allow in new[] { true, false }) {
      foreach (var squeeze in new[] { true, false }) {
        var plan = TableRewriteStrategyResolver.Resolve(TableRewriteStrategy.PgRepack, allow, squeeze);
        await Assert.That(plan.Strategy).IsEqualTo(TableRewriteStrategies.PG_REPACK);
        await Assert.That(plan.Reason).IsEqualTo("PgRepack: online rewrite through pg_repack");
      }
    }
  }

  // ---- External never starts a process ----

  [Test]
  public async Task External_NeverStartsAProcess_NotToProbe_NotToRewriteAsync() {
    var runner = new RecordingRunner();
    var options = _options(PgRepackMode.External);
    var provider = new PgRepackClientCapabilityProvider(options, runner);
    var registry = await _probedAsync(provider);
    var rewriter = new PgRepackTableRewriter(options, runner, registry, NullLogger<PgRepackTableRewriter>.Instance);
    var coordinator = new TargetCoordinator();

    var canRun = rewriter.CanRun;
    var ratio = await rewriter.RewriteAsync(coordinator, "wh_outbox", CancellationToken.None);

    await Assert.That(provider.Scopes).IsEmpty().Because("in External mode the client is the operator's job, so it is not probed");
    await Assert.That(canRun).IsFalse();
    await Assert.That(rewriter.CannotRunReason).IsEqualTo(
      "pg_repack runs as an external job (" + MODE_KEY + " = External): Whizbang does not start it, and requested "
      + "tables stay queued for that job (wh_tables_needing_rewrite() lists them)");
    await Assert.That(ratio).IsNull();
    await Assert.That(coordinator.Outcomes).IsEmpty().Because("nothing is handed a connection in External mode");
    await Assert.That(runner.Requests).IsEmpty();
  }

  [Test]
  public async Task External_TheRewriteStepSkips_AndStartsNothingAsync() {
    var runner = new RecordingRunner();
    var options = _options(PgRepackMode.External);
    var registry = await _probedAsync(new PgRepackClientCapabilityProvider(options, runner));
    var services = new ServiceCollection().AddSingleton<IWorkCoordinator>(new TargetCoordinator()).BuildServiceProvider();
    var step = new TableRewriteStartupStep(services.GetRequiredService<IServiceScopeFactory>(),
      [new PgRepackTableRewriter(options, runner, registry, NullLogger<PgRepackTableRewriter>.Instance)],
      options, registry, NullLogger<TableRewriteStartupStep>.Instance);

    var report = await step.ExecuteAsync(CancellationToken.None);

    await Assert.That(report.Outcome).IsEqualTo(StartupStepOutcome.Skipped);
    await Assert.That(report.Reason).Contains("pg_repack runs as an external job");
    await Assert.That(runner.Requests).IsEmpty();
  }

  [Test]
  public async Task External_PutsNoClientRequirementInForceAsync() {
    var source = new PgRepackClientRequirementSource(_options(PgRepackMode.External));

    var active = source.Resolve(_configuration((STRATEGY_KEY, "PgRepack"), (MODE_KEY, "External")));

    await Assert.That(active).IsEmpty();
  }

  // ---- InProcess: the client is a probed prerequisite ----

  [Test]
  public async Task InProcess_WithNoClient_FailsAnExplicitRequirementAsync() {
    var runner = new RecordingRunner { Result = _notFound };
    var options = _options(PgRepackMode.InProcess);
    var registry = await _probedAsync(new PgRepackClientCapabilityProvider(options, runner));
    var active = new PgRepackClientRequirementSource(options)
      .Resolve(_configuration((STRATEGY_KEY, "PgRepack"), (MODE_KEY, "InProcess")));

    var verdict = CapabilityVerdict.Evaluate(registry.Statuses, active);

    var failure = verdict.Failures.Single();
    await Assert.That(failure.Outcome).IsEqualTo(CapabilityVerdictOutcome.Fail);
    await Assert.That(failure.Line).IsEqualTo(
      "capability program/pg_repack on 'pg_repack': Missing: An error occurred trying to start process 'pg_repack': "
      + "No such file or directory. Fix: install the pg_repack client program, the same version as the pg_repack "
      + "extension, on this host's PATH or set Whizbang:Workers:Maintenance:TableRewrite:PgRepack:ClientPath to it; or set "
      + MODE_KEY + " = External and run it as a job. Required by: Online table rewrite, in process (pg_repack client) ("
      + MODE_KEY + " = InProcess, explicit)");
  }

  [Test]
  public async Task InProcess_WithNoClient_DegradesAnImplicitRequirement_AndTheRewriterHoldsBackAsync() {
    var runner = new RecordingRunner { Result = _notFound };
    var options = _options(PgRepackMode.InProcess);
    var registry = await _probedAsync(new PgRepackClientCapabilityProvider(options, runner));
    var active = new PgRepackClientRequirementSource(options).Resolve(_configuration((STRATEGY_KEY, "PgRepack")));
    var rewriter = new PgRepackTableRewriter(options, runner, registry, NullLogger<PgRepackTableRewriter>.Instance);

    var verdict = CapabilityVerdict.Evaluate(registry.Statuses, active);

    var entry = verdict.Entries.Single();
    await Assert.That(entry.Outcome).IsEqualTo(CapabilityVerdictOutcome.Degrade)
      .Because("Mode was left at its default, so a missing client degrades rather than stopping start");
    await Assert.That(entry.Line).EndsWith("Required by: Online table rewrite, in process (pg_repack client) (" + MODE_KEY + " = InProcess, default)");
    await Assert.That(verdict.Failures).IsEmpty();
    await Assert.That(rewriter.CanRun).IsFalse();
    await Assert.That(rewriter.CannotRunReason).IsEqualTo(
      "the pg_repack client is not usable on this host (Missing): An error occurred trying to start process 'pg_repack': "
      + "No such file or directory; requested tables stay queued");
    await Assert.That(runner.Requests.Count).IsEqualTo(1).Because("the probe started the client once, to read its version; the rewriter started nothing");
  }

  [Test]
  public async Task InProcess_WithTheClient_IsReady_AndTheVersionIsReadAsync() {
    var runner = new RecordingRunner();
    var options = _options(PgRepackMode.InProcess, clientPath: "/usr/lib/postgresql/16/bin/pg_repack");
    var provider = new PgRepackClientCapabilityProvider(options, runner);
    var registry = await _probedAsync(provider);

    var status = registry.Statuses.Single();
    var rewriter = new PgRepackTableRewriter(options, runner, registry, NullLogger<PgRepackTableRewriter>.Instance);

    await Assert.That(provider.Name).IsEqualTo("program");
    await Assert.That(provider.Scopes).IsEquivalentTo(["/usr/lib/postgresql/16/bin/pg_repack"]);
    await Assert.That(status.State).IsEqualTo(CapabilityState.Ready);
    await Assert.That(status.Version).IsEqualTo("1.5.2");
    await Assert.That(status.Descriptor.Provisionable).IsFalse().Because("Whizbang never installs a program on the host");
    var probe = runner.Requests.Single();
    await Assert.That(probe.FileName).IsEqualTo("/usr/lib/postgresql/16/bin/pg_repack");
    await Assert.That(probe.Arguments).IsEquivalentTo(["--version"]);
    await Assert.That(probe.Environment).IsEmpty().Because("reading the version needs no connection, so no credentials");
    await Assert.That(rewriter.CanRun).IsTrue();
  }

  [Test]
  public async Task InProcess_AClientThatFailsToReportItsVersion_IsMissingWithItsErrorAsync() {
    var runner = new RecordingRunner { Result = new(Started: true, ExitCode: 127, StandardOutput: "", StandardError: "  error while loading shared libraries: libpq.so.5\n") };
    var registry = await _probedAsync(new PgRepackClientCapabilityProvider(_options(PgRepackMode.InProcess), runner));

    var status = registry.Statuses.Single();

    await Assert.That(status.State).IsEqualTo(CapabilityState.Missing);
    await Assert.That(status.Reason).IsEqualTo("pg_repack --version exited 127: error while loading shared libraries: libpq.so.5");
  }

  [Test]
  public async Task InProcess_AVersionLineWithNoVersion_IsReadyWithTheWholeLineAsync() {
    var runner = new RecordingRunner { Result = new(Started: true, ExitCode: 0, StandardOutput: "", StandardError: "") };
    var registry = await _probedAsync(new PgRepackClientCapabilityProvider(_options(PgRepackMode.InProcess), runner));

    await Assert.That(registry.Statuses.Single().State).IsEqualTo(CapabilityState.Ready);
    await Assert.That(registry.Statuses.Single().Version).IsNull();
  }

  [Test]
  public async Task AnotherStrategy_ProbesNothing_AndRequiresNoClientAsync() {
    var runner = new RecordingRunner();
    var options = _options(PgRepackMode.InProcess, TableRewriteStrategy.Auto);
    var provider = new PgRepackClientCapabilityProvider(options, runner);

    var registry = await _probedAsync(provider);
    var active = new PgRepackClientRequirementSource(options).Resolve(_configuration((MODE_KEY, "InProcess")));

    await Assert.That(provider.Scopes).IsEmpty();
    await Assert.That(registry.Statuses).IsEmpty();
    await Assert.That(runner.Requests).IsEmpty().Because("a host that does not use pg_repack never starts its client");
    await Assert.That(active).IsEmpty();
  }

  [Test]
  public async Task RequirementSource_WithoutConfiguration_IsImplicitAsync() {
    var active = new PgRepackClientRequirementSource(_options(PgRepackMode.InProcess)).Resolve(null);

    var requirement = active.Single();
    await Assert.That(requirement.Activation).IsEqualTo(CapabilityActivation.Implicit);
    await Assert.That(requirement.Requirement.Capability).IsEqualTo(PgRepackClient.CAPABILITY);
    await Assert.That(requirement.Key).IsEqualTo(MODE_KEY);
    await Assert.That(requirement.Value).IsEqualTo("InProcess");
  }

  [Test]
  public async Task RewriterWithoutARegistry_OrUnprobedOrUnknown_TriesTheClientAsync() {
    var options = _options(PgRepackMode.InProcess);
    var unknown = new CapabilityRegistry([new PgRepackClientCapabilityProvider(options, new RecordingRunner())], NullLogger<CapabilityRegistry>.Instance);
    var unprobed = new CapabilityRegistry([], NullLogger<CapabilityRegistry>.Instance);
    await unprobed.RefreshAsync(CancellationToken.None);

    await Assert.That(new PgRepackTableRewriter(options, new RecordingRunner(), null, NullLogger<PgRepackTableRewriter>.Instance).CanRun).IsTrue();
    await Assert.That(new PgRepackTableRewriter(options, new RecordingRunner(), unknown, NullLogger<PgRepackTableRewriter>.Instance).CanRun).IsTrue()
      .Because("Unknown means the probe could not decide; the run itself fails loudly if the client is missing");
    await Assert.That(new PgRepackTableRewriter(options, new RecordingRunner(), unprobed, NullLogger<PgRepackTableRewriter>.Instance).CanRun).IsTrue();
  }

  // ---- credentials never appear in the arguments ----

  [Test]
  public async Task InProcess_Rewrite_PassesTheConnectionThroughTheEnvironmentOnlyAsync() {
    var runner = new RecordingRunner();
    var options = _options(PgRepackMode.InProcess, clientPath: "/usr/bin/pg_repack");
    var rewriter = new PgRepackTableRewriter(options, runner, null, NullLogger<PgRepackTableRewriter>.Instance);
    var coordinator = new TargetCoordinator { RatioAfterRewrite = 1.05 };

    var ratio = await rewriter.RewriteAsync(coordinator, "wh_outbox", CancellationToken.None);

    await Assert.That(ratio).IsEqualTo(1.05);
    var request = runner.Requests.Single();
    await Assert.That(request.FileName).IsEqualTo("/usr/bin/pg_repack");
    await Assert.That(request.Arguments).IsEquivalentTo(
      ["--no-superuser-check", "--no-kill-backend", "--table=\"public\".wh_outbox"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    foreach (var argument in request.Arguments) {
      foreach (var secret in new[] { PASSWORD, USER, HOST, DATABASE }) {
        await Assert.That(argument).DoesNotContain(secret)
          .Because("process arguments are visible to every user on the host (ps, /proc/<pid>/cmdline); the connection is not");
      }
    }
    await Assert.That(request.Environment["PGPASSWORD"]).IsEqualTo(PASSWORD);
    await Assert.That(request.Environment["PGUSER"]).IsEqualTo(USER);
    await Assert.That(request.Environment["PGHOST"]).IsEqualTo(HOST);
    await Assert.That(request.Environment["PGDATABASE"]).IsEqualTo(DATABASE);
    await Assert.That(request.ToString()).DoesNotContain(PASSWORD).Because("a request written to a log must not carry the password");
  }

  [Test]
  public async Task InProcess_AClientThatFails_LeavesTheTableQueued_AndLogsWhyAsync() {
    var runner = new RecordingRunner { Result = new(Started: true, ExitCode: 1, StandardOutput: "", StandardError: "ERROR: pg_repack failed with error: ERROR:  permission denied for table wh_outbox\n") };
    var logger = new FakeLogger<PgRepackTableRewriter>();
    var rewriter = new PgRepackTableRewriter(_options(PgRepackMode.InProcess), runner, null, logger);
    var coordinator = new TargetCoordinator();

    var ratio = await rewriter.RewriteAsync(coordinator, "wh_outbox", CancellationToken.None);

    await Assert.That(ratio).IsNull().Because("nothing was rewritten, so the step leaves the request queued");
    await Assert.That(coordinator.Outcomes).IsEquivalentTo([false]);
    var record = logger.Collector.GetSnapshot().Single();
    await Assert.That(record.Level).IsEqualTo(Microsoft.Extensions.Logging.LogLevel.Warning);
    await Assert.That(record.Message).IsEqualTo(
      "pg_repack did not rewrite \"public\".wh_outbox: exited 1: ERROR: pg_repack failed with error: ERROR:  permission denied for table wh_outbox");
    await Assert.That(record.Message).DoesNotContain(PASSWORD);
  }

  [Test]
  public async Task InProcess_AClientThatCannotStart_LeavesTheTableQueued_AndLogsWhyAsync() {
    var logger = new FakeLogger<PgRepackTableRewriter>();
    var rewriter = new PgRepackTableRewriter(_options(PgRepackMode.InProcess), new RecordingRunner { Result = _notFound }, null, logger);

    var ratio = await rewriter.RewriteAsync(new TargetCoordinator(), "wh_outbox", CancellationToken.None);

    await Assert.That(ratio).IsNull();
    await Assert.That(logger.Collector.GetSnapshot().Single().Message).IsEqualTo(
      "pg_repack did not rewrite \"public\".wh_outbox: did not start: An error occurred trying to start process 'pg_repack': No such file or directory");
  }

  [Test]
  public async Task Rewrite_NullCoordinator_ThrowsAsync() {
    var rewriter = new PgRepackTableRewriter(_options(PgRepackMode.InProcess), new RecordingRunner(), null, NullLogger<PgRepackTableRewriter>.Instance);

    await Assert.That(() => rewriter.RewriteAsync(null!, "wh_outbox", CancellationToken.None)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Target_ToString_NamesOnlyTheTableAsync() {
    var target = new TableRewriteTarget("\"public\".wh_outbox", new Dictionary<string, string> { ["PGPASSWORD"] = PASSWORD });

    await Assert.That(target.ToString()).IsEqualTo("\"public\".wh_outbox");
  }

  [Test]
  public async Task Coordinator_WithoutAnExternalRewrite_RewritesNothingAsync() {
    IWorkCoordinator coordinator = new MinimalCoordinator();
    var called = false;

    var ratio = await coordinator.RewriteTableWithAsync("wh_outbox", (_, _) => {
      called = true;
      return Task.FromResult(true);
    });

    await Assert.That(ratio).IsNull();
    await Assert.That(called).IsFalse().Because("a coordinator that cannot hand out a connection never runs the rewrite");
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
}
