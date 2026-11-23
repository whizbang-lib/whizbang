// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Unit.Dispatcher.Tests.Generated;
using Whizbang.Core.ValueObjects;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>
/// Inside a handling, what a receptor publishes explicitly takes an id derived from the handling, as what it returns
/// always has, and a <c>[GenerateStreamId]</c> message starts a stream derived from its own id (#2274). A retry of the
/// handling then re-derives every id, so the store's keys turn the second run's copies into no-ops instead of a
/// republish to every subscriber and a second stream nothing else refers to.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Dispatcher.cs</code-under-test>
[Category("Dispatcher")]
[Category("Messaging")]
[NotInParallel("StreamIdExtractorRegistry")] // the process-wide stream id extractor registry these tests register into
public class DispatcherDerivedIdentityInHandlingTests {

  /// <summary>An event a receptor publishes explicitly, on a stream it names.</summary>
  public record DerivedPublishProbeEvent([property: StreamId] Guid StreamId) : IEvent;

  /// <summary>The handled message: it names no stream, so a returned event does not inherit one from it.</summary>
  public record DerivedSourceCommand(string Data);

  /// <summary>An event that starts a stream of its own.</summary>
  public class DerivedStreamProbeEvent : IEvent, IHasStreamId {
    /// <inheritdoc />
    [StreamId]
    [GenerateStreamId]
    public Guid StreamId { get; set; }
  }

  /// <summary>A command that starts a stream of its own (#2537).</summary>
  public class DerivedStreamProbeCommand : ICommand, IHasStreamId {
    /// <inheritdoc />
    [StreamId]
    [GenerateStreamId]
    public Guid StreamId { get; set; }

    /// <summary>The stream the command's receptor saw.</summary>
    public Guid SeenByReceptor { get; set; }
  }

  /// <summary>An event that starts a stream of its own and is invoked locally (#2537).</summary>
  public class DerivedStreamLocalEvent : IEvent, IHasStreamId {
    /// <inheritdoc />
    [StreamId]
    [GenerateStreamId]
    public Guid StreamId { get; set; }
  }

  /// <summary>Records the stream the command carries when its receptor runs.</summary>
  public class DerivedStreamProbeCommandReceptor : IReceptor<DerivedStreamProbeCommand, DerivedStreamProbeAck> {
    /// <inheritdoc />
    public ValueTask<DerivedStreamProbeAck> HandleAsync(DerivedStreamProbeCommand message, CancellationToken cancellationToken = default) {
      message.SeenByReceptor = message.StreamId;
      return ValueTask.FromResult(new DerivedStreamProbeAck(message.StreamId));
    }
  }

  /// <summary>Handles the locally invoked event.</summary>
  public class DerivedStreamLocalEventReceptor : IReceptor<DerivedStreamLocalEvent> {
    /// <inheritdoc />
    public ValueTask HandleAsync(DerivedStreamLocalEvent message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
  }

  /// <summary>The command receptor's answer.</summary>
  public record DerivedStreamProbeAck(Guid StreamId);

  private const string HANDLER = "OrderPlacedHandler";

  // ---- explicit publishes ----

  [Test]
  public async Task PublishAsync_InsideAHandling_ARetryPublishesUnderTheSameIdsAsync() {
    var (dispatcher, _) = _createDispatcher();
    var source = (Guid)TrackedGuid.New();
    var stream = (Guid)TrackedGuid.New();

    var firstRun = await _publishTwiceAsync(dispatcher, _handling(source, HANDLER), stream);
    var retry = await _publishTwiceAsync(dispatcher, _handling(source, HANDLER), stream);

    await Assert.That(retry[0]).IsEqualTo(firstRun[0])
      .Because("a retry re-runs the receptor; its publish must carry the id the first run's had, so the stored copy absorbs it");
    await Assert.That(retry[1]).IsEqualTo(firstRun[1]);
    await Assert.That(firstRun[1]).IsNotEqualTo(firstRun[0])
      .Because("two publishes in one handling are two messages");
  }

  [Test]
  public async Task PublishAsync_WithOptions_InsideAHandling_ARetryPublishesUnderTheSameIdAsync() {
    var (dispatcher, _) = _createDispatcher();
    var source = (Guid)TrackedGuid.New();
    var stream = (Guid)TrackedGuid.New();

    Guid firstRun;
    using (ReceptorHandling.Enter(_handling(source, HANDLER))) {
      firstRun = (await dispatcher.PublishAsync(new DerivedPublishProbeEvent(stream), new DispatchOptions())).MessageId.Value;
    }
    Guid retry;
    using (ReceptorHandling.Enter(_handling(source, HANDLER))) {
      retry = (await dispatcher.PublishAsync(new DerivedPublishProbeEvent(stream), new DispatchOptions())).MessageId.Value;
    }

    await Assert.That(retry).IsEqualTo(firstRun);
  }

  [Test]
  public async Task PublishAsync_InsideAHandling_DerivesTheIdTheHandlingsCascadeWouldAsync() {
    var (dispatcher, serviceName) = _createDispatcher();
    var source = (Guid)TrackedGuid.New();

    var ids = await _publishTwiceAsync(dispatcher, _handling(source, HANDLER), (Guid)TrackedGuid.New());

    var type = TypeNameFormatter.Format(typeof(DerivedPublishProbeEvent));
    await Assert.That(ids[0]).IsEqualTo(EmissionIdentity.Derive(source, serviceName, HANDLER, type, ordinal: 0))
      .Because("one derivation for everything a handling emits, returned or published");
    await Assert.That(ids[1]).IsEqualTo(EmissionIdentity.Derive(source, serviceName, HANDLER, type, ordinal: 1));
  }

  [Test]
  public async Task PublishAsync_AfterTheHandlingReturnedAnEvent_TakesTheHandlingsNextOrdinalAsync() {
    var (dispatcher, serviceName) = _createDispatcher();
    var source = (Guid)TrackedGuid.New();
    var handling = _handling(source, HANDLER);

    await ((Core.Dispatcher)dispatcher).CascadeMessageAsync(new DerivedPublishProbeEvent((Guid)TrackedGuid.New()), handling, DispatchModes.Local);
    Guid published;
    using (ReceptorHandling.Enter(handling)) {
      published = (await dispatcher.PublishAsync(new DerivedPublishProbeEvent((Guid)TrackedGuid.New()))).MessageId.Value;
    }

    await Assert.That(published).IsEqualTo(
      EmissionIdentity.Derive(source, serviceName, HANDLER, TypeNameFormatter.Format(typeof(DerivedPublishProbeEvent)), ordinal: 1))
      .Because("returned and published emissions share the handling's ordinals, so the two can never derive one id");
  }

  [Test]
  public async Task PublishAsync_SiblingHandlersOfOneMessage_PublishUnderDistinctIdsAsync() {
    var (dispatcher, _) = _createDispatcher();
    var source = (Guid)TrackedGuid.New();
    var stream = (Guid)TrackedGuid.New();

    var first = await _publishTwiceAsync(dispatcher, _handling(source, "FirstHandler"), stream);
    var second = await _publishTwiceAsync(dispatcher, _handling(source, "SecondHandler"), stream);

    await Assert.That(second[0]).IsNotEqualTo(first[0])
      .Because("two handlers of one message each publish ordinal zero of one type; the handler keeps them apart");
  }

  [Test]
  public async Task PublishAsync_OutsideAHandling_MintsAFreshIdEachTimeAsync() {
    var (dispatcher, _) = _createDispatcher();
    var stream = (Guid)TrackedGuid.New();

    var first = (await dispatcher.PublishAsync(new DerivedPublishProbeEvent(stream))).MessageId.Value;
    var second = (await dispatcher.PublishAsync(new DerivedPublishProbeEvent(stream))).MessageId.Value;

    await Assert.That(second).IsNotEqualTo(first)
      .Because("with no handling there is nothing to derive from: each publish is a new message");
    await Assert.That(first.Version).IsEqualTo(7);
  }

  // ---- [GenerateStreamId] ----

  [Test]
  public async Task PublishAsync_GenerateStreamId_InsideAHandling_ARetryStartsTheSameStreamAsync() {
    var (dispatcher, _) = _createDispatcher();
    var source = (Guid)TrackedGuid.New();

    var firstRun = new DerivedStreamProbeEvent();
    IDeliveryReceipt receipt;
    using (ReceptorHandling.Enter(_handling(source, HANDLER))) {
      receipt = await dispatcher.PublishAsync(firstRun);
    }
    var retry = new DerivedStreamProbeEvent();
    using (ReceptorHandling.Enter(_handling(source, HANDLER))) {
      await dispatcher.PublishAsync(retry);
    }

    await Assert.That(retry.StreamId).IsEqualTo(firstRun.StreamId)
      .Because("a retry must not start a second stream that no other record refers to");
    await Assert.That(firstRun.StreamId).IsEqualTo(GeneratedStreamIdentity.Derive(receipt.MessageId.Value));
    await Assert.That(receipt.StreamId).IsEqualTo(firstRun.StreamId);
  }

  [Test]
  public async Task PublishAsync_WithOptions_GenerateStreamId_DerivesTheStreamFromThePublishedIdAsync() {
    var (dispatcher, _) = _createDispatcher();

    var published = new DerivedStreamProbeEvent();
    IDeliveryReceipt receipt;
    using (ReceptorHandling.Enter(_handling((Guid)TrackedGuid.New(), HANDLER))) {
      receipt = await dispatcher.PublishAsync(published, new DispatchOptions());
    }

    await Assert.That(published.StreamId).IsEqualTo(GeneratedStreamIdentity.Derive(receipt.MessageId.Value));
  }

  [Test]
  public async Task PublishAsync_GenerateStreamId_OutsideAHandling_StartsANewStreamDerivedFromTheFreshIdAsync() {
    var (dispatcher, _) = _createDispatcher();

    var first = new DerivedStreamProbeEvent();
    var receipt = await dispatcher.PublishAsync(first);
    var second = new DerivedStreamProbeEvent();
    await dispatcher.PublishAsync(second);

    await Assert.That(first.StreamId).IsEqualTo(GeneratedStreamIdentity.Derive(receipt.MessageId.Value))
      .Because("one rule for a generated stream: it derives from the message that starts it");
    await Assert.That(second.StreamId).IsNotEqualTo(first.StreamId);
  }

  [Test]
  public async Task CascadeMessageAsync_GenerateStreamId_ARetryOfTheHandlingStartsTheSameStreamAsync() {
    var (dispatcher, serviceName) = _createDispatcher();
    var source = (Guid)TrackedGuid.New();

    var firstRun = new DerivedStreamProbeEvent();
    await ((Core.Dispatcher)dispatcher).CascadeMessageAsync(firstRun, _handling(source, HANDLER), DispatchModes.Local);
    var retry = new DerivedStreamProbeEvent();
    await ((Core.Dispatcher)dispatcher).CascadeMessageAsync(retry, _handling(source, HANDLER), DispatchModes.Local);

    var eventId = EmissionIdentity.Derive(source, serviceName, HANDLER, TypeNameFormatter.Format(typeof(DerivedStreamProbeEvent)), ordinal: 0);
    await Assert.That(firstRun.StreamId).IsEqualTo(GeneratedStreamIdentity.Derive(eventId))
      .Because("a returned event that starts a stream derives it from its own derived id");
    await Assert.That(retry.StreamId).IsEqualTo(firstRun.StreamId);
  }

  // ---- [GenerateStreamId] on a command sent inside a handling (#2537) ----

  [Test]
  public async Task SendAsync_GenerateStreamId_InsideAHandling_ARedeliveryStartsTheSameStreamAsync() {
    var (dispatcher, serviceName) = _createDispatcher();
    var source = (Guid)TrackedGuid.New();

    var firstRun = new DerivedStreamProbeCommand();
    IDeliveryReceipt receipt;
    using (ReceptorHandling.Enter(_handling(source, HANDLER))) {
      receipt = await dispatcher.SendAsync(firstRun);
    }
    var redelivery = new DerivedStreamProbeCommand();
    using (ReceptorHandling.Enter(_handling(source, HANDLER))) {
      await dispatcher.SendAsync(redelivery);
    }

    await Assert.That(redelivery.StreamId).IsEqualTo(firstRun.StreamId)
      .Because("a redelivered handling sends the command again; it must address the stream the first run started");
    await Assert.That(firstRun.StreamId).IsEqualTo(GeneratedStreamIdentity.DeriveInHandling(
      source, serviceName, HANDLER, TypeNameFormatter.Format(typeof(DerivedStreamProbeCommand)), ordinal: 0))
      .Because("the command's own id is still minted, so its stream derives from the handling that sent it");
    await Assert.That(receipt.StreamId).IsEqualTo(firstRun.StreamId);
    await Assert.That(firstRun.SeenByReceptor).IsEqualTo(firstRun.StreamId)
      .Because("the command's receptor sees the stream the send recorded, not a second one");
  }

  [Test]
  public async Task SendAsync_GenerateStreamId_TwoCommandsInOneHandling_StartTwoStreamsInSendOrderAsync() {
    var (dispatcher, serviceName) = _createDispatcher();
    var source = (Guid)TrackedGuid.New();

    var first = new DerivedStreamProbeCommand();
    var second = new DerivedStreamProbeCommand();
    using (ReceptorHandling.Enter(_handling(source, HANDLER))) {
      await dispatcher.SendAsync(first);
      await dispatcher.SendAsync(second);
    }

    var type = TypeNameFormatter.Format(typeof(DerivedStreamProbeCommand));
    await Assert.That(first.StreamId).IsEqualTo(GeneratedStreamIdentity.DeriveInHandling(source, serviceName, HANDLER, type, ordinal: 0));
    await Assert.That(second.StreamId).IsEqualTo(GeneratedStreamIdentity.DeriveInHandling(source, serviceName, HANDLER, type, ordinal: 1))
      .Because("each send in a handling takes the next ordinal once, however often the send path generates the stream");
  }

  [Test]
  public async Task SendAsync_GenerateStreamId_DoesNotShiftTheHandlingsEmissionIdsAsync() {
    var (dispatcher, serviceName) = _createDispatcher();
    var source = (Guid)TrackedGuid.New();

    Guid published;
    using (ReceptorHandling.Enter(_handling(source, HANDLER))) {
      await dispatcher.SendAsync(new DerivedStreamProbeCommand());
      published = (await dispatcher.PublishAsync(new DerivedPublishProbeEvent((Guid)TrackedGuid.New()))).MessageId.Value;
    }

    await Assert.That(published).IsEqualTo(
      EmissionIdentity.Derive(source, serviceName, HANDLER, TypeNameFormatter.Format(typeof(DerivedPublishProbeEvent)), ordinal: 0))
      .Because("a command's stream has ordinals of its own, so a handling's event ids are what they were before it sent the command");
  }

  [Test]
  public async Task LocalInvokeAsync_GenerateStreamId_InsideAHandling_ARedeliveryStartsTheSameStreamAsync() {
    var (dispatcher, _) = _createDispatcher();
    var source = (Guid)TrackedGuid.New();

    var firstRun = new DerivedStreamProbeCommand();
    DerivedStreamProbeAck ack;
    using (ReceptorHandling.Enter(_handling(source, HANDLER))) {
      ack = await dispatcher.LocalInvokeAsync<DerivedStreamProbeAck>(firstRun);
    }
    var redelivery = new DerivedStreamProbeCommand();
    using (ReceptorHandling.Enter(_handling(source, HANDLER))) {
      await dispatcher.LocalInvokeAsync<DerivedStreamProbeAck>(redelivery);
    }

    await Assert.That(redelivery.StreamId).IsEqualTo(firstRun.StreamId);
    await Assert.That(ack.StreamId).IsEqualTo(firstRun.StreamId)
      .Because("the receptor ran on the stream the invoke settled on");
  }

  [Test]
  public async Task SendAsync_GenerateStreamId_OutsideAHandling_MintsAFreshStreamEachTimeAsync() {
    var (dispatcher, _) = _createDispatcher();

    var first = new DerivedStreamProbeCommand();
    await dispatcher.SendAsync(first);
    var second = new DerivedStreamProbeCommand();
    await dispatcher.SendAsync(second);

    await Assert.That(second.StreamId).IsNotEqualTo(first.StreamId)
      .Because("with no handling there is nothing to derive from: each send starts a new stream, as before");
    await Assert.That(first.StreamId.Version).IsEqualTo(7);
  }

  [Test]
  public async Task SendAsync_GenerateStreamId_AHandlingWithNoMessageId_MintsAsync() {
    var (dispatcher, _) = _createDispatcher();

    var first = new DerivedStreamProbeCommand();
    var second = new DerivedStreamProbeCommand();
    using (ReceptorHandling.Enter(_anonymousHandling())) {
      await dispatcher.SendAsync(first);
    }
    using (ReceptorHandling.Enter(_anonymousHandling())) {
      await dispatcher.SendAsync(second);
    }

    await Assert.That(second.StreamId).IsNotEqualTo(first.StreamId)
      .Because("a handling with no message id has no cause to derive from, as an emission of it has none");
    await Assert.That(first.StreamId).IsNotEqualTo(Guid.Empty);
  }

  [Test]
  public async Task LocalInvokeAsync_GenerateStreamIdEvent_InsideAHandling_StillMintsAsync() {
    var (dispatcher, _) = _createDispatcher();
    var source = (Guid)TrackedGuid.New();

    var first = new DerivedStreamLocalEvent();
    using (ReceptorHandling.Enter(_handling(source, HANDLER))) {
      await dispatcher.LocalInvokeAsync(first);
    }
    var second = new DerivedStreamLocalEvent();
    using (ReceptorHandling.Enter(_handling(source, HANDLER))) {
      await dispatcher.LocalInvokeAsync(second);
    }

    await Assert.That(second.StreamId).IsNotEqualTo(first.StreamId)
      .Because("an event's stream derives from the event's own id; until a locally invoked event's id derives, it mints");
  }

  // ---- helpers ----

  private static async Task<Guid[]> _publishTwiceAsync(IDispatcher dispatcher, IMessageEnvelope handling, Guid stream) {
    using (ReceptorHandling.Enter(handling)) {
      var first = await dispatcher.PublishAsync(new DerivedPublishProbeEvent(stream));
      var second = await dispatcher.PublishAsync(new DerivedPublishProbeEvent(stream));
      return [first.MessageId.Value, second.MessageId.Value];
    }
  }

  /// <summary>
  /// The envelope a worker hands a receptor for one handler row. A retry builds a new instance with the same values.
  /// </summary>
  private static MessageEnvelope<object> _handling(Guid sourceMessageId, string handlerName) => new() {
    MessageId = MessageId.From(sourceMessageId),
    Payload = new DerivedSourceCommand("origin"),
    Hops = [],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local, HandlerName = handlerName },
  };

  /// <summary>A handling whose envelope carries no message id: a default-initialized id has an empty value.</summary>
  private static MessageEnvelope<object> _anonymousHandling() => new() {
    MessageId = default,
    Payload = new DerivedSourceCommand("origin"),
    Hops = [],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local, HandlerName = HANDLER },
  };

  private static (IDispatcher Dispatcher, string ServiceName) _createDispatcher() {
    var services = new ServiceCollection();
    var instance = new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build());
    services.AddSingleton<IServiceInstanceProvider>(instance);
    services.AddReceptors();
    services.AddWhizbangDispatcher();
    var provider = services.BuildServiceProvider();
    return (provider.GetRequiredService<IDispatcher>(), instance.ServiceName);
  }
}
