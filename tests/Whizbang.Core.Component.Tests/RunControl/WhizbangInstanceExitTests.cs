// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.RunControl;
using Whizbang.Core.Startup;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Component.Tests.RunControl;

/// <summary>
/// The one exit path (#2126). Every exit, intended or not, halts through the run-control path, writes one detailed entry,
/// records the same facts durably when it can (and says when it could not), sets the exit code from one place and stops
/// the host. The first cause wins: the process exits once.
/// </summary>
/// <remarks>
/// The clock is a <see cref="FakeTimeProvider"/>: uptime and the heartbeat's age are what the test sets, never real time.
/// Every wait is on the exit's own completion.
/// </remarks>
/// <docs>operations/startup/instance-exits</docs>
/// <code-under-test>src/Whizbang.Core/RunControl/WhizbangInstanceExit.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/RunControl/InstanceExitLogEntry.cs</code-under-test>
[Category("Component")]
[Category("RunControl")]
public class WhizbangInstanceExitTests {
  private static readonly Guid _instanceId = Guid.Parse("0199e000-0000-7000-8000-000000000001");
  private static readonly Guid _peerId = Guid.Parse("0199e000-0000-7000-8000-000000000002");
  private static readonly DateTimeOffset _start = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

  [Test]
  [Arguments(ExitCause.HostShutdown, 0, LogLevel.Information, "check what asked the host to stop")]
  [Arguments(ExitCause.OperatorStop, 0, LogLevel.Information, "check who used the killswitch")]
  [Arguments(ExitCause.UpgradeStandDown, 0, LogLevel.Information, "check the rollout that migrated the schema")]
  [Arguments(ExitCause.Crash, 1, LogLevel.Error, "check the exception attached to this entry")]
  [Arguments(ExitCause.Unknown, 2, LogLevel.Error, "check the log entries just before this one")]
  [Arguments(ExitCause.ClusterEviction, 3, LogLevel.Error, "check for heartbeat starvation")]
  [Arguments(ExitCause.StartupFailure, 4, LogLevel.Error, "check database connectivity from this host")]
  [Arguments(ExitCause.SchemaIncompatible, 5, LogLevel.Error, "the schema was migrated by a newer release")]
  public async Task EachCause_SetsItsExitCode_WritesOneEntryAtItsLevel_HaltsAndStopsTheHostOnceAsync(
      ExitCause cause, int exitCode, LogLevel level, string whatToCheck) {
    var h = new Harness();

    await h.Exit.ExitAsync(new InstanceExitRequest(cause, "a reason"), CancellationToken.None);

    await Assert.That(h.ExitCodes.Codes).IsEquivalentTo([exitCode]);
    await Assert.That(h.Lifetime.StopCalls).IsEqualTo(1);
    await Assert.That(h.RunControl.Halts).IsEquivalentTo([RunHaltReason.Stop]);
    var entry = h.Entries.Single();
    await Assert.That(entry.Level).IsEqualTo(level);
    await Assert.That(entry.Id).IsEqualTo(WhizbangInstanceExit.ExitEventId);
    await Assert.That(entry.Id.Id).IsEqualTo(1);
    await Assert.That(_field(entry, "ExitCause")).IsEqualTo(cause.ToString());
    await Assert.That(_field(entry, "ExitCode")).IsEqualTo(exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture));
    await Assert.That(_field(entry, "Intended")).IsEqualTo(exitCode == 0 ? "True" : "False");
    await Assert.That(entry.Message).Contains("If this was not expected, " + whatToCheck);
    await Assert.That(h.Exit.Cause).IsEqualTo(cause);
    await Assert.That(h.Exit.Report!.Record.ExitCode).IsEqualTo(exitCode);
  }

  [Test]
  public async Task TheEntry_CarriesEveryRequiredFact_ForAnEvictionAsync() {
    var lastHeartbeat = _start.AddSeconds(40);
    var evictedAt = _start.AddSeconds(80);
    var h = new Harness {
      Recording = InstanceExitRecordResult.Written(
        new InstanceEviction(evictedAt, _peerId, "not live (wh_instance_liveness)", lastHeartbeat, 150)),
    };
    h.Exit.OnHostStarting();
    h.Time.Advance(TimeSpan.FromSeconds(51));
    h.Heartbeat.LastAcceptedAt = h.Time.GetUtcNow();
    h.Time.Advance(TimeSpan.FromSeconds(12));
    using var first = await h.RunControl.Permit.EnterAsync(CancellationToken.None);
    using var second = await h.RunControl.Permit.EnterAsync(CancellationToken.None);

    h.Exit.RequestExit(new InstanceExitRequest(ExitCause.ClusterEviction, "its heartbeat was refused"));
    await h.Exit.OnHostStoppingAsync(CancellationToken.None);

    var message = h.Entries.Single().Message;
    foreach (var fact in new[] {
      $"Instance {_instanceId} of service orders-svc on host orders-pod-7 (process 4242) is exiting: ClusterEviction, a failure.",
      "Its heartbeat was refused.",
      $"It was evicted at {evictedAt:O} by peer instance {_peerId}, recorded reason \"not live (wh_instance_liveness)\"",
      $"judged dead by the one liveness rule because its last heartbeat, at {lastHeartbeat:O}, was older than its liveness window of 150 seconds.",
      "Exit code 3: the host (Kubernetes or another supervisor) restarts the process, and the new process registers under a new instance id.",
      "At the exit it was in startup stage Migrate, lifecycle phase Migrating, up 63.0 seconds; its last accepted heartbeat was 12.0 seconds before.",
      "It held the roles maintainer, migrator, 4 leased stream(s) and 2 unit(s) of work in hand; the run-control path released what it could",
      "The exit is recorded in wh_instance_exits.",
      "heartbeat starvation", "database connectivity from this host", "long process pauses",
      "See " + WhizbangInstanceExit.DOCS_LINK,
    }) {
      await Assert.That(message).Contains(fact);
    }
    var entry = h.Entries.Single();
    await Assert.That(_field(entry, "InstanceId")).IsEqualTo(_instanceId.ToString());
    await Assert.That(_field(entry, "ServiceName")).IsEqualTo("orders-svc");
    await Assert.That(_field(entry, "HostName")).IsEqualTo("orders-pod-7");
    await Assert.That(_field(entry, "ProcessId")).IsEqualTo("4242");
    await Assert.That(_field(entry, "StartupStage")).IsEqualTo("Migrate");
    await Assert.That(_field(entry, "LifecyclePhase")).IsEqualTo("Migrating");
    await Assert.That(_field(entry, "EvictedBy")).IsEqualTo(_peerId.ToString());
    await Assert.That(_field(entry, "LivenessWindowSeconds")).IsEqualTo("150");
    await Assert.That(_field(entry, "ExitRecorded")).IsEqualTo("True");
    await Assert.That(_field(entry, "DocsLink")).IsEqualTo(WhizbangInstanceExit.DOCS_LINK);
    foreach (var key in new[] { "Reason", "UptimeSeconds", "SecondsSinceLastHeartbeat", "HeldRoles", "HeldLeases", "InHand",
        "ExitRecordNote", "EvictedAt", "EvictionReason", "EvictedLastHeartbeatAt" }) {
      await Assert.That(entry.StructuredState!.Any(kv => kv.Key == key)).IsTrue().Because($"{key} is a structured field");
    }
  }

  [Test]
  public async Task TheEntry_StatesWhatTheInstanceHeld_ReadBeforeTheHaltReleasedItAsync() {
    var h = new Harness(realRunControl: true);

    await h.Exit.ExitAsync(new InstanceExitRequest(ExitCause.HostShutdown, "the host was asked to stop"), CancellationToken.None);

    await Assert.That(h.Exit.Report!.Record.HeldRoles).IsEquivalentTo(["maintainer", "migrator"],
      TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That(h.Exit.Report.Record.HeldLeases).IsEqualTo(4);
    await Assert.That(h.Roles.Released).IsTrue().Because("the halt released the roles after the exit read them");
    await Assert.That(h.Work.Released).IsTrue();
  }

  [Test]
  public async Task TheDurableRow_IsWrittenThroughTheCoordinator_WithTheFactsTheEntryStatesAsync() {
    var h = new Harness();

    await h.Exit.ExitAsync(new InstanceExitRequest(ExitCause.StartupFailure, "the version record could not be read",
      new InvalidOperationException("boom")), CancellationToken.None);

    var written = h.Coordinator.Written.Single();
    await Assert.That(written).IsEqualTo(h.Exit.Report!.Record);
    await Assert.That(written.InstanceId).IsEqualTo(_instanceId);
    await Assert.That(written.Cause).IsEqualTo(ExitCause.StartupFailure);
    await Assert.That(written.ExitCode).IsEqualTo(4);
    await Assert.That(written.Intended).IsFalse();
    await Assert.That(written.ExceptionType).IsEqualTo("System.InvalidOperationException");
    await Assert.That(written.ExceptionMessage).IsEqualTo("boom");
    await Assert.That(written.LibraryVersion).IsEqualTo("9.9.9");
    await Assert.That(h.Entries.Single().Exception).IsTypeOf<InvalidOperationException>();
  }

  [Test]
  [Arguments("throws", "The exit could not be recorded in wh_instance_exits: the database could not be reached or refused the write (System.TimeoutException: the database is down).")]
  [Arguments("not-kept", "The exit could not be recorded in wh_instance_exits: this host's work coordinator keeps no instance records.")]
  [Arguments("no-reason", "The exit could not be recorded in wh_instance_exits: unknown.")]
  [Arguments("no-coordinator", "The exit could not be recorded in wh_instance_exits: this host has no work coordinator, so it keeps no instance records.")]
  [Arguments("no-identity", "The exit could not be recorded in wh_instance_exits: this host has no instance identity to record the exit against.")]
  public async Task WhenTheRowIsNotWritten_TheEntrySaysWhy_AndTheExitStillCompletesAsync(string how, string said) {
    var h = new Harness(withCoordinator: how != "no-coordinator", withIdentity: how != "no-identity");
    h.Coordinator.Failure = how == "throws" ? new TimeoutException("the database is down") : null;
    h.Recording = how switch {
      "not-kept" => InstanceExitRecordResult.NotKept,
      "no-reason" => new InstanceExitRecordResult(false, null, null),
      _ => h.Recording,
    };

    await h.Exit.ExitAsync(new InstanceExitRequest(ExitCause.Crash, "a worker failed."), CancellationToken.None);

    var entry = h.Entries.Single();
    await Assert.That(entry.Message).Contains(said);
    await Assert.That(entry.Message).Contains("A worker failed.").Because("a reason that ends a sentence is not given a second period");
    await Assert.That(_field(entry, "ExitRecorded")).IsEqualTo("False");
    await Assert.That(h.ExitCodes.Codes).IsEquivalentTo([1]);
    await Assert.That(h.Lifetime.StopCalls).IsEqualTo(1);
  }

  [Test]
  public async Task AHostWithNothingToReport_StillExits_SayingWhatItCouldNotTellAsync() {
    var h = new Harness(bare: true);

    await h.Exit.ExitAsync(new InstanceExitRequest(ExitCause.ClusterEviction, "its heartbeat was refused"), CancellationToken.None);

    var message = h.Entries.Single().Message;
    await Assert.That(message).Contains($"Instance {Guid.Empty} of service unknown on host {Environment.MachineName} (process {Environment.ProcessId})");
    await Assert.That(message).Contains("The eviction's details could not be read with the exit record.");
    await Assert.That(message).Contains("startup stage unknown, lifecycle phase unknown, up for an unknown time (no host start was seen); it had no accepted heartbeat.");
    await Assert.That(message).Contains("It held no roles, 0 leased stream(s) and 0 unit(s) of work in hand");
    await Assert.That(h.ExitCodes.Codes).IsEquivalentTo([3]);
  }

  [Test]
  public async Task AnEvictionTheTombstoneDoesNotFullyDescribe_SaysSo_AndAnyCauseStatesATombstoneItHasAsync() {
    var evictedAt = _start.AddSeconds(5);
    var h = new Harness {
      Recording = InstanceExitRecordResult.Written(new InstanceEviction(evictedAt, null, null, null, null)),
    };

    await h.Exit.ExitAsync(new InstanceExitRequest(ExitCause.Crash, "a worker failed"), CancellationToken.None);

    await Assert.That(h.Entries.Single().Message).Contains(
      $"It was evicted at {evictedAt:O} by a sweep that recorded no peer, recorded reason \"unknown\": judged dead by the one "
      + "liveness rule because its last heartbeat (not recorded on this tombstone) was older than the rule's default window "
      + "of 30 seconds (its row recorded none).");
  }

  [Test]
  public async Task AHostedExit_StopsTheHostFirst_AndTheHostsStopCompletesItOnTheStopTokenAsync() {
    var h = new Harness();
    h.Exit.OnHostStarting();
    using var stop = new CancellationTokenSource();

    h.Exit.RequestExit(new InstanceExitRequest(ExitCause.ClusterEviction, "its heartbeat was refused"));
    var completedBeforeTheHostStopped = h.Exit.Completion.IsCompleted;
    var haltsBeforeTheHostStopped = h.RunControl.Halts.Count;
    await h.Exit.OnHostStoppingAsync(stop.Token);

    await Assert.That(completedBeforeTheHostStopped).IsFalse();
    await Assert.That(haltsBeforeTheHostStopped).IsEqualTo(0).Because("the host's own stop completes the exit");
    await Assert.That(h.Lifetime.StopCalls).IsEqualTo(1).Because("the host is stopped once, by the request");
    await Assert.That(h.RunControl.Tokens.Single()).IsEqualTo(stop.Token).Because("the host's stop token bounds the halt");
    await Assert.That(h.Exit.Completion.IsCompletedSuccessfully).IsTrue();
    await Assert.That(h.ExitCodes.Codes).IsEquivalentTo([3]);
  }

  [Test]
  public async Task AHostedExit_WithoutAHostLifetime_CompletesItselfAsync() {
    var h = new Harness(withLifetime: false);
    h.Exit.OnHostStarting();

    await h.Exit.ExitAsync(new InstanceExitRequest(ExitCause.OperatorStop, "an operator stopped it"), CancellationToken.None);

    await Assert.That(h.ExitCodes.Codes).IsEquivalentTo([0]);
  }

  [Test]
  public async Task TheFirstCauseWins_ALaterRequestOrAHostStopChangesNothingAsync() {
    var h = new Harness();

    await h.Exit.ExitAsync(new InstanceExitRequest(ExitCause.ClusterEviction, "its heartbeat was refused"), CancellationToken.None);
    h.Exit.RequestExit(new InstanceExitRequest(ExitCause.Crash, "a later failure"));
    await h.Exit.OnHostStoppingAsync(CancellationToken.None);

    await Assert.That(h.Exit.Cause).IsEqualTo(ExitCause.ClusterEviction);
    await Assert.That(h.Entries.Count).IsEqualTo(1);
    await Assert.That(h.ExitCodes.Codes).IsEquivalentTo([3]);
    await Assert.That(h.RunControl.Halts.Count).IsEqualTo(1);
  }

  [Test]
  public async Task AHostStopNoCauseAskedFor_IsAHostShutdownAsync() {
    var h = new Harness();
    h.Exit.OnHostStarting();

    await h.Exit.OnHostStoppingAsync(CancellationToken.None);

    await Assert.That(h.Exit.Cause).IsEqualTo(ExitCause.HostShutdown);
    await Assert.That(h.ExitCodes.Codes).IsEquivalentTo([0]);
    await Assert.That(h.Entries.Single().Level).IsEqualTo(LogLevel.Information);
  }

  [Test]
  public async Task AHostStop_AfterAHostedWorkerFaulted_IsACrash_NamingTheWorkerAndItsFailureAsync() {
    var faulted = new FaultingWorker();
    var h = new Harness(hosted: [new HealthyWorker(), faulted]);
    await faulted.StartAsync(CancellationToken.None);
    await Assert.That(async () => await faulted.ExecuteTask!).Throws<InvalidOperationException>();

    await h.Exit.OnHostStoppingAsync(CancellationToken.None);

    await Assert.That(h.Exit.Cause).IsEqualTo(ExitCause.Crash);
    await Assert.That(h.ExitCodes.Codes).IsEquivalentTo([1]);
    var entry = h.Entries.Single();
    await Assert.That(entry.Message).Contains("the hosted service Whizbang.Core.Component.Tests.RunControl.WhizbangInstanceExitTests+FaultingWorker failed");
    await Assert.That(entry.Exception!.Message).IsEqualTo("the worker failed");
    await Assert.That(h.Exit.Report!.Record.ExceptionType).IsEqualTo("System.InvalidOperationException");
  }

  [Test]
  public async Task AHaltThatThrows_IsSaidInTheEntry_AndTheExitGoesOnAsync() {
    var h = new Harness();
    h.RunControl.Failure = new InvalidOperationException("the lifecycle could not be written");

    await h.Exit.ExitAsync(new InstanceExitRequest(ExitCause.Crash, "a worker failed"), CancellationToken.None);

    await Assert.That(h.Entries.Single().Message).Contains(
      "the run-control path could not complete (System.InvalidOperationException: the lifecycle could not be written)");
    await Assert.That(h.ExitCodes.Codes).IsEquivalentTo([1]);
  }

  [Test]
  public async Task TheHostsStopBudget_RunningOut_WhileAnotherCallerCompletesTheExit_EndsOnlyTheWaitAsync() {
    var h = new Harness();
    h.RunControl.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    h.Exit.RequestExit(new InstanceExitRequest(ExitCause.Crash, "a worker failed"));
    await h.RunControl.Entered.Task;
    using var spent = new CancellationTokenSource();
    await spent.CancelAsync();

    await h.Exit.OnHostStoppingAsync(spent.Token);
    var completedWhenTheBudgetRanOut = h.Exit.Completion.IsCompleted;
    h.RunControl.Hold.SetResult();
    await h.Exit.Completion;

    await Assert.That(completedWhenTheBudgetRanOut).IsFalse();
    await Assert.That(h.ExitCodes.Codes).IsEquivalentTo([1]).Because("the exit the first caller began still completes");
  }

  [Test]
  public async Task ExitAsync_ItsTokenEndsOnlyTheWaitAsync() {
    var h = new Harness();
    h.RunControl.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    using var impatient = new CancellationTokenSource();
    var waiting = h.Exit.ExitAsync(new InstanceExitRequest(ExitCause.OperatorStop, "stopped"), impatient.Token);
    await h.RunControl.Entered.Task;

    await impatient.CancelAsync();
    await Assert.That(async () => await waiting).Throws<TaskCanceledException>();
    h.RunControl.Hold.SetResult();
    await h.Exit.Completion;

    await Assert.That(h.ExitCodes.Codes).IsEquivalentTo([0]);
  }

  [Test]
  public async Task TheRunControlRegistration_ProvidesOneExitPath_AndTheProcessExitCodeAsync() {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddWhizbangRunControl();
    await using var provider = services.BuildServiceProvider();

    await Assert.That(provider.GetRequiredService<IWhizbangInstanceExit>()).IsTypeOf<WhizbangInstanceExit>();
    await Assert.That(provider.GetRequiredService<IWhizbangInstanceExit>())
      .IsSameReferenceAs(provider.GetRequiredService<IWhizbangInstanceExit>());
    await Assert.That(provider.GetRequiredService<IProcessExitCode>()).IsSameReferenceAs(ProcessExitCode.Instance);
  }

  [Test]
  public async Task TheKillswitchStop_ExitsAsAnOperatorStop_WithExitCodeZeroAsync() {
    var codes = new RecordingExitCode();
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<IProcessExitCode>(codes);
    services.AddWhizbangRunControl();
    await using var provider = services.BuildServiceProvider();

    await provider.GetRequiredService<IWhizbangKillswitch>().StopAsync();

    await Assert.That(provider.GetRequiredService<IWhizbangInstanceExit>().Cause).IsEqualTo(ExitCause.OperatorStop);
    await Assert.That(codes.Codes).IsEquivalentTo([0]);
    await Assert.That(provider.GetRequiredService<IWhizbangRunController>().HaltedFor).IsEqualTo(RunHaltReason.Stop);
  }

  [Test]
  public async Task Arguments_AreValidatedAsync() {
    var services = new ServiceCollection().BuildServiceProvider();
    var run = new RecordingRunControl();
    var codes = new RecordingExitCode();
    var logger = NullLogger<WhizbangInstanceExit>.Instance;

    await Assert.That(() => new WhizbangInstanceExit(null!, run, codes, null, logger)).Throws<ArgumentNullException>();
    await Assert.That(() => new WhizbangInstanceExit(services, null!, codes, null, logger)).Throws<ArgumentNullException>();
    await Assert.That(() => new WhizbangInstanceExit(services, run, null!, null, logger)).Throws<ArgumentNullException>();
    await Assert.That(() => new WhizbangInstanceExit(services, run, codes, null, null!)).Throws<ArgumentNullException>();
    await Assert.That(() => new WhizbangInstanceExit(services, run, codes, null, logger).RequestExit(null!))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task TheEntry_IsAListOfItsFields_EndingWithItsFormatAsync() {
    var h = new Harness();
    await h.Exit.ExitAsync(new InstanceExitRequest(ExitCause.HostShutdown, "stop"), CancellationToken.None);

    var state = (IReadOnlyList<KeyValuePair<string, object?>>)h.RawStates.Single();
    await Assert.That(state[0].Key).IsEqualTo("ExitCause");
    await Assert.That(state[state.Count - 1].Key).IsEqualTo("{OriginalFormat}");
    var keys = new List<string>();
    foreach (var pair in (System.Collections.IEnumerable)state) {
      keys.Add(((KeyValuePair<string, object?>)pair).Key);
    }
    await Assert.That(keys.Count).IsEqualTo(state.Count);
  }

  private static string? _field(FakeLogRecord entry, string key) =>
    entry.StructuredState!.Single(kv => kv.Key == key).Value;

  // ------------------------------------------------------------------

  private sealed class Harness {
    public Harness(
        bool withCoordinator = true, bool withIdentity = true, bool withLifetime = true, bool bare = false,
        bool realRunControl = false, IHostedService[]? hosted = null) {
      var services = new ServiceCollection();
      services.AddSingleton<ILoggerProvider>(new CapturingProvider(Collector, RawStates));
      services.AddLogging();
      if (withLifetime) {
        services.AddSingleton<IHostApplicationLifetime>(Lifetime);
      }
      if (!bare) {
        if (withIdentity) {
          services.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(_instanceId, "orders-svc", "orders-pod-7", 4242));
        }
        if (withCoordinator) {
          services.AddScoped<IWorkCoordinator>(_ => Coordinator);
        }
        services.AddSingleton<IStartupStageReporter>(new StageAt(StartupStage.Migrate));
        services.AddSingleton<IWhizbangLifecycleState>(new PhaseAt(LifecyclePhase.Migrating));
        services.AddSingleton<ILibraryVersionProvider>(new LibraryVersionProvider("9.9.9"));
        services.AddSingleton<ILastAcceptedHeartbeat>(Heartbeat);
        services.AddSingleton<IReleasesHeldRoles>(Roles);
        services.AddSingleton<IReleasesHeldRoles>(new SilentRoles());
        services.AddSingleton<IReleasesHeldWork>(Work);
        services.AddSingleton<IReleasesHeldWork>(new SilentWork());
      }
      foreach (var service in hosted ?? []) {
        services.AddSingleton(service);
      }
      var provider = services.BuildServiceProvider();
      Coordinator.Results = () => Recording;
      if (realRunControl) {
        var options = new WhizbangLifecycleOptions();
        RealRunControl = new WhizbangRunController(new WhizbangRunPermit(), new PhaseAt(LifecyclePhase.Running),
          provider.GetServices<IReleasesHeldWork>(), provider.GetServices<IReleasesHeldRoles>(), options, Time,
          NullLogger<WhizbangRunController>.Instance);
      }
      Exit = new WhizbangInstanceExit(provider, (IWhizbangRunController?)RealRunControl ?? RunControl, ExitCodes, Time,
        provider.GetRequiredService<ILogger<WhizbangInstanceExit>>());
    }

    public FakeTimeProvider Time { get; } = new(_start);
    public FakeLogCollector Collector { get; } = new();
    public List<object> RawStates { get; } = [];
    public IReadOnlyList<FakeLogRecord> Entries => Collector.GetSnapshot();
    public RecordingLifetime Lifetime { get; } = new();
    public RecordingExitCode ExitCodes { get; } = new();
    public RecordingRunControl RunControl { get; } = new();
    public WhizbangRunController? RealRunControl { get; }
    public RecordingCoordinator Coordinator { get; } = new();
    public InstanceExitRecordResult Recording { get; set; } = InstanceExitRecordResult.Written(null);
    public SettableHeartbeat Heartbeat { get; } = new();
    public HoldingRoles Roles { get; } = new();
    public HoldingWork Work { get; } = new();
    public WhizbangInstanceExit Exit { get; }
  }

  /// <summary>Feeds a <see cref="FakeLogCollector"/>, and keeps each raw state so the entry's own list can be read.</summary>
  private sealed class CapturingProvider(FakeLogCollector collector, List<object> states) : ILoggerProvider {
    public ILogger CreateLogger(string categoryName) => new Capturing(new FakeLogger(collector, categoryName), states);
    public void Dispose() { }

    private sealed class Capturing(FakeLogger inner, List<object> states) : ILogger {
      public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);
      public bool IsEnabled(LogLevel logLevel) => true;
      public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
        if (state is not null) {
          states.Add(state);
        }
        inner.Log(logLevel, eventId, state, exception, formatter);
      }
    }
  }

  internal sealed class RecordingLifetime : IHostApplicationLifetime, IDisposable {
    private readonly CancellationTokenSource _stopping = new();
    public void Dispose() => _stopping.Dispose();
    public int StopCalls { get; private set; }
    public CancellationToken ApplicationStarted => CancellationToken.None;
    public CancellationToken ApplicationStopping => _stopping.Token;
    public CancellationToken ApplicationStopped => CancellationToken.None;
    public void StopApplication() {
      StopCalls++;
      _stopping.Cancel();
    }
  }

  internal sealed class RecordingExitCode : IProcessExitCode {
    public List<int> Codes { get; } = [];
    public void SetExitCode(int exitCode) => Codes.Add(exitCode);
  }

  internal sealed class RecordingRunControl : IWhizbangRunController {
    public List<RunHaltReason> Halts { get; } = [];
    public List<CancellationToken> Tokens { get; } = [];
    public Exception? Failure { get; set; }
    public TaskCompletionSource? Hold { get; set; }
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public WhizbangRunPermit Permit => _permit.Value;
    private readonly Lazy<WhizbangRunPermit> _permit = new(() => new WhizbangRunPermit());
    public RunHaltReason? HaltedFor => Halts.Count == 0 ? null : Halts[^1];
    public IReadOnlyCollection<RunHaltReason> HaltedReasons => Halts;

    public async Task HaltAsync(RunHaltReason reason, CancellationToken cancellationToken) {
      Halts.Add(reason);
      Tokens.Add(cancellationToken);
      Entered.TrySetResult();
      if (Hold is { } hold) {
        await hold.Task;
      }
      if (Failure is { } failure) {
        throw failure;
      }
    }

    public Task ResumeAsync(RunHaltReason reason, CancellationToken cancellationToken) => Task.CompletedTask;
  }

  internal sealed class RecordingCoordinator : IWorkCoordinator {
    public List<InstanceExitRecord> Written { get; } = [];
    public Exception? Failure { get; set; }
    public Func<InstanceExitRecordResult> Results { get; set; } = () => InstanceExitRecordResult.Written(null);

    public Task<InstanceExitRecordResult> RecordInstanceExitAsync(InstanceExitRecord exitRecord, CancellationToken cancellationToken = default) {
      if (Failure is { } failure) {
        return Task.FromException<InstanceExitRecordResult>(failure);
      }
      Written.Add(exitRecord);
      return Task.FromResult(Results());
    }

    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default)
      => Task.FromResult(new WorkCoordinatorStatistics());
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default)
      => Task.FromResult<PerspectiveCursorInfo?>(null);
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default)
      => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default)
      => Task.CompletedTask;
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default)
      => Task.CompletedTask;
  }

  internal sealed class StageAt(StartupStage stage) : IStartupStageReporter {
    public StartupStageSnapshot? Current { get; } = new(stage, "applying migrations", false, _start);
    public ValueTask EnterAsync(StartupStage stage, string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public ValueTask SkipAsync(StartupStage stage, string reason, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public ValueTask InstanceRegisteredAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
  }

  internal sealed class PhaseAt(LifecyclePhase phase) : IWhizbangLifecycleState {
    public LifecyclePhase Phase { get; private set; } = phase;
    public ValueTask AdvanceToAsync(LifecyclePhase phase, CancellationToken cancellationToken) {
      Phase = phase;
      return ValueTask.CompletedTask;
    }
    public ValueTask FaultAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
  }

  internal sealed class SettableHeartbeat : ILastAcceptedHeartbeat {
    public DateTimeOffset? LastAcceptedAt { get; set; }
  }

  /// <summary>Holds two roles until they are released; names them while it holds them.</summary>
  internal sealed class HoldingRoles : IReleasesHeldRoles {
    public bool Released { get; private set; }
    public IReadOnlyCollection<string> HeldRoles => Released ? [] : ["migrator", "maintainer"];
    public Task ReleaseAllAsync(CancellationToken cancellationToken) {
      Released = true;
      return Task.CompletedTask;
    }
  }

  /// <summary>Holds four leased streams until they are released.</summary>
  internal sealed class HoldingWork : IReleasesHeldWork {
    public bool Released { get; private set; }
    public int HeldLeases => Released ? 0 : 4;
    public Task ReleaseHeldWorkAsync(CancellationToken cancellationToken) {
      Released = true;
      return Task.CompletedTask;
    }
  }

  /// <summary>A releaser that does not name what it holds: the defaults say nothing.</summary>
  internal sealed class SilentRoles : IReleasesHeldRoles {
    public Task ReleaseAllAsync(CancellationToken cancellationToken) => Task.CompletedTask;
  }

  /// <summary>A releaser that does not count what it holds.</summary>
  internal sealed class SilentWork : IReleasesHeldWork {
    public Task ReleaseHeldWorkAsync(CancellationToken cancellationToken) => Task.CompletedTask;
  }

  internal sealed class HealthyWorker : BackgroundService {
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
  }

  internal sealed class FaultingWorker : BackgroundService {
    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
      await Task.Yield();
      throw new InvalidOperationException("the worker failed");
    }
  }
}
