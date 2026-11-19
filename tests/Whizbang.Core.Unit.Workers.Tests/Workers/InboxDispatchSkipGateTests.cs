// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Routing;
using Whizbang.Core.Workers;
using Whizbang.Testing.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// RED-first locks on Slice 4 of the message-discard-policy plan: the inbox
/// dispatch worker calls <see cref="IMessageDiscardPolicy.EvaluateInbox"/> as an
/// early gate. When the policy says discard (the row references a type with no
/// active consumer), the helper records the skip telemetry and tells the caller
/// to short-circuit the row — avoiding the lease / scope / security-context
/// overhead for messages that wouldn't fire any receptor anyway.
/// </summary>
public class InboxDispatchSkipGateTests {

  private sealed class TestRegistry : IReceptorRegistryQuery {
    public HashSet<string> Consumed { get; } = [];
    public bool HasReceptors(LifecycleStage stage, string messageType) => Consumed.Contains(messageType);
    public bool HasInboxHandler(string messageType) => Consumed.Contains(messageType);
    public bool HasAnyConsumer(string messageType) => Consumed.Contains(messageType);
  }

  private sealed class RecordingLogger : ILogger {
    public List<(LogLevel Level, string Message)> Entries { get; } = [];
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullDisposable.Instance;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
      Entries.Add((logLevel, formatter(state, exception)));
    }
    private sealed class NullDisposable : IDisposable { public static readonly NullDisposable Instance = new(); public void Dispose() { } }
  }

  private sealed class TestLogger<T>(RecordingLogger inner) : ILogger<T> {
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);
    public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
      => inner.Log(logLevel, eventId, state, exception, formatter);
  }

  // A tag declared where this host's generator never scanned: the tag registry answers for it,
  // which is what a shared contracts assembly looks like from the gate's side, while the consumer
  // registry does not list the type at all.
  [System.AttributeUsage(System.AttributeTargets.Class)]
  private sealed class ContractsNotifyTagAttribute : Whizbang.Core.Attributes.MessageTagAttribute;

  private sealed record TaggedInContractsEvent : IEvent;

  private sealed class ForeignAssemblyTagRegistry : Whizbang.Core.Tags.IMessageTagRegistry {
    public IEnumerable<Whizbang.Core.Tags.MessageTagRegistration> GetTagsFor(Type messageType)
      => messageType == typeof(TaggedInContractsEvent) ? GetAllTags() : [];

    public IEnumerable<Whizbang.Core.Tags.MessageTagRegistration> GetAllTags() => [
      new Whizbang.Core.Tags.MessageTagRegistration {
        MessageType = typeof(TaggedInContractsEvent),
        AttributeType = typeof(ContractsNotifyTagAttribute),
        Tag = "notify",
        PayloadBuilder = static _ => default,
        AttributeFactory = static () => new ContractsNotifyTagAttribute { Tag = "notify" },
      },
    ];
  }

  private sealed class NoOpTagHook : Whizbang.Core.Tags.IMessageTagHook<ContractsNotifyTagAttribute> {
    public ValueTask<System.Text.Json.JsonElement?> OnTaggedMessageAsync(
        Whizbang.Core.Tags.TagContext<ContractsNotifyTagAttribute> context, CancellationToken ct)
      => ValueTask.FromResult<System.Text.Json.JsonElement?>(null);
  }

  private static MessageDiscardPolicy _policyWithTagHooks(bool withHook, string meterName) {
    var core = new Whizbang.Core.Configuration.WhizbangCoreOptions();
    if (withHook) {
      core.Tags.UseHook<ContractsNotifyTagAttribute, NoOpTagHook>();
    }
    return new MessageDiscardPolicy(
      registry: new TestRegistry(),                      // nothing consumes it
      logger: NullLogger<MessageDiscardPolicy>.Instance,
      meter: new Meter(meterName),
      routingOptions: Options.Create(new RoutingOptions()),
      markerResolver: new EventMarkerResolver(NullMessageTypeCatalog.Instance),
      coreOptions: core);
  }

  /// <summary>
  /// An event this host keeps only because it acts on a tag the event carries is kept, and the same
  /// event is dropped by a host that acts on no such tag.
  /// </summary>
  /// <remarks>
  /// <para>
  /// A receptor and a perspective are not the only reasons a host wants an event. A host whose job
  /// is to send the notification a tag declares consumes it by acting on the tag -- and the gate
  /// asked only the generated consumer registry, which lists what the generator scanned for this
  /// host. A tag declared in a shared contracts assembly counted for nothing, so the host that was
  /// meant to send the notification dropped the event on arrival, sent nothing, and logged nothing
  /// above Debug.
  /// </para>
  /// <para>
  /// Both halves are asserted because either alone would be wrong: keeping every tagged event would
  /// keep other hosts' work as well, and keeping none is the defect.
  /// </para>
  /// </remarks>
  [Test]
  public async Task Gates_TaggedEventWithNoConsumer_KeptWhereThisHostActsOnTheTagAsync() {
    Whizbang.Core.Tags.MessageTagRegistry.Register(new ForeignAssemblyTagRegistry(), priority: 100);
    var wireName = typeof(TaggedInContractsEvent).AssemblyQualifiedName!;

    var acting = _policyWithTagHooks(withHook: true, "Whizbang.Tests.InboxDispatchSkipGateTests.Tag1");
    await Assert.That(acting.EvaluateReceive(wireName, "topic", "sub").ShouldDiscard).IsFalse()
      .Because("this host's whole reason for the event is the tag it carries, wherever the tag was declared");
    await Assert.That(acting.EvaluateInbox(wireName).ShouldDiscard).IsFalse()
      .Because("the inbox gate drops for the same reason the receive gate does, so it needs the same answer");

    var notActing = _policyWithTagHooks(withHook: false, "Whizbang.Tests.InboxDispatchSkipGateTests.Tag2");
    await Assert.That(notActing.EvaluateReceive(wireName, "topic", "sub").ShouldDiscard).IsTrue()
      .Because("a tag no hook here acts on is another host's work, and keeping it would keep everything");
  }

  [Test]
  public async Task ShouldSkipInbox_TypeNoLongerInRegistry_RecordsSkip_AndReturnsTrueAsync() {
    var registry = new TestRegistry();  // empty
    var policyLogger = new RecordingLogger();
    var meter = new Meter("Whizbang.Tests.InboxDispatchSkipGateTests.A");
    var policy = new MessageDiscardPolicy(registry: registry, logger: new TestLogger<MessageDiscardPolicy>(policyLogger), meter: meter, routingOptions: Options.Create(new RoutingOptions()), markerResolver: new EventMarkerResolver(NullMessageTypeCatalog.Instance));
    long skippedCount = 0;
    using var listener = new MeterListener {
      InstrumentPublished = (i, l) => { if (i.Meter == meter && i.Name == MessageDiscardPolicy.COUNTER_NAME) { l.EnableMeasurementEvents(i); } }
    };
    listener.SetMeasurementEventCallback<long>((_, v, _, _) => skippedCount += v);
    listener.Start();

    var shouldSkip = InboxDispatchWorker.ShouldSkipInbox(
      discardPolicy: policy,
      messageType: "Test.Contracts.Foo",
      messageId: Guid.Parse("11111111-1111-1111-1111-111111111111"));
    // Passive counter: series report only at collection (the declared gate series at zero).
    listener.RecordObservableInstruments();

    await Assert.That(shouldSkip).IsTrue();
    // RegistryChanged → Information level per policy
    await Assert.That(policyLogger.Entries.Count).IsEqualTo(1);
    await Assert.That(policyLogger.Entries[0].Level).IsEqualTo(LogLevel.Information);
    await Assert.That(skippedCount).IsEqualTo(1L);
  }

  [Test]
  public async Task ShouldSkipInbox_TypeHasConsumer_ReturnsFalse_NoTelemetryAsync() {
    var registry = new TestRegistry { Consumed = { "Test.Contracts.Foo" } };
    var policyLogger = new RecordingLogger();
    var meter = new Meter("Whizbang.Tests.InboxDispatchSkipGateTests.B");
    var policy = new MessageDiscardPolicy(registry: registry, logger: new TestLogger<MessageDiscardPolicy>(policyLogger), meter: meter, routingOptions: Options.Create(new RoutingOptions()), markerResolver: new EventMarkerResolver(NullMessageTypeCatalog.Instance));
    long skippedCount = 0;
    using var listener = new MeterListener {
      InstrumentPublished = (i, l) => { if (i.Meter == meter && i.Name == MessageDiscardPolicy.COUNTER_NAME) { l.EnableMeasurementEvents(i); } }
    };
    listener.SetMeasurementEventCallback<long>((_, v, _, _) => skippedCount += v);
    listener.Start();

    var shouldSkip = InboxDispatchWorker.ShouldSkipInbox(
      discardPolicy: policy,
      messageType: "Test.Contracts.Foo",
      messageId: Guid.Parse("22222222-2222-2222-2222-222222222222"));
    // Passive counter: collect so the zero below is every series' real count, not silence.
    listener.RecordObservableInstruments();

    await Assert.That(shouldSkip).IsFalse();
    await Assert.That(policyLogger.Entries.Count).IsEqualTo(0);
    await Assert.That(skippedCount).IsEqualTo(0L);
  }

  /// <summary>
  /// A broker dead letter recovered into the inbox used to carry its envelope's type name (#934). The
  /// gate asked whether anything consumes the envelope, found nothing, and skipped the row as
  /// RegistryChanged: a recovered message lost after recovery. The gate asks about the payload.
  /// </summary>
  [Test]
  public async Task ShouldSkipInbox_EnvelopeWrappedRow_WhosePayloadHasAConsumer_IsKeptAsync() {
    var registry = new TestRegistry { Consumed = { "Test.Contracts.Foo, Test.Contracts" } };
    var policyLogger = new RecordingLogger();
    var policy = new MessageDiscardPolicy(registry: registry, logger: new TestLogger<MessageDiscardPolicy>(policyLogger), meter: new Meter("Whizbang.Tests.InboxDispatchSkipGateTests.D"), routingOptions: Options.Create(new RoutingOptions()), markerResolver: new EventMarkerResolver(NullMessageTypeCatalog.Instance));

    var shouldSkip = InboxDispatchWorker.ShouldSkipInbox(
      discardPolicy: policy,
      messageType: EnvelopeTypeNameHelper.Format("Test.Contracts.Foo, Test.Contracts"),
      messageId: Guid.Parse("44444444-4444-4444-4444-444444444444"));

    await Assert.That(shouldSkip).IsFalse()
      .Because("the envelope is a transport wrapper; the payload it carries has a consumer");
    await Assert.That(policyLogger.Entries).IsEmpty();
  }

  [Test]
  public async Task ShouldSkipInbox_EnvelopeWrappedRow_WhosePayloadHasNoConsumer_IsStillSkippedAsync() {
    var registry = new TestRegistry { Consumed = { "Test.Contracts.Foo, Test.Contracts" } };
    var policy = new MessageDiscardPolicy(registry: registry, logger: NullLogger<MessageDiscardPolicy>.Instance, meter: new Meter("Whizbang.Tests.InboxDispatchSkipGateTests.E"), routingOptions: Options.Create(new RoutingOptions()), markerResolver: new EventMarkerResolver(NullMessageTypeCatalog.Instance));

    var decision = policy.EvaluateInbox(EnvelopeTypeNameHelper.Format("Test.Contracts.Bar, Test.Contracts"));

    await Assert.That(decision.ShouldDiscard).IsTrue();
    await Assert.That(decision.Reason).IsEqualTo(MessageDiscardReason.RegistryChanged);
  }

  [Test]
  public async Task EvaluateReceive_EnvelopeWrappedName_WhosePayloadHasAConsumer_IsKeptAsync() {
    var registry = new TestRegistry { Consumed = { "Test.Contracts.Foo, Test.Contracts" } };
    var policy = new MessageDiscardPolicy(registry: registry, logger: NullLogger<MessageDiscardPolicy>.Instance, meter: new Meter("Whizbang.Tests.InboxDispatchSkipGateTests.F"), routingOptions: Options.Create(new RoutingOptions()), markerResolver: new EventMarkerResolver(NullMessageTypeCatalog.Instance));

    var decision = policy.EvaluateReceive(EnvelopeTypeNameHelper.Format("Test.Contracts.Foo, Test.Contracts"), topic: "t", subscription: "s");

    await Assert.That(decision.ShouldDiscard).IsFalse()
      .Because("a transport that hands the gate its envelope type name is asking about the payload inside it");
  }

  [Test]
  public async Task ShouldSkipInbox_NoPolicyWired_ReturnsFalse_PreservesLegacyBehaviorAsync() {
    var shouldSkip = InboxDispatchWorker.ShouldSkipInbox(
      discardPolicy: new MessageDiscardPolicy(new PermissiveReceptorRegistryQuery(), NullLogger<MessageDiscardPolicy>.Instance, new System.Diagnostics.Metrics.Meter("test"), Options.Create(new RoutingOptions()), new EventMarkerResolver(NullMessageTypeCatalog.Instance)),
      messageType: "Test.Contracts.Foo",
      messageId: Guid.Parse("33333333-3333-3333-3333-333333333333"));

    await Assert.That(shouldSkip).IsFalse();
  }
}
