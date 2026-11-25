// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Temporal;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2586: a check that is off, or on with an empty schedule, ends every start with no active schedule under its key.
/// The scheduler used to only ever register, so a schedule stored while the check was on kept firing after the check
/// was turned off (a no-op occurrence) or after its cron was cleared (the check ran on the schedule and on the
/// every-Nth-audit counter both).
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/IntegritySweepScheduler.cs</code-under-test>
/// <docs>resilience/stream-integrity</docs>
[Category("Shard2")]
public class IntegritySweepScheduleRemovalTests {
  private const string COMPARE_KEY = "wh-integrity-sweep";
  private const string RECHECK_KEY = "wh-seal-recheck";

  private sealed class RecordingScheduleManager : IScheduleManager {
    public List<string> CanceledKeys { get; } = [];
    public List<ScheduleDefinition> Created { get; } = [];
    public HashSet<string> Stored { get; } = [];
    public HashSet<string> FailingKeys { get; } = [];

    public Task<ScheduleHandle> CreateAsync(ScheduleDefinition definition, CancellationToken cancellationToken = default) {
      Created.Add(definition);
      return Task.FromResult(new ScheduleHandle(TrackedGuid.New().Value, DateTimeOffset.UtcNow, WasCreated: true));
    }

    public Task<bool> CancelByKeyAsync(string key, CancellationToken cancellationToken = default) {
      if (FailingKeys.Contains(key)) {
        return Task.FromException<bool>(new InvalidOperationException("schedule store unavailable"));
      }
      CanceledKeys.Add(key);
      return Task.FromResult(Stored.Remove(key));
    }

    public Task<bool> PauseAsync(Guid scheduleId, long? expectedVersion = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<bool> ResumeAsync(Guid scheduleId, long? expectedVersion = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<bool> CancelAsync(Guid scheduleId, long? expectedVersion = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<Guid?> TriggerNowAsync(Guid scheduleId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<ScheduleUpdateResult?> UpdateAsync(Guid scheduleId, ScheduleUpdate update, long? expectedVersion = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
  }

  private sealed class RecordingLogger : ILogger<IntegritySweepScheduler> {
    public List<(LogLevel Level, int EventId, string Message, Exception? Exception)> Entries { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(
        LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) =>
      Entries.Add((logLevel, eventId.Id, formatter(state, exception), exception));
  }

  private sealed class InstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = TrackedGuid.New().Value;
    public string ServiceName => "removal-svc";
    public string HostName => "test-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() {
      ServiceName = ServiceName,
      InstanceId = InstanceId,
      HostName = HostName,
      ProcessId = ProcessId,
    };
  }

  private static (IntegritySweepScheduler Scheduler, RecordingScheduleManager Manager, RecordingLogger Logger, IntegritySweepScheduleState State)
      _build(StreamIntegrityOptions options, bool withManager = true, bool withInstanceProvider = true) {
    var services = new ServiceCollection();
    var manager = new RecordingScheduleManager();
    if (withManager) {
      services.AddSingleton<IScheduleManager>(manager);
    }
    if (withInstanceProvider) {
      services.AddSingleton<IServiceInstanceProvider>(new InstanceProvider());
    }
    var state = new IntegritySweepScheduleState();
    services.AddSingleton(state);
    var logger = new RecordingLogger();
    var scheduler = new IntegritySweepScheduler(services.BuildServiceProvider(), Options.Create(options), logger);
    return (scheduler, manager, logger, state);
  }

  /// <summary>The four ways a check wants no schedule: each check off, and each check on with an empty schedule.</summary>
  public static IEnumerable<Func<(StreamIntegrityOptions Options, string RemovedKey, string KeptKey)>> UnwantedSchedules() {
    yield return () => (new StreamIntegrityOptions {
      FullHistoryCompare = { Enabled = false, Schedule = "17 2 * * *" },
      SealRecheck = { Enabled = true, Schedule = "33 4 * * 0" },
    }, COMPARE_KEY, RECHECK_KEY);
    yield return () => (new StreamIntegrityOptions {
      FullHistoryCompare = { Enabled = true, Schedule = "" },
      SealRecheck = { Enabled = true, Schedule = "33 4 * * 0" },
    }, COMPARE_KEY, RECHECK_KEY);
    yield return () => (new StreamIntegrityOptions {
      FullHistoryCompare = { Enabled = true, Schedule = "17 2 * * *" },
      SealRecheck = { Enabled = false, Schedule = "33 4 * * 0" },
    }, RECHECK_KEY, COMPARE_KEY);
    yield return () => (new StreamIntegrityOptions {
      FullHistoryCompare = { Enabled = true, Schedule = "17 2 * * *" },
      SealRecheck = { Enabled = true, Schedule = "  " },
    }, RECHECK_KEY, COMPARE_KEY);
  }

  [Test]
  [MethodDataSource(nameof(UnwantedSchedules))]
  public async Task Start_ACheckThatWantsNoSchedule_CancelsItsStoredSchedule_AndLogsTheRemovalAsync(
      StreamIntegrityOptions options, string removedKey, string keptKey) {
    var (scheduler, manager, logger, _) = _build(options);
    manager.Stored.Add(removedKey);

    await scheduler.StartAsync(CancellationToken.None);

    await Assert.That(manager.CanceledKeys).IsEquivalentTo(new[] { removedKey })
      .Because("a schedule left behind keeps firing: a no-op for a check that is off, a second run for one on the counter");
    await Assert.That(manager.Stored).DoesNotContain(removedKey);
    await Assert.That(manager.Created.Select(d => d.Key!)).IsEquivalentTo(new[] { keptKey })
      .Because("the other check is on with a cron and registers as before");
    var removal = logger.Entries.Single(e => e.EventId == 101);
    await Assert.That(removal.Level).IsEqualTo(LogLevel.Information);
    await Assert.That(removal.Message).Contains(removedKey);
  }

  [Test]
  public async Task Start_WithNoStoredSchedule_CancelsQuietlyAsync() {
    var (scheduler, manager, logger, state) = _build(new StreamIntegrityOptions());

    await scheduler.StartAsync(CancellationToken.None);

    await Assert.That(manager.CanceledKeys).IsEquivalentTo(new[] { COMPARE_KEY, RECHECK_KEY })
      .Because("both checks are off by default, so both keys must end inactive");
    await Assert.That(manager.Created).IsEmpty();
    await Assert.That(logger.Entries).IsEmpty()
      .Because("removing a schedule that does not exist is a no-op: nothing was removed, so nothing is said");
    await Assert.That(state.CronActive).IsFalse();
    await Assert.That(state.SealRecheckCronActive).IsFalse();
  }

  [Test]
  public async Task Start_WithBothChecksOnACron_RegistersBoth_AndCancelsNothingAsync() {
    var (scheduler, manager, _, state) = _build(new StreamIntegrityOptions {
      FullHistoryCompare = { Enabled = true, Schedule = "17 2 * * *" },
      SealRecheck = { Enabled = true, Schedule = "33 4 * * 0" },
    });
    manager.Stored.Add(COMPARE_KEY);
    manager.Stored.Add(RECHECK_KEY);

    await scheduler.StartAsync(CancellationToken.None);

    await Assert.That(manager.CanceledKeys).IsEmpty();
    await Assert.That(manager.Created.Select(d => d.Key!)).IsEquivalentTo(new[] { COMPARE_KEY, RECHECK_KEY });
    await Assert.That(state.CronActive).IsTrue();
    await Assert.That(state.SealRecheckCronActive).IsTrue();
  }

  [Test]
  public async Task Start_WithNoTemporalEngine_RemovesNothing_AndDoesNotThrowAsync() {
    var (scheduler, manager, logger, _) = _build(new StreamIntegrityOptions {
      FullHistoryCompare = { Enabled = true, Schedule = "" },
    }, withManager: false);

    await Assert.That(async () => await scheduler.StartAsync(CancellationToken.None)).ThrowsNothing();

    await Assert.That(manager.CanceledKeys).IsEmpty();
    await Assert.That(logger.Entries.Where(e => e.Level >= LogLevel.Warning)).IsEmpty()
      .Because("with no engine there is no stored schedule to remove, which is not an error");
  }

  [Test]
  public async Task Start_WithAManagerButNoInstanceProvider_StillRemovesTheUnwantedScheduleAsync() {
    var (scheduler, manager, logger, state) = _build(new StreamIntegrityOptions {
      FullHistoryCompare = { Enabled = false },
      SealRecheck = { Enabled = true, Schedule = "33 4 * * 0" },
    }, withInstanceProvider: false);
    manager.Stored.Add(COMPARE_KEY);

    await scheduler.StartAsync(CancellationToken.None);

    await Assert.That(manager.CanceledKeys).IsEquivalentTo(new[] { COMPARE_KEY, });
    await Assert.That(manager.Created).IsEmpty()
      .Because("registering needs the instance to run as; removing does not");
    await Assert.That(state.SealRecheckCronActive).IsFalse();
    await Assert.That(logger.Entries.Select(e => e.EventId)).Contains(96);
  }

  [Test]
  public async Task Start_WhenARemovalFails_LogsWhatFailed_AndCarriesOnAsync() {
    var (scheduler, manager, logger, state) = _build(new StreamIntegrityOptions {
      FullHistoryCompare = { Enabled = false },
      SealRecheck = { Enabled = true, Schedule = "33 4 * * 0" },
    });
    manager.FailingKeys.Add(COMPARE_KEY);

    await Assert.That(async () => await scheduler.StartAsync(CancellationToken.None)).ThrowsNothing()
      .Because("a failed removal must not stop the host; the occurrence handler's no-op is the second line of defense");

    var failure = logger.Entries.Single(e => e.EventId == 102);
    await Assert.That(failure.Level).IsEqualTo(LogLevel.Warning);
    await Assert.That(failure.Message).Contains(COMPARE_KEY);
    await Assert.That(failure.Message).Contains(nameof(IntegrityCheck.FullHistoryCompare));
    await Assert.That(failure.Exception).IsTypeOf<InvalidOperationException>();
    await Assert.That(manager.Created.Single().Key).IsEqualTo(RECHECK_KEY)
      .Because("the other check is still registered after one removal failed");
    await Assert.That(state.SealRecheckCronActive).IsTrue();
  }

  [Test]
  public async Task Start_WhenARemovalIsCanceled_PropagatesTheCancellationAsync() {
    var services = new ServiceCollection();
    services.AddSingleton<IScheduleManager>(new CancelingScheduleManager());
    var scheduler = new IntegritySweepScheduler(
      services.BuildServiceProvider(), Options.Create(new StreamIntegrityOptions()), new RecordingLogger());

    await Assert.That(async () => await scheduler.StartAsync(CancellationToken.None)).Throws<OperationCanceledException>()
      .Because("a host that is stopping is not a failed removal to log and step past");
  }

  private sealed class CancelingScheduleManager : IScheduleManager {
    public Task<bool> CancelByKeyAsync(string key, CancellationToken cancellationToken = default) =>
      Task.FromException<bool>(new OperationCanceledException());
    public Task<ScheduleHandle> CreateAsync(ScheduleDefinition definition, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<bool> PauseAsync(Guid scheduleId, long? expectedVersion = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<bool> ResumeAsync(Guid scheduleId, long? expectedVersion = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<bool> CancelAsync(Guid scheduleId, long? expectedVersion = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<Guid?> TriggerNowAsync(Guid scheduleId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<ScheduleUpdateResult?> UpdateAsync(Guid scheduleId, ScheduleUpdate update, long? expectedVersion = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
  }
}
