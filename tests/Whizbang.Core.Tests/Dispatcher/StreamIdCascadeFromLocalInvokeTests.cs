using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Tests.Generated;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>
/// Tests for [GenerateStreamId] in the void LocalInvokeAsync cascade path.
/// When void LocalInvokeAsync calls a non-void receptor that returns events,
/// the command's [GenerateStreamId] should be applied BEFORE the receptor is invoked,
/// so the receptor sees the auto-generated StreamId and can propagate it to cascaded events.
/// </summary>
/// <remarks>
/// Root cause: The previous fast-path cascade method skipped envelope creation,
/// which meant _autoGenerateStreamIdIfNeeded was never called on the command.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Dispatcher.cs</code-under-test>
[Category("Dispatcher")]
[Category("StreamId")]
[Category("GenerateStreamId")]
[Category("Cascade")]
public class StreamIdCascadeFromLocalInvokeTests {

  // ========================================
  // Test Message Types
  // ========================================

  /// <summary>
  /// Command with [GenerateStreamId] and [StreamId] - should get StreamId auto-generated
  /// even when dispatched via void LocalInvokeAsync.
  /// </summary>
  public class CascadeStreamIdCommand : ICommand, IHasStreamId {
    [StreamId]
    [GenerateStreamId]
    public Guid StreamId { get; set; }

    public string Description { get; set; } = "";
  }

  /// <summary>
  /// Event returned by the receptor. The receptor copies command.StreamId to this event.
  /// Uses [DefaultRouting(Local)] so we can verify via a local tracking receptor.
  /// </summary>
  [DefaultRouting(DispatchMode.Local)]
  public record CascadeStreamIdEvent([property: StreamId] Guid StreamId, string Detail) : IEvent;

  /// <summary>
  /// Second event for tuple cascade tests.
  /// </summary>
  [DefaultRouting(DispatchMode.Local)]
  public record CascadeStreamIdEvent2([property: StreamId] Guid StreamId, string Detail) : IEvent;

  /// <summary>
  /// Result DTO (non-event) returned alongside event in a tuple.
  /// </summary>
  public record CascadeStreamIdResult(Guid StreamId, bool Success);

  /// <summary>
  /// Separate command for the tuple cascade test to avoid conflict with CascadeStreamIdCommandReceptor.
  /// </summary>
  public class CascadeTupleStreamIdCommand : ICommand, IHasStreamId {
    [StreamId]
    [GenerateStreamId]
    public Guid StreamId { get; set; }

    public string Description { get; set; } = "";
  }

  // ========================================
  // Event Tracking
  // ========================================

  public static class CascadeStreamIdEventTracker {
    private static readonly List<IEvent> _publishedEvents = [];
    private static readonly object _lock = new();

    public static void Reset() {
      lock (_lock) {
        _publishedEvents.Clear();
      }
    }

    public static void Track(IEvent evt) {
      lock (_lock) {
        _publishedEvents.Add(evt);
      }
    }

    public static IReadOnlyList<IEvent> GetPublishedEvents() {
      lock (_lock) {
        return _publishedEvents.ToList();
      }
    }

    public static int Count {
      get {
        lock (_lock) {
          return _publishedEvents.Count;
        }
      }
    }
  }

  // ========================================
  // Test Receptors
  // ========================================

  /// <summary>
  /// Non-void receptor that returns a single event with the command's StreamId.
  /// </summary>
  public class CascadeStreamIdCommandReceptor : IReceptor<CascadeStreamIdCommand, CascadeStreamIdEvent> {
    public ValueTask<CascadeStreamIdEvent> HandleAsync(
        CascadeStreamIdCommand message,
        CancellationToken cancellationToken = default) {
      // The receptor reads message.StreamId - if [GenerateStreamId] was applied,
      // this should be non-empty even though the caller didn't set it.
      return ValueTask.FromResult(new CascadeStreamIdEvent(message.StreamId, "from-receptor"));
    }
  }

  /// <summary>
  /// Event tracking receptor that records CascadeStreamIdEvent publications from cascade.
  /// </summary>
  public class CascadeStreamIdEventTrackerReceptor : IReceptor<CascadeStreamIdEvent> {
    public ValueTask HandleAsync(CascadeStreamIdEvent message, CancellationToken cancellationToken = default) {
      CascadeStreamIdEventTracker.Track(message);
      return ValueTask.CompletedTask;
    }
  }

  /// <summary>
  /// Event tracking receptor for the second event type.
  /// </summary>
  public class CascadeStreamIdEvent2TrackerReceptor : IReceptor<CascadeStreamIdEvent2> {
    public ValueTask HandleAsync(CascadeStreamIdEvent2 message, CancellationToken cancellationToken = default) {
      CascadeStreamIdEventTracker.Track(message);
      return ValueTask.CompletedTask;
    }
  }

  // ========================================
  // Tests
  // ========================================

  /// <summary>
  /// When calling void LocalInvokeAsync on a command with [GenerateStreamId],
  /// the cascaded event should have a non-empty StreamId.
  /// This tests that _autoGenerateStreamIdIfNeeded runs in the void cascade path.
  /// </summary>
  [Test]
  [NotInParallel]
  public async Task LocalInvokeAsync_CommandWithGenerateStreamId_CascadedEventGetsStreamIdAsync() {
    // Arrange
    CascadeStreamIdEventTracker.Reset();
    var command = new CascadeStreamIdCommand { Description = "test-cascade-streamid" };
    var dispatcher = _createDispatcher();

    // Act - Use void LocalInvokeAsync (no result type)
    await dispatcher.LocalInvokeAsync(command);

    // Assert - The cascaded event should have a non-empty StreamId
    await Assert.That(CascadeStreamIdEventTracker.Count).IsEqualTo(1)
      .Because("The non-void receptor's event should be cascaded");

    var cascadedEvent = CascadeStreamIdEventTracker.GetPublishedEvents()[0] as CascadeStreamIdEvent;
    await Assert.That(cascadedEvent).IsNotNull();
    await Assert.That(cascadedEvent!.StreamId).IsNotEqualTo(Guid.Empty)
      .Because("[GenerateStreamId] should have auto-generated a StreamId on the command before the receptor ran");
  }

  /// <summary>
  /// The cascaded event's StreamId should match the command's auto-generated StreamId.
  /// This verifies the receptor correctly reads the generated value.
  /// </summary>
  [Test]
  [NotInParallel]
  public async Task LocalInvokeAsync_CommandWithGenerateStreamId_StreamIdMatchesCommandStreamIdAsync() {
    // Arrange
    CascadeStreamIdEventTracker.Reset();
    var command = new CascadeStreamIdCommand { Description = "test-streamid-matches" };
    var dispatcher = _createDispatcher();

    // Act
    await dispatcher.LocalInvokeAsync(command);

    // Assert - Command's StreamId should have been generated
    await Assert.That(command.StreamId).IsNotEqualTo(Guid.Empty)
      .Because("[GenerateStreamId] should have mutated the command's StreamId before receptor invocation");

    // Assert - Cascaded event's StreamId should match the command's StreamId
    await Assert.That(CascadeStreamIdEventTracker.Count).IsEqualTo(1);
    var cascadedEvent = CascadeStreamIdEventTracker.GetPublishedEvents()[0] as CascadeStreamIdEvent;
    await Assert.That(cascadedEvent).IsNotNull();
    await Assert.That(cascadedEvent!.StreamId).IsEqualTo(command.StreamId)
      .Because("Receptor copies command.StreamId to the event, so they should match");
  }

  /// <summary>
  /// When a receptor returns a tuple with multiple events, all cascaded events should
  /// have the auto-generated StreamId from the command.
  /// </summary>
  [Test]
  [NotInParallel]
  public async Task LocalInvokeAsync_CommandWithGenerateStreamId_TupleCascade_AllEventsGetStreamIdAsync() {
    // Arrange
    CascadeStreamIdEventTracker.Reset();
    var command = new CascadeTupleStreamIdCommand { Description = "test-tuple-cascade" };
    var dispatcher = _createDispatcher();

    // Act
    await dispatcher.LocalInvokeAsync(command);

    // Assert - Command's StreamId should have been generated
    await Assert.That(command.StreamId).IsNotEqualTo(Guid.Empty)
      .Because("[GenerateStreamId] should have mutated the command's StreamId");

    // Assert - Both cascaded events should have the command's StreamId
    await Assert.That(CascadeStreamIdEventTracker.Count).IsEqualTo(2)
      .Because("Both events from the tuple should be cascaded");

    var events = CascadeStreamIdEventTracker.GetPublishedEvents();
    foreach (var evt in events) {
      var eventStreamId = evt switch {
        CascadeStreamIdEvent e => e.StreamId,
        CascadeStreamIdEvent2 e => e.StreamId,
        _ => Guid.Empty
      };
      await Assert.That(eventStreamId).IsEqualTo(command.StreamId)
        .Because("Each cascaded event should have the command's auto-generated StreamId");
    }
  }

  // ========================================
  // Tuple-returning receptor for multi-event cascade test
  // ========================================

  /// <summary>
  /// Non-void receptor that returns a tuple with two events.
  /// Both should be cascaded with the command's StreamId.
  /// </summary>
  public class CascadeTupleStreamIdCommandReceptor : IReceptor<CascadeTupleStreamIdCommand, (CascadeStreamIdEvent, CascadeStreamIdEvent2)> {
    public ValueTask<(CascadeStreamIdEvent, CascadeStreamIdEvent2)> HandleAsync(
        CascadeTupleStreamIdCommand message,
        CancellationToken cancellationToken = default) {
      var evt1 = new CascadeStreamIdEvent(message.StreamId, "tuple-event-1");
      var evt2 = new CascadeStreamIdEvent2(message.StreamId, "tuple-event-2");
      return ValueTask.FromResult((evt1, evt2));
    }
  }

  // ========================================
  // Helper Methods
  // ========================================

  private static IDispatcher _createDispatcher() {
    var services = new ServiceCollection();

    services.AddSingleton<Whizbang.Core.Observability.IServiceInstanceProvider>(
      new Whizbang.Core.Observability.ServiceInstanceProvider(configuration: null));

    services.AddReceptors();
    services.AddWhizbangDispatcher();

    var serviceProvider = services.BuildServiceProvider();
    return serviceProvider.GetRequiredService<IDispatcher>();
  }

}
