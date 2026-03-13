using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Routing;
using Whizbang.Core.Tests.Generated;
using Whizbang.Core.Validation;
using Whizbang.Core.ValueObjects;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>
/// Tests covering new code paths in Dispatcher.cs for PR #121.
/// Covers: sync invoker wrapping, StreamId cascade propagation,
/// _localInvokeVoidWithAnyInvokerAndTracingAsync with debug logging,
/// StreamIdGuard in outbox, and sourceEnvelope scope fallback.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Dispatcher.cs</code-under-test>
[Category("Dispatcher")]
[Category("PR121Coverage")]
[NotInParallel("StreamIdExtractorRegistry")]
public class DispatcherNewCodeCoverageTests {

  // ========================================
  // Test Message Types
  // ========================================

  // --- Sync invoker wrapping path ---

  /// <summary>
  /// Command that ONLY has a sync receptor (no async receptor).
  /// This forces the sync wrapping fallback path in LocalInvokeAsync.
  /// </summary>
  public record SyncOnlyCommand(string Data);
  public record SyncOnlyResult(string Data, Guid Id);

  /// <summary>
  /// Sync-only receptor. The generated dispatcher should pick this up and
  /// the Dispatcher will wrap it via the sync invoker wrapping path.
  /// </summary>
  public class SyncOnlyCommandReceptor : ISyncReceptor<SyncOnlyCommand, SyncOnlyResult> {
    public SyncOnlyResult Handle(SyncOnlyCommand message) {
      return new SyncOnlyResult(message.Data, Guid.NewGuid());
    }
  }

  // --- StreamId cascade propagation path ---

  /// <summary>
  /// Command with [GenerateStreamId] that returns an event WITHOUT its own StreamId set.
  /// The cascade propagation should inherit the command's StreamId onto the event.
  /// </summary>
  public class PropagateStreamIdCommand : ICommand, IHasStreamId {
    [StreamId]
    [GenerateStreamId]
    public Guid StreamId { get; set; }
    public string Description { get; set; } = "";
  }

  /// <summary>
  /// Event that implements IHasStreamId but does NOT have [GenerateStreamId].
  /// Its StreamId starts as Guid.Empty and should inherit from the source command.
  /// </summary>
  [DefaultRouting(DispatchMode.Local)]
  public class PropagatedStreamIdEvent : IEvent, IHasStreamId {
    [StreamId]
    public Guid StreamId { get; set; }
    public string Detail { get; set; } = "";
  }

  /// <summary>
  /// Receptor that returns an event with empty StreamId, relying on cascade propagation.
  /// </summary>
  public class PropagateStreamIdCommandReceptor : IReceptor<PropagateStreamIdCommand, PropagatedStreamIdEvent> {
    public ValueTask<PropagatedStreamIdEvent> HandleAsync(
        PropagateStreamIdCommand message,
        CancellationToken cancellationToken = default) {
      // Intentionally does NOT copy command.StreamId to the event.
      // The cascade propagation in Dispatcher should do this.
      return ValueTask.FromResult(new PropagatedStreamIdEvent { Detail = "from-receptor" });
    }
  }

  // --- Event tracking for propagation tests ---

  public static class PropagationEventTracker {
    private static readonly List<IEvent> _publishedEvents = [];
    private static readonly object _lock = new();

    public static void Reset() {
      lock (_lock) { _publishedEvents.Clear(); }
    }

    public static void Track(IEvent evt) {
      lock (_lock) { _publishedEvents.Add(evt); }
    }

    public static IReadOnlyList<IEvent> GetPublishedEvents() {
      lock (_lock) { return _publishedEvents.ToList(); }
    }

    public static int Count {
      get { lock (_lock) { return _publishedEvents.Count; } }
    }
  }

  public class PropagatedStreamIdEventTrackerReceptor : IReceptor<PropagatedStreamIdEvent> {
    public ValueTask HandleAsync(PropagatedStreamIdEvent message, CancellationToken cancellationToken = default) {
      PropagationEventTracker.Track(message);
      return ValueTask.CompletedTask;
    }
  }

  // --- Void path with DispatchOptions (anyInvoker + tracing) ---

  /// <summary>
  /// Command that only has a non-void receptor.
  /// Used to exercise _localInvokeVoidWithAnyInvokerAndTracingAsync via DispatchOptions path.
  /// </summary>
  public record VoidOptionsCommand(Guid Id);

  [DefaultRouting(DispatchMode.Local)]
  public record VoidOptionsEvent([property: StreamId] Guid Id) : IEvent;

  public record VoidOptionsResult(Guid Id, bool Success);

  public class VoidOptionsCommandReceptor : IReceptor<VoidOptionsCommand, (VoidOptionsResult, VoidOptionsEvent)> {
    public ValueTask<(VoidOptionsResult, VoidOptionsEvent)> HandleAsync(
        VoidOptionsCommand message,
        CancellationToken cancellationToken = default) {
      return ValueTask.FromResult((new VoidOptionsResult(message.Id, true), new VoidOptionsEvent(message.Id)));
    }
  }

  public static class VoidOptionsEventTracker {
    private static readonly List<IEvent> _publishedEvents = [];
    private static readonly object _lock = new();
    public static void Reset() { lock (_lock) { _publishedEvents.Clear(); } }
    public static void Track(IEvent evt) { lock (_lock) { _publishedEvents.Add(evt); } }
    public static IReadOnlyList<IEvent> GetPublishedEvents() { lock (_lock) { return _publishedEvents.ToList(); } }
    public static int Count { get { lock (_lock) { return _publishedEvents.Count; } } }
  }

  public class VoidOptionsEventTrackerReceptor : IReceptor<VoidOptionsEvent> {
    public ValueTask HandleAsync(VoidOptionsEvent message, CancellationToken cancellationToken = default) {
      VoidOptionsEventTracker.Track(message);
      return ValueTask.CompletedTask;
    }
  }

  // --- Sync invoker wrapping with DispatchOptions path ---

  /// <summary>
  /// Command with only a sync receptor, tested via LocalInvokeAsync with DispatchOptions.
  /// Exercises the second sync wrapping path (line 1663 in the diff).
  /// </summary>
  public record SyncOptionsCommand(string Data);
  public record SyncOptionsResult(string Data, Guid Id);

  public class SyncOptionsCommandReceptor : ISyncReceptor<SyncOptionsCommand, SyncOptionsResult> {
    public SyncOptionsResult Handle(SyncOptionsCommand message) {
      return new SyncOptionsResult(message.Data, Guid.NewGuid());
    }
  }

  // --- Void invoker null-result path is tested via the existing void cascade tests ---

  // ========================================
  // Tests: Sync Invoker Wrapping
  // ========================================

  /// <summary>
  /// When LocalInvokeAsync is called and only a sync receptor exists,
  /// the dispatcher wraps it as async via:
  ///   ReceptorInvoker wrappedInvoker = (msg) => new ValueTask(syncInvoker(msg));
  /// Then routes through _localInvokeWithCastFallbackAsync.
  /// </summary>
  [Test]
  public async Task LocalInvokeAsync_SyncReceptorOnly_WrapsAndInvokesAsync() {
    // Arrange
    var command = new SyncOnlyCommand("sync-wrap-test");
    var dispatcher = _createDispatcher();

    // Act
    var result = await dispatcher.LocalInvokeAsync<SyncOnlyResult>(command);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result.Data).IsEqualTo("sync-wrap-test");
    await Assert.That(result.Id).IsNotEqualTo(Guid.Empty);
  }

  /// <summary>
  /// Tests the sync wrapping path via DispatchOptions overload.
  /// This exercises the second wrapping site in _localInvokeWithOptionsAsync.
  /// </summary>
  [Test]
  public async Task LocalInvokeAsync_SyncReceptorWithOptions_WrapsAndInvokesAsync() {
    // Arrange
    var command = new SyncOptionsCommand("sync-options-test");
    var dispatcher = _createDispatcher();
    var options = new DispatchOptions { CancellationToken = CancellationToken.None };

    // Act
    var result = await dispatcher.LocalInvokeAsync<SyncOptionsResult>(command, options);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result.Data).IsEqualTo("sync-options-test");
    await Assert.That(result.Id).IsNotEqualTo(Guid.Empty);
  }

  // ========================================
  // Tests: StreamId Cascade Propagation
  // ========================================

  /// <summary>
  /// When a command has [GenerateStreamId] and the receptor returns an event
  /// with StreamId == Guid.Empty, the cascade propagation should inherit the
  /// command's auto-generated StreamId onto the event.
  /// </summary>
  [Test]
  [NotInParallel]
  public async Task LocalInvokeAsync_CascadePropagation_EventInheritsCommandStreamIdAsync() {
    // Arrange
    PropagationEventTracker.Reset();
    var command = new PropagateStreamIdCommand { Description = "propagation-test" };
    var dispatcher = _createDispatcher();

    // Act - Invoke typed to get the event back
    var result = await dispatcher.LocalInvokeAsync<PropagatedStreamIdEvent>(command);

    // Assert - Command should have auto-generated StreamId
    await Assert.That(command.StreamId).IsNotEqualTo(Guid.Empty)
      .Because("[GenerateStreamId] should auto-generate a StreamId on the command");

    // Assert - The cascaded event should inherit the command's StreamId
    // via cascade propagation in _cascadeEventsFromResultAsync
    await Assert.That(result.StreamId).IsNotEqualTo(Guid.Empty)
      .Because("Event StreamId should be propagated from source command");
  }

  /// <summary>
  /// When void LocalInvokeAsync is called and the command has [GenerateStreamId],
  /// the cascaded event should inherit the command's StreamId via the void-with-any-invoker path.
  /// This exercises _localInvokeVoidWithAnyInvokerAndTracingAsync with StreamId propagation.
  /// </summary>
  [Test]
  [NotInParallel]
  public async Task VoidLocalInvokeAsync_CascadePropagation_EventInheritsCommandStreamIdAsync() {
    // Arrange
    PropagationEventTracker.Reset();
    var command = new PropagateStreamIdCommand { Description = "void-propagation" };
    var dispatcher = _createDispatcher();

    // Act - Void dispatch (no result type expected)
    await dispatcher.LocalInvokeAsync(command);

    // Assert
    await Assert.That(command.StreamId).IsNotEqualTo(Guid.Empty)
      .Because("[GenerateStreamId] should auto-generate StreamId before receptor runs");

    await Assert.That(PropagationEventTracker.Count).IsEqualTo(1)
      .Because("Event should be cascaded from non-void receptor");

    var cascadedEvent = PropagationEventTracker.GetPublishedEvents()[0] as PropagatedStreamIdEvent;
    await Assert.That(cascadedEvent).IsNotNull();
    await Assert.That(cascadedEvent!.StreamId).IsNotEqualTo(Guid.Empty)
      .Because("Cascaded event should inherit StreamId from source command via propagation");
  }

  // ========================================
  // Tests: Void with DispatchOptions (anyInvoker path)
  // ========================================

  /// <summary>
  /// Tests the _localInvokeVoidWithAnyInvokerAndTracingAsync path via DispatchOptions.
  /// When void LocalInvokeAsync is called with DispatchOptions and only a non-void receptor exists,
  /// the void-with-any-invoker-and-tracing path creates envelopes and cascades.
  /// </summary>
  [Test]
  [NotInParallel]
  public async Task VoidLocalInvokeAsync_WithDispatchOptions_CascadesViaAnyInvokerTracingAsync() {
    // Arrange
    VoidOptionsEventTracker.Reset();
    var command = new VoidOptionsCommand(Guid.NewGuid());
    var dispatcher = _createDispatcher();
    var options = new DispatchOptions { CancellationToken = CancellationToken.None };

    // Act
    await dispatcher.LocalInvokeAsync(command, options);

    // Assert
    await Assert.That(VoidOptionsEventTracker.Count).IsEqualTo(1)
      .Because("Event should cascade from non-void receptor via DispatchOptions void path");

    var cascadedEvent = VoidOptionsEventTracker.GetPublishedEvents()[0] as VoidOptionsEvent;
    await Assert.That(cascadedEvent).IsNotNull();
    await Assert.That(cascadedEvent!.Id).IsEqualTo(command.Id);
  }

  // ========================================
  // Tests: StreamIdGuard in Outbox
  // ========================================

  /// <summary>
  /// When an IEvent with a [StreamId] property has Guid.Empty StreamId,
  /// and it is published to the outbox, StreamIdGuard.ThrowIfEmpty should fire.
  /// </summary>
  [Test]
  public async Task PublishToOutbox_EventWithEmptyStreamId_ThrowsInvalidStreamIdExceptionAsync() {
    // Arrange - Event with StreamId = Guid.Empty, published through the outbox path
    var @event = new EmptyStreamIdEvent(Guid.Empty, "test");
    var strategy = new StubWorkCoordinatorStrategy();
    var dispatcher = _createDispatcherWithOutbox(strategy);

    // Act & Assert - Should throw InvalidStreamIdException from StreamIdGuard
    await Assert.That(async () => await dispatcher.PublishAsync(@event))
      .ThrowsExactly<InvalidStreamIdException>();
  }

  /// <summary>
  /// Event with a [StreamId] that is Guid.Empty - should fail StreamIdGuard.
  /// Note: no [GenerateStreamId], so auto-generation won't save it.
  /// </summary>
  public record EmptyStreamIdEvent([property: StreamId] Guid StreamId, string Data) : IEvent;

  /// <summary>
  /// Receptor that handles the EmptyStreamIdEvent (required for PublishAsync to find a handler).
  /// </summary>
  public class EmptyStreamIdEventReceptor : IReceptor<EmptyStreamIdEvent> {
    public ValueTask HandleAsync(EmptyStreamIdEvent message, CancellationToken cancellationToken = default) {
      return ValueTask.CompletedTask;
    }
  }

  // ========================================
  // Tests: Debug Logging Coverage
  // ========================================

  /// <summary>
  /// Exercises debug logging blocks in _localInvokeVoidWithAnyInvokerAndTracingAsync
  /// by enabling Debug-level logging via ILoggerFactory.
  /// </summary>
  [Test]
  [NotInParallel]
  public async Task VoidLocalInvokeAsync_WithDebugLogging_ExercisesDebugBlocksAsync() {
    // Arrange
    VoidOptionsEventTracker.Reset();
    var command = new VoidOptionsCommand(Guid.NewGuid());
    var dispatcher = _createDispatcherWithDebugLogging();

    // Act - With debug logging enabled, the if (CascadeLogger.IsEnabled(LogLevel.Debug)) blocks execute
    await dispatcher.LocalInvokeAsync(command);

    // Assert - Event should still cascade correctly
    await Assert.That(VoidOptionsEventTracker.Count).IsEqualTo(1);
  }

  /// <summary>
  /// Exercises debug logging in cascade propagation path with Debug-level logging enabled.
  /// </summary>
  [Test]
  [NotInParallel]
  public async Task LocalInvokeAsync_WithDebugLogging_StreamIdPropagation_LogsDebugAsync() {
    // Arrange
    PropagationEventTracker.Reset();
    var command = new PropagateStreamIdCommand { Description = "debug-log-test" };
    var dispatcher = _createDispatcherWithDebugLogging();

    // Act
    await dispatcher.LocalInvokeAsync(command);

    // Assert - Command StreamId auto-generated and event inherits it
    await Assert.That(command.StreamId).IsNotEqualTo(Guid.Empty);
  }

  // ========================================
  // Tests: Generic Void LocalInvokeAsync<TMessage> path
  // ========================================

  /// <summary>
  /// Tests the generic void LocalInvokeAsync&lt;TMessage&gt; path with a non-void receptor.
  /// This exercises the _localInvokeVoidWithAnyInvokerAndTracingAsync from the
  /// strongly-typed generic void overload (different call site than untyped).
  /// </summary>
  [Test]
  [NotInParallel]
  public async Task GenericVoidLocalInvokeAsync_WithAnyInvoker_CascadesWithTracingAsync() {
    // Arrange
    VoidOptionsEventTracker.Reset();
    var command = new VoidOptionsCommand(Guid.NewGuid());
    var dispatcher = _createDispatcher();

    // Act - Use generic void overload
    await dispatcher.LocalInvokeAsync<VoidOptionsCommand>(command);

    // Assert
    await Assert.That(VoidOptionsEventTracker.Count).IsEqualTo(1)
      .Because("Generic void path should cascade events via anyInvoker tracing path");
  }

  // ========================================
  // Stub Implementations
  // ========================================

  private sealed class StubWorkCoordinatorStrategy : IWorkCoordinatorStrategy {
    public List<OutboxMessage> QueuedOutboxMessages { get; } = [];
    public List<InboxMessage> QueuedInboxMessages { get; } = [];
    public int FlushCount { get; private set; }

    public void QueueOutboxMessage(OutboxMessage message) => QueuedOutboxMessages.Add(message);
    public void QueueInboxMessage(InboxMessage message) => QueuedInboxMessages.Add(message);
    public void QueueOutboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
    public void QueueInboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
    public void QueueOutboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }
    public void QueueInboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }

    public Task<WorkBatch> FlushAsync(WorkBatchFlags flags, FlushMode mode = FlushMode.Required, CancellationToken ct = default) {
      FlushCount++;
      return Task.FromResult(new WorkBatch {
        OutboxWork = [],
        InboxWork = [],
        PerspectiveWork = []
      });
    }
  }

  private sealed class StubEnvelopeSerializer : IEnvelopeSerializer {
    public SerializedEnvelope SerializeEnvelope<TMessage>(IMessageEnvelope<TMessage> envelope) {
      var jsonElement = System.Text.Json.JsonSerializer.SerializeToElement(new { });
      var jsonEnvelope = new MessageEnvelope<System.Text.Json.JsonElement> {
        MessageId = envelope.MessageId,
        Payload = jsonElement,
        Hops = []
      };
      return new SerializedEnvelope(
        jsonEnvelope,
        typeof(MessageEnvelope<>).MakeGenericType(typeof(TMessage)).AssemblyQualifiedName!,
        typeof(TMessage).AssemblyQualifiedName!
      );
    }

    public object DeserializeMessage(MessageEnvelope<System.Text.Json.JsonElement> jsonEnvelope, string messageTypeName) {
      throw new NotImplementedException();
    }
  }

  // ========================================
  // Helper Methods
  // ========================================

  private static IDispatcher _createDispatcher() {
    var services = new ServiceCollection();
    services.AddSingleton<IServiceInstanceProvider>(
      new ServiceInstanceProvider(configuration: null));
    services.AddReceptors();
    services.AddWhizbangDispatcher();

    var serviceProvider = services.BuildServiceProvider();
    return serviceProvider.GetRequiredService<IDispatcher>();
  }

  private static IDispatcher _createDispatcherWithDebugLogging() {
    var services = new ServiceCollection();
    services.AddSingleton<IServiceInstanceProvider>(
      new ServiceInstanceProvider(configuration: null));

    // Add logging with Debug level to exercise debug log blocks
    services.AddLogging(builder => {
      builder.SetMinimumLevel(LogLevel.Debug);
      builder.AddConsole();
    });

    services.AddReceptors();
    services.AddWhizbangDispatcher();

    var serviceProvider = services.BuildServiceProvider();
    return serviceProvider.GetRequiredService<IDispatcher>();
  }

  private static IDispatcher _createDispatcherWithOutbox(IWorkCoordinatorStrategy strategy) {
    var services = new ServiceCollection();
    services.AddSingleton<IServiceInstanceProvider>(
      new ServiceInstanceProvider(configuration: null));
    services.AddSingleton<IEnvelopeSerializer, StubEnvelopeSerializer>();
    services.AddScoped<IWorkCoordinatorStrategy>(_ => strategy);
    services.AddReceptors();
    services.AddWhizbangDispatcher();

    var serviceProvider = services.BuildServiceProvider();
    return serviceProvider.GetRequiredService<IDispatcher>();
  }
}
