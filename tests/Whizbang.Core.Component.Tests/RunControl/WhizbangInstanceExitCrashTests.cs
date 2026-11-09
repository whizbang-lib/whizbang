// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.RunControl;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Component.Tests.RunControl;

/// <summary>
/// A crash on any thread goes through the one exit path (#2126): an exception nothing caught is a
/// <see cref="ExitCause.Crash"/> (code 1) that writes the detailed entry, records the durable row, halts, and deregisters
/// the instance, bounded by <see cref="WhizbangInstanceExit.CrashExitBudget"/>, without ever throwing, hanging the dying
/// process, or exiting twice. A faulted task nobody observed is said, and the instance goes on.
/// </summary>
/// <remarks>
/// A test raises the process's failure events through <see cref="IProcessCrashEvents"/>, never a real crash. The clock
/// is a <see cref="FakeTimeProvider"/>: the budget passes when the test advances it, after the exit is known to be stuck.
/// </remarks>
/// <docs>operations/startup/instance-exits#a-crash-on-any-thread</docs>
/// <code-under-test>src/Whizbang.Core/RunControl/WhizbangInstanceExit.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/RunControl/ProcessCrashEvents.cs</code-under-test>
[Category("Component")]
[Category("RunControl")]
public class WhizbangInstanceExitCrashTests {
  private static readonly Guid _instanceId = Guid.Parse("0199e000-0000-7000-8000-000000000011");

  [Test]
  [Timeout(30000)]
  public async Task TheHostStart_SubscribesTheExitPathOnce_AndTheExitGivesTheEventsBackAsync(CancellationToken cancellationToken) {
    var h = new Harness();

    h.Exit.OnHostStarting();
    h.Exit.OnHostStarting();

    await Assert.That(h.Events.UnhandledSubscribers).IsEqualTo(1);
    await Assert.That(h.Events.UnobservedSubscribers).IsEqualTo(1);

    // In a host a requested exit stops the host, and the host's stop completes it.
    h.Exit.RequestExit(new InstanceExitRequest(ExitCause.HostShutdown, "the host was asked to stop"));
    await h.Exit.OnHostStoppingAsync(cancellationToken);

    await Assert.That(h.Events.UnhandledSubscribers).IsEqualTo(0)
      .Because("once the exit has completed there is nothing left for a crash to exit");
    await Assert.That(h.Events.UnobservedSubscribers).IsEqualTo(0);
    h.Exit.OnHostStarting();
    await Assert.That(h.Events.UnhandledSubscribers).IsEqualTo(0).Because("an exit that completed is not hooked again");
  }

  [Test]
  [Timeout(30000)]
  public async Task NoHostStart_NoHook_AndAnExitWithoutOneUnhooksNothingAsync(CancellationToken cancellationToken) {
    var h = new Harness();

    await h.Exit.ExitAsync(new InstanceExitRequest(ExitCause.HostShutdown, "a direct exit"), cancellationToken);

    await Assert.That(h.Events.UnhandledSubscribers).IsEqualTo(0);
  }

  [Test]
  [Timeout(30000)]
  public async Task ACrashOnAnyThread_IsACrashExit_WithItsEntryRowHaltAndDeregistrationAsync(CancellationToken cancellationToken) {
    var h = new Harness();
    h.Exit.OnHostStarting();
    var failure = new InvalidOperationException("a background thread failed");

    await Task.Run(() => h.Events.Crash(failure), cancellationToken);

    await Assert.That(h.Exit.Cause).IsEqualTo(ExitCause.Crash);
    await Assert.That(h.ExitCodes.Codes).IsEquivalentTo([1]);
    var entry = h.Collector.GetSnapshot().Single(e => e.Id == WhizbangInstanceExit.ExitEventId);
    await Assert.That(entry.Level).IsEqualTo(LogLevel.Error);
    await Assert.That(entry.Exception).IsSameReferenceAs(failure);
    await Assert.That(entry.Message).Contains("An exception nothing caught (System.InvalidOperationException) ended the process.");
    await Assert.That(h.Coordinator.Written.Single().Cause).IsEqualTo(ExitCause.Crash);
    await Assert.That(h.RunControl.Halts).IsEquivalentTo([RunHaltReason.Stop]);
    await Assert.That(h.Coordinator.Deregistered).IsEquivalentTo([_instanceId])
      .Because("a dying process never reaches the shutdown service, so the crash exit deregisters the instance itself");
    await Assert.That(h.Lifetime.StopCalls).IsEqualTo(1);
    await Assert.That(h.Events.UnhandledSubscribers).IsEqualTo(0);
  }

  [Test]
  [Timeout(30000)]
  public async Task SomethingThrownThatIsNotAnException_IsStillACrashExitAsync(CancellationToken cancellationToken) {
    var h = new Harness();
    h.Exit.OnHostStarting();

    await Task.Run(() => h.Events.Crash("a string thrown from another language"), cancellationToken);

    await Assert.That(h.Exit.Cause).IsEqualTo(ExitCause.Crash);
    await Assert.That(h.Exit.Report!.Record.ExceptionMessage).Contains("nothing caught it: System.String");
  }

  [Test]
  [Timeout(30000)]
  public async Task ANullThrown_IsStillACrashExitAsync(CancellationToken cancellationToken) {
    var h = new Harness();
    h.Exit.OnHostStarting();

    await Task.Run(() => h.Events.Crash(null!), cancellationToken);

    await Assert.That(h.Exit.Cause).IsEqualTo(ExitCause.Crash);
    await Assert.That(h.Exit.Report!.Record.ExceptionMessage).EndsWith("nothing caught it: null");
  }

  [Test]
  [Timeout(30000)]
  public async Task ACrashDuringAnExitAlreadyUnderWay_KeepsThatExitsCause_AndDoesNotExitTwiceAsync(CancellationToken cancellationToken) {
    var h = new Harness();
    h.Exit.OnHostStarting();
    h.Exit.RequestExit(new InstanceExitRequest(ExitCause.OperatorStop, "the killswitch was used"));

    await Task.Run(() => h.Events.Crash(new InvalidOperationException("late")), cancellationToken);
    await Task.Run(() => h.Events.Crash(new InvalidOperationException("later")), cancellationToken);

    await Assert.That(h.Exit.Cause).IsEqualTo(ExitCause.OperatorStop).Because("the first cause wins, as in every exit");
    await Assert.That(h.Collector.GetSnapshot().Count(e => e.Id == WhizbangInstanceExit.ExitEventId)).IsEqualTo(1);
    await Assert.That(h.Coordinator.Written).Count().IsEqualTo(1);
    await Assert.That(h.Coordinator.Deregistered).Count().IsEqualTo(1);
    await Assert.That(h.ExitCodes.Codes).IsEquivalentTo([0]);
  }

  [Test]
  [Timeout(30000)]
  public async Task AnExitThatNeverFinishes_DoesNotHoldTheDyingProcess_PastTheBudgetAsync(CancellationToken cancellationToken) {
    var h = new Harness();
    h.RunControl.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    h.Exit.OnHostStarting();

    var crash = Task.Run(() => h.Events.Crash(new InvalidOperationException("boom")), cancellationToken);
    await h.RunControl.Entered.Task.WaitAsync(cancellationToken);

    await Assert.That(crash.IsCompleted).IsFalse().Because("within the budget the exit is given its chance");
    h.Time.Advance(WhizbangInstanceExit.CrashExitBudget);
    await crash;

    await Assert.That(h.Exit.Cause).IsEqualTo(ExitCause.Crash);
    await Assert.That(h.RunControl.Tokens.Single().IsCancellationRequested).IsTrue()
      .Because("the halt is told the budget is spent");
  }

  [Test]
  [Timeout(30000)]
  public async Task NothingTheCrashExitDoes_Throws_IntoTheRuntimesHandlerAsync(CancellationToken cancellationToken) {
    var h = new Harness(throwingExitCode: true);
    h.Coordinator.Failure = new InvalidOperationException("the database is gone");
    h.Exit.OnHostStarting();

    await Assert.That(async () => await Task.Run(() => h.Events.Crash(new InvalidOperationException("boom")), cancellationToken))
      .ThrowsNothing();
    await Assert.That(h.Exit.Cause).IsEqualTo(ExitCause.Crash);
    await Assert.That(h.Collector.GetSnapshot()).Contains(e => e.Level == LogLevel.Warning && e.Message.Contains("could not be deregistered"));
  }

  [Test]
  [Timeout(30000)]
  public async Task ASecondCrashAfterTheExit_DeregistersNothingTwiceAsync(CancellationToken cancellationToken) {
    var h = new Harness();

    await Task.Run(() => h.Exit.ExitOnCrash(new InvalidOperationException("first")), cancellationToken);
    await Task.Run(() => h.Exit.ExitOnCrash(new InvalidOperationException("second")), cancellationToken);

    await Assert.That(h.Coordinator.Deregistered).Count().IsEqualTo(1);
    await Assert.That(h.Coordinator.Written).Count().IsEqualTo(1);
  }

  [Test]
  [Timeout(30000)]
  public async Task ACrashWithNoIdentity_OrNoCoordinator_StillExits_AndDeregistersNothingAsync(CancellationToken cancellationToken) {
    var anonymous = new Harness(withIdentity: false);
    var uncoordinated = new Harness(withCoordinator: false);

    await Task.Run(() => anonymous.Exit.ExitOnCrash(new InvalidOperationException("boom")), cancellationToken);
    await Task.Run(() => uncoordinated.Exit.ExitOnCrash(new InvalidOperationException("boom")), cancellationToken);

    await Assert.That(anonymous.Exit.Cause).IsEqualTo(ExitCause.Crash);
    await Assert.That(anonymous.Coordinator.Deregistered).IsEmpty().Because("there is no instance to deregister");
    await Assert.That(uncoordinated.Exit.Cause).IsEqualTo(ExitCause.Crash);
    await Assert.That(uncoordinated.Exit.Report!.Recording.Recorded).IsFalse()
      .Because("a host with no work coordinator keeps no instance records, and has none to remove");
  }

  [Test]
  public async Task AClockThatFails_DoesNotLetTheCrashExitThrowAsync() {
    var h = new Harness(clock: new FailingClock());

    await Assert.That(() => h.Exit.ExitOnCrash(new InvalidOperationException("boom"))).ThrowsNothing();
  }

  [Test]
  public async Task AnUnobservedTaskException_IsSaid_AndDoesNotExitAsync() {
    var h = new Harness();
    h.Exit.OnHostStarting();
    var failure = new InvalidOperationException("a fire-and-forget task failed");

    h.Events.Unobserve(failure);

    var entry = h.Collector.GetSnapshot().Single();
    await Assert.That(entry.Level).IsEqualTo(LogLevel.Error);
    await Assert.That(entry.Id).IsEqualTo(WhizbangInstanceExit.UnobservedTaskEventId);
    await Assert.That(entry.Exception!.InnerException).IsSameReferenceAs(failure);
    await Assert.That(h.Exit.Cause).IsNull().Because("the runtime does not end the process for it, so neither does the exit path");
  }

  [Test]
  public async Task AnUnobservedTaskException_ThatCannotBeSaid_DoesNotEscapeAsync() {
    var h = new Harness(throwingLogger: true);
    h.Exit.OnHostStarting();

    await Assert.That(() => h.Events.Unobserve(new InvalidOperationException("boom"))).ThrowsNothing();
  }

  [Test]
  public async Task TheRegistration_HooksTheRegisteredCrashEvents_WhenTheHostStartsAsync() {
    var events = new FakeCrashEvents();
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<IProcessExitCode>(new WhizbangInstanceExitTests.RecordingExitCode());
    services.AddSingleton<IProcessCrashEvents>(events);
    services.TryAddWhizbangInstanceExit();
    await using var provider = services.BuildServiceProvider();

    provider.GetRequiredService<IWhizbangInstanceExit>().OnHostStarting();

    await Assert.That(events.UnhandledSubscribers).IsEqualTo(1)
      .Because("the exit path hooks the process's crash events itself, so a host that can exit cannot lack it");
  }

  [Test]
  public async Task TheProcessEvents_AreTheRuntimesOwnAsync() {
    static void unhandled(object? sender, UnhandledExceptionEventArgs e) {
      // nothing: the test only adds and removes the handler
    }
    static void unobserved(object? sender, UnobservedTaskExceptionEventArgs e) {
      // nothing: the test only adds and removes the handler
    }

    ProcessCrashEvents.Instance.UnhandledException += unhandled;
    ProcessCrashEvents.Instance.UnobservedTaskException += unobserved;
    ProcessCrashEvents.Instance.UnhandledException -= unhandled;
    ProcessCrashEvents.Instance.UnobservedTaskException -= unobserved;

    await Assert.That(ProcessCrashEvents.Instance).IsSameReferenceAs(ProcessCrashEvents.Instance);
  }

  // ─────────────────────────────────────────────────────────────────────────

  internal sealed class FakeCrashEvents : IProcessCrashEvents {
    private EventHandler<UnhandledExceptionEventArgs>? _unhandled;
    private EventHandler<UnobservedTaskExceptionEventArgs>? _unobserved;

    public int UnhandledSubscribers => _unhandled?.GetInvocationList().Length ?? 0;
    public int UnobservedSubscribers => _unobserved?.GetInvocationList().Length ?? 0;

    public event EventHandler<UnhandledExceptionEventArgs> UnhandledException {
      add => _unhandled += value;
      remove => _unhandled -= value;
    }

    public event EventHandler<UnobservedTaskExceptionEventArgs> UnobservedTaskException {
      add => _unobserved += value;
      remove => _unobserved -= value;
    }

    public void Crash(object thrown) => _unhandled?.Invoke(this, new UnhandledExceptionEventArgs(thrown, isTerminating: true));

    public void Unobserve(Exception exception) =>
      _unobserved?.Invoke(this, new UnobservedTaskExceptionEventArgs(new AggregateException(exception)));
  }

  private sealed class Harness {
    public Harness(
        bool throwingExitCode = false, bool throwingLogger = false, bool withIdentity = true, bool withCoordinator = true,
        TimeProvider? clock = null) {
      var services = new ServiceCollection();
      if (throwingLogger) {
        services.AddSingleton<ILoggerProvider>(new ThrowingProvider());
      } else {
        services.AddSingleton<ILoggerProvider>(new FakeLoggerProvider(Collector));
      }
      services.AddLogging();
      services.AddSingleton<IHostApplicationLifetime>(Lifetime);
      if (withIdentity) {
        services.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(_instanceId, "orders-svc", "orders-pod-7", 4242));
      }
      if (withCoordinator) {
        services.AddScoped<IWorkCoordinator>(_ => Coordinator);
      }
      var provider = services.BuildServiceProvider();
      Exit = new WhizbangInstanceExit(provider, RunControl,
        throwingExitCode ? new ThrowingExitCode() : ExitCodes, clock ?? Time,
        provider.GetRequiredService<ILogger<WhizbangInstanceExit>>(), Events);
    }

    public FakeTimeProvider Time { get; } = new();
    public FakeLogCollector Collector { get; } = new();
    public FakeCrashEvents Events { get; } = new();
    public WhizbangInstanceExitTests.RecordingLifetime Lifetime { get; } = new();
    public WhizbangInstanceExitTests.RecordingExitCode ExitCodes { get; } = new();
    public WhizbangInstanceExitTests.RecordingRunControl RunControl { get; } = new();
    public DeregisteringCoordinator Coordinator { get; } = new();
    public WhizbangInstanceExit Exit { get; }
  }

  /// <summary>A clock whose timers cannot be created: the crash exit's budget cannot even be set.</summary>
  private sealed class FailingClock : TimeProvider {
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
      throw new InvalidOperationException("no timers");
  }

  private sealed class ThrowingExitCode : IProcessExitCode {
    public void SetExitCode(int exitCode) => throw new InvalidOperationException("the exit code cannot be set");
  }

  private sealed class ThrowingProvider : ILoggerProvider {
    public ILogger CreateLogger(string categoryName) => new Throwing();
    public void Dispose() { }

    private sealed class Throwing : ILogger {
      public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
      public bool IsEnabled(LogLevel logLevel) => true;
      public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
          Func<TState, Exception?, string> formatter) => throw new InvalidOperationException("the log sink is gone");
    }
  }

  /// <summary>Records exit rows and deregistrations; fails both when told to.</summary>
  internal sealed class DeregisteringCoordinator : IWorkCoordinator {
    public List<InstanceExitRecord> Written { get; } = [];
    public List<Guid> Deregistered { get; } = [];
    public Exception? Failure { get; set; }

    public Task<InstanceExitRecordResult> RecordInstanceExitAsync(InstanceExitRecord exitRecord, CancellationToken cancellationToken = default) {
      if (Failure is { } failure) {
        return Task.FromException<InstanceExitRecordResult>(failure);
      }
      Written.Add(exitRecord);
      return Task.FromResult(InstanceExitRecordResult.Written(null));
    }

    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) {
      if (Failure is { } failure) {
        return Task.FromException(failure);
      }
      Deregistered.Add(instanceId);
      return Task.CompletedTask;
    }

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
}
