// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// The level the generated runner logs a failed run at (#1976). The runner rethrows, and the perspective worker
/// records the failure against the leased rows, so the batch is retried and dead-lettered at the bound. The runner's
/// own line for the attempt is therefore a Warning carrying its exception, like the partial-progress line beside it,
/// and Error is kept for the dead-letter record.
/// </summary>
/// <docs>operations/observability/logging-categories</docs>
public class PerspectiveRunnerAttemptFailureLogLevelTests {
  private const string PERSPECTIVE_NAME = nameof(RowConflictPerspective);

  [Test]
  public async Task RunFailsBeforeAnyEventApplies_LogsNoProgressToSaveAtWarningWithItsExceptionAsync() {
    var streamId = Guid.CreateVersion7();
    var store = new FailingStore(failWrites: false);
    var entries = new List<(LogLevel Level, string Message, Exception? Exception)>();
    var runner = _runner(store, entries, new ThrowingPreInlineInvoker());

    await Assert.That(async () => await runner.RunWithEventsAsync(streamId, PERSPECTIVE_NAME, null,
        [_envelope(new RowConflictCreated { StreamId = streamId, Name = "new", Value = 1 })], CancellationToken.None))
      .Throws<InvalidOperationException>();

    await _assertAttemptAtWarningAsync(entries, "no progress to save");
  }

  [Test]
  public async Task PartialProgressCannotBeSaved_LogsTheSaveFailureAtWarningWithItsExceptionAsync() {
    var streamId = Guid.CreateVersion7();
    var store = new FailingStore(failWrites: true);
    store.Rows[streamId] = new RowConflictModel { Id = streamId, Name = "original", Value = 1 };
    var entries = new List<(LogLevel Level, string Message, Exception? Exception)>();
    var runner = _runner(store, entries, invoker: null);

    await Assert.That(async () => await runner.RunWithEventsAsync(streamId, PERSPECTIVE_NAME, null,
        [_envelope(new RowConflictValueChanged { StreamId = streamId, Value = 2 })], CancellationToken.None))
      .Throws<InvalidOperationException>();

    await _assertAttemptAtWarningAsync(entries, "Failed to save partial progress");
  }

  private static async Task _assertAttemptAtWarningAsync(
      List<(LogLevel Level, string Message, Exception? Exception)> entries, string line) {
    var attempts = entries.Where(e => e.Message.Contains(line, StringComparison.Ordinal)).ToList();
    await Assert.That(attempts.Count).IsEqualTo(1);
    await Assert.That(attempts[0].Level).IsEqualTo(LogLevel.Warning)
      .Because("the worker retries the batch and dead-letters it at the bound, so the attempt is a Warning");
    await Assert.That(attempts[0].Exception).IsTypeOf<InvalidOperationException>()
      .Because("the attempt's exception is attached, so the Warning says what failed");
    await Assert.That(entries.Any(e => e.Level >= LogLevel.Error)).IsFalse()
      .Because("nothing was dead-lettered, so nothing logs at Error");
  }

  private static IPerspectiveRunner _runner(
      IPerspectiveStore<RowConflictModel> rows,
      List<(LogLevel Level, string Message, Exception? Exception)> entries,
      IReceptorInvoker? invoker) {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<RowConflictPerspective>();
    if (invoker is not null) {
      services.AddSingleton(invoker);
      services.AddSingleton<IReceptorRegistry>(new PreInlineOnlyRegistry());
    }
    var provider = services.BuildServiceProvider();
    // The real generated runner, resolved by name: naming a generated type in source does not compile in a
    // workspace that loads without running the generators (the formatting gate).
    var runnerType = typeof(PerspectiveRunnerAttemptFailureLogLevelTests).Assembly
      .GetType("Whizbang.Core.Tests.Generated.RowConflictPerspectiveRunner", throwOnError: true)!;
    var logger = Activator.CreateInstance(typeof(RecordingLogger<>).MakeGenericType(runnerType), entries)!;
    var ctor = runnerType.GetConstructors().Single();
    var args = new object?[ctor.GetParameters().Length];
    args[0] = provider;
    args[1] = logger;
    args[2] = new PerspectiveRowConflictRetryTests.EmptyEventStore();
    args[3] = rows;
    args[4] = provider.GetRequiredService<IServiceScopeFactory>();
    return (IPerspectiveRunner)ctor.Invoke(args);
  }

  private static MessageEnvelope<IEvent> _envelope(IEvent payload) => new() {
    MessageId = MessageId.New(),
    Payload = payload,
    Hops = [
      new MessageHop {
        Type = HopType.Current,
        ServiceInstance = ServiceInstanceInfo.Unknown,
        Timestamp = DateTimeOffset.UtcNow,
      }
    ],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
  };

  /// <summary>Records every entry with its level and exception.</summary>
  private sealed class RecordingLogger<T>(List<(LogLevel Level, string Message, Exception? Exception)> entries) : ILogger<T> {
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) =>
      entries.Add((logLevel, formatter(state, exception), exception));
  }

  /// <summary>A store with no row versions; its writes throw when <c>failWrites</c> is set.</summary>
  private sealed class FailingStore(bool failWrites) : IPerspectiveStore<RowConflictModel> {
    public Dictionary<Guid, RowConflictModel> Rows { get; } = [];

    public Task<RowConflictModel?> GetByStreamIdAsync(Guid streamId, CancellationToken cancellationToken = default) =>
      Task.FromResult(Rows.TryGetValue(streamId, out var row) ? row : null);

    public Task<PerspectiveMetadata?> GetMetadataByStreamIdAsync(Guid streamId, CancellationToken cancellationToken = default) =>
      Task.FromResult<PerspectiveMetadata?>(null);

    public Task UpsertAsync(Guid streamId, RowConflictModel model, CancellationToken cancellationToken = default) {
      if (failWrites) {
        throw new InvalidOperationException("the row store refused the write");
      }
      Rows[streamId] = model;
      return Task.CompletedTask;
    }

    public Task UpsertWithPhysicalFieldsAsync(Guid streamId, RowConflictModel model, IDictionary<string, object?> physicalFieldValues,
        PerspectiveScope? scope = null, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<RowConflictModel?> GetByPartitionKeyAsync<TPartitionKey>(TPartitionKey partitionKey, CancellationToken cancellationToken = default)
      where TPartitionKey : notnull => Task.FromResult<RowConflictModel?>(null);

    public Task UpsertByPartitionKeyAsync<TPartitionKey>(TPartitionKey partitionKey, RowConflictModel model, CancellationToken cancellationToken = default)
      where TPartitionKey : notnull => Task.CompletedTask;

    public Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task PurgeAsync(Guid streamId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task PurgeByPartitionKeyAsync<TPartitionKey>(TPartitionKey partitionKey, CancellationToken cancellationToken = default)
      where TPartitionKey : notnull => Task.CompletedTask;
  }

  /// <summary>Throws from the inline pre-perspective stage, which runs before any event is applied.</summary>
  private sealed class ThrowingPreInlineInvoker : IReceptorInvoker {
    public ValueTask InvokeAsync(IMessageEnvelope envelope, LifecycleStage stage, ILifecycleContext? context = null,
        CancellationToken cancellationToken = default) =>
      stage == LifecycleStage.PrePerspectiveInline
        ? throw new InvalidOperationException("the pre-perspective receptor failed")
        : ValueTask.CompletedTask;
  }

  /// <summary>Reports a receptor at the inline pre-perspective stage only.</summary>
  private sealed class PreInlineOnlyRegistry : IReceptorRegistry {
    private static readonly ReceptorInfo[] _one = [
      new ReceptorInfo(typeof(RowConflictCreated), "pre-inline-probe", (_, _, _, _, _) => ValueTask.FromResult<object?>(null))
    ];

    public IReadOnlyList<ReceptorInfo> GetReceptorsFor(Type messageType, LifecycleStage stage) =>
      stage == LifecycleStage.PrePerspectiveInline ? _one : [];
    public void Register<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage { }
    public void Register<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage { }
    public bool Unregister<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage => false;
    public bool Unregister<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage => false;
  }
}
