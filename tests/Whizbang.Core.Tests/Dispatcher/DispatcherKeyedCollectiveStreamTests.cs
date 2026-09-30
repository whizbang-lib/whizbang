using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives.Sync;
using Whizbang.Core.Tests.Generated;

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>
/// A collective carrying an ordering key keeps its key's stream through the dispatcher (#963). The dispatcher mints a
/// stream id for every <c>[GenerateStreamId]</c> event; for a keyed collective the minted id is discarded by the
/// event itself, and every place the dispatcher records the stream has to read it back rather than keep the id it
/// minted.
/// </summary>
/// <docs>fundamentals/messaging/collective-events</docs>
[Category("Dispatcher")]
[Category("CollectiveEvents")]
[NotInParallel] // ScopedEventTrackerAccessor.CurrentTracker is static
public sealed class DispatcherKeyedCollectiveStreamTests {
  public sealed record EmitKeyedFlipCommand(string Key);

  public sealed record KeyedFlipCollectiveEvent : CollectiveEventBase;

  public sealed class EmitKeyedFlipReceptor : IReceptor<EmitKeyedFlipCommand, KeyedFlipCollectiveEvent> {
    public ValueTask<KeyedFlipCollectiveEvent> HandleAsync(EmitKeyedFlipCommand message, CancellationToken cancellationToken = default) =>
      ValueTask.FromResult(new KeyedFlipCollectiveEvent { Scope = new TenantCollectiveScope("t-1"), OrderingKey = message.Key });
  }

  [Test]
  public async Task PublishAsync_KeyedCollective_ReportsTheKeysStreamAsync() {
    var evt = new KeyedFlipCollectiveEvent { Scope = new TenantCollectiveScope("t-1"), OrderingKey = "family-7" };
    var dispatcher = _createDispatcher();

    var receipt = await dispatcher.PublishAsync(evt);

    var expected = CollectiveOrdering.StreamIdFor(new TenantCollectiveScope("t-1"), "family-7");
    await Assert.That(evt.StreamId).IsEqualTo(expected);
    await Assert.That(receipt.StreamId).IsEqualTo(expected);
  }

  [Test]
  public async Task CascadedKeyedCollective_IsTrackedOnTheKeysStreamAsync() {
    var tracker = new ScopedEventTracker();
    var dispatcher = _createDispatcher();

    ScopedEventTrackerAccessor.CurrentTracker = tracker;
    try {
      await dispatcher.LocalInvokeAsync<EmitKeyedFlipCommand, KeyedFlipCollectiveEvent>(new EmitKeyedFlipCommand("family-7"));
    } finally {
      ScopedEventTrackerAccessor.CurrentTracker = null;
    }

    var tracked = tracker.GetEmittedEvents().Where(e => e.EventType == typeof(KeyedFlipCollectiveEvent)).ToList();
    await Assert.That(tracked.Count).IsEqualTo(1);
    await Assert.That(tracked[0].StreamId).IsEqualTo(CollectiveOrdering.StreamIdFor(new TenantCollectiveScope("t-1"), "family-7"))
      .Because("A caller waiting on the collective waits on the stream it was tracked under; the minted id is not "
        + "the stream the event is stored on.");
  }

  private static IDispatcher _createDispatcher() {
    var services = new ServiceCollection();
    services.AddSingleton<IServiceInstanceProvider>(
      new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()));
    services.AddReceptors();
    services.AddWhizbangDispatcher();
    return services.BuildServiceProvider().GetRequiredService<IDispatcher>();
  }
}
