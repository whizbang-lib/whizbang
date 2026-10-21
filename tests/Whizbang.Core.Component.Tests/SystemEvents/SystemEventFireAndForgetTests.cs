// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.SystemEvents;
using Whizbang.Core.SystemEvents.Security;

namespace Whizbang.Core.Tests.SystemEvents;

/// <summary>
/// A security record is emitted fire-and-forget so a denied read is never slowed by its audit, but a
/// record that fails to store is never lost silently (#1555): the failure is logged with what was
/// lost and counted on <c>whizbang.system_events.emit_failures</c>, and the caller is neither blocked
/// nor faulted.
/// </summary>
/// <docs>fundamentals/events/system-events#fire-and-forget-emits</docs>
/// <code-under-test>src/Whizbang.Core/SystemEvents/SystemEventFireAndForget.cs</code-under-test>
[Category("SystemEvents")]
public class SystemEventFireAndForgetTests {
  private static readonly DateTimeOffset _fixedTime = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

  [Test]
  public async Task AFailedEmit_IsLoggedWithWhatWasLost_AndCountedAsync() {
    var logger = new CapturingLogger();
    var metrics = _newMetrics();
    var emitter = new ControlledEmitter();

    SystemEventFireAndForget.Emit(emitter, _denied("tenant-lost"), "tenant-lost", logger, metrics);
    emitter.Fail(new InvalidOperationException("store unavailable"));
    var entry = await logger.FirstEntry.Task.WaitAsync(TimeSpan.FromSeconds(30));

    await Assert.That(entry.Level).IsEqualTo(LogLevel.Warning);
    await Assert.That(entry.Message).Contains(nameof(AccessDenied));
    await Assert.That(entry.Message).Contains("tenant-lost");
    await Assert.That(entry.Message).Contains(SystemEventStreams.StreamId.ToString())
      .Because("the log names the stream the record was meant for");
    await Assert.That(entry.Exception?.Message).IsEqualTo("store unavailable");
    await Assert.That(_count(metrics, nameof(AccessDenied))).IsEqualTo(1L);
  }

  [Test]
  public async Task TheCaller_IsNotBlockedWhileTheEmitIsStillRunningAsync() {
    var logger = new CapturingLogger();
    var emitter = new ControlledEmitter();

    // Returns although the emit has not completed: a pending store must never hold up the caller.
    SystemEventFireAndForget.Emit(emitter, _denied("tenant-pending"), "tenant-pending", logger, _newMetrics());

    await Assert.That(emitter.Started).IsTrue();
    await Assert.That(logger.FirstEntry.Task.IsCompleted).IsFalse()
      .Because("nothing has failed yet");
    emitter.Succeed();
  }

  [Test]
  public async Task ASynchronousThrow_DoesNotReachTheCaller_AndIsStillLoggedAsync() {
    var logger = new CapturingLogger();
    var metrics = _newMetrics();
    var emitter = new ThrowingEmitter(new InvalidOperationException("thrown before any await"));

    SystemEventFireAndForget.Emit(emitter, _denied("tenant-sync"), "tenant-sync", logger, metrics);
    var entry = await logger.FirstEntry.Task.WaitAsync(TimeSpan.FromSeconds(30));

    await Assert.That(entry.Exception?.Message).IsEqualTo("thrown before any await");
    await Assert.That(_count(metrics, nameof(AccessDenied))).IsEqualTo(1L);
  }

  [Test]
  public async Task WithoutMetrics_TheFailureIsStillLoggedAsync() {
    var logger = new CapturingLogger();
    var emitter = new ThrowingEmitter(new InvalidOperationException("no meter"));

    SystemEventFireAndForget.Emit(emitter, _denied(null), null, logger, metrics: null);
    var entry = await logger.FirstEntry.Task.WaitAsync(TimeSpan.FromSeconds(30));

    await Assert.That(entry.Message).Contains(nameof(AccessDenied));
  }

  [Test]
  public async Task ASuccessfulEmit_LogsAndCountsNothingAsync() {
    var logger = new CapturingLogger();
    var metrics = _newMetrics();
    var emitter = new ControlledEmitter();

    // Completed before the call, so the whole emit (and any failure handling) runs inside it.
    emitter.Succeed();
    SystemEventFireAndForget.Emit(emitter, _denied("tenant-ok"), "tenant-ok", logger, metrics);

    await Assert.That(logger.Entries).IsEmpty();
    await Assert.That(_count(metrics, nameof(AccessDenied))).IsEqualTo(0L);
  }

  [Test]
  public async Task NullArguments_AreRefusedAsync() {
    var logger = new CapturingLogger();
    var emitter = new ControlledEmitter();
    var denied = _denied(null);

    await Assert.That(() => SystemEventFireAndForget.Emit(null!, denied, null, logger, null)).Throws<ArgumentNullException>();
    await Assert.That(() => SystemEventFireAndForget.Emit<AccessDenied>(emitter, null!, null, logger, null)).Throws<ArgumentNullException>();
    await Assert.That(() => SystemEventFireAndForget.Emit(emitter, denied, null, null!, null)).Throws<ArgumentNullException>();
  }

  // ===== Helpers =====

  internal static AccessDenied _denied(string? tenant) => new() {
    ResourceType = "Order",
    RequiredPermission = new Permission("orders:read"),
    CallerPermissions = new HashSet<Permission>(),
    CallerRoles = new HashSet<string>(),
    Scope = new PerspectiveScope { TenantId = tenant },
    Reason = AccessDenialReason.InsufficientPermission,
    Timestamp = _fixedTime
  };

  internal static EventCategoryMetrics _newMetrics() =>
    new(new WhizbangMetrics(meterFactory: new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>()));

  /// <summary>Reads the emit-failure counter's series for one event type.</summary>
  internal static long _count(EventCategoryMetrics metrics, string eventType) {
    long total = 0;
    using var listener = new MeterListener();
    listener.InstrumentPublished = (instrument, l) => {
      if (ReferenceEquals(instrument, metrics.SystemEventEmitFailures.Instrument)) {
        l.EnableMeasurementEvents(instrument);
      }
    };
    listener.SetMeasurementEventCallback<long>((_, value, tags, _) => {
      foreach (var tag in tags) {
        if (tag.Key == EventCategoryMetrics.Tags.EVENT_TYPE && Equals(tag.Value, eventType)) {
          Interlocked.Add(ref total, value);
        }
      }
    });
    listener.Start();
    listener.RecordObservableInstruments();
    return Interlocked.Read(ref total);
  }

  internal sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

  internal sealed class CapturingLogger : ILogger<ScopedLensFactory> {
    public List<LogEntry> Entries { get; } = [];
    public TaskCompletionSource<LogEntry> FirstEntry { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
      var entry = new LogEntry(logLevel, formatter(state, exception), exception);
      lock (Entries) {
        Entries.Add(entry);
      }
      FirstEntry.TrySetResult(entry);
    }
  }

  /// <summary>An emitter whose emit stays pending until the test completes or faults it.</summary>
  internal sealed class ControlledEmitter : ISystemEventEmitter {
    private readonly TaskCompletionSource _pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool Started { get; private set; }

    public void Fail(Exception exception) => _pending.SetException(exception);
    public void Succeed() => _pending.SetResult();

    public Task EmitAsync<TSystemEvent>(TSystemEvent systemEvent, CancellationToken cancellationToken = default) where TSystemEvent : ISystemEvent {
      Started = true;
      return _pending.Task;
    }

    public Task EmitEventAuditedAsync<TEvent>(Guid streamId, long streamPosition, MessageEnvelope<TEvent> envelope, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task EmitCommandAuditedAsync<TCommand, TResponse>(TCommand command, TResponse response, string receptorName, IMessageContext? context, CancellationToken cancellationToken = default) where TCommand : notnull => Task.CompletedTask;
    public bool ShouldExcludeFromAudit(Type type) => false;
  }

  /// <summary>An emitter that throws before returning a task at all.</summary>
  internal sealed class ThrowingEmitter(Exception exception) : ISystemEventEmitter {
    public Task EmitAsync<TSystemEvent>(TSystemEvent systemEvent, CancellationToken cancellationToken = default) where TSystemEvent : ISystemEvent => throw exception;
    public Task EmitEventAuditedAsync<TEvent>(Guid streamId, long streamPosition, MessageEnvelope<TEvent> envelope, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task EmitCommandAuditedAsync<TCommand, TResponse>(TCommand command, TResponse response, string receptorName, IMessageContext? context, CancellationToken cancellationToken = default) where TCommand : notnull => Task.CompletedTask;
    public bool ShouldExcludeFromAudit(Type type) => false;
  }
}
