// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.SystemEvents;
using Whizbang.Core.Tracing;
using Whizbang.Core.ValueObjects;
using Whizbang.Testing.Options;

namespace Whizbang.Core.Tests.SystemEvents;

/// <summary>An event the call-site hook tests audit; public so its assembly-qualified name resolves.</summary>
public sealed record HookCallSiteProbeEvent([property: StreamId] Guid ProbeId) : IEvent;

/// <summary>
/// The registered <see cref="IAuditDecisionHook"/> runs on the outbox audit path too (#1485). The builder
/// accepted a hook, but every work coordinator strategy called it without one, so a hook that vetoed or
/// named an occurrence changed only the records written by the event store decorator. Each strategy that
/// builds audit records, and the factory that constructs them from the container, is checked here.
/// </summary>
[Category("SystemEvents")]
public class AuditDecisionHookCallSiteTests {
  private const string RENAMED = "Probe activity";

  // ---- Scoped ----

  [Test]
  public async Task Scoped_VetoingHook_SuppressesTheAuditRecordAsync() {
    var coordinator = new CapturingWorkCoordinator();
    var sut = _scoped(coordinator, new FixedHook(AuditDecision.Skip));

    sut.QueueOutboxMessage(_auditedEvent());
    await sut.FlushAsync(WorkBatchOptions.None);
    await sut.DisposeAsync();

    await Assert.That(_audits(coordinator.Stored)).IsEmpty()
      .Because("a hook that vetoes an occurrence must suppress it on the outbox path as well");
  }

  [Test]
  public async Task Scoped_NamingHook_SetsTheActivityNameAsync() {
    var coordinator = new CapturingWorkCoordinator();
    var sut = _scoped(coordinator, new FixedHook(AuditDecision.Record(RENAMED)));

    sut.QueueOutboxMessage(_auditedEvent());
    await sut.FlushAsync(WorkBatchOptions.None);
    await sut.DisposeAsync();

    var audit = _audits(coordinator.Stored).Single();
    await Assert.That(audit.Envelope.Payload.GetProperty("ActivityName").GetString()).IsEqualTo(RENAMED);
  }

  [Test]
  public async Task Scoped_NoOpinionHook_LeavesTheAttributesDecisionAsync() {
    var coordinator = new CapturingWorkCoordinator();
    var sut = _scoped(coordinator, new FixedHook(AuditDecision.NoOpinion));

    sut.QueueOutboxMessage(_auditedEvent());
    await sut.FlushAsync(WorkBatchOptions.None);
    await sut.DisposeAsync();

    await Assert.That(_audits(coordinator.Stored)).Count().IsEqualTo(1)
      .Because("no opinion defers to the attribute, which audits an unmarked event under OptOut");
  }

  // ---- Immediate ----

  [Test]
  public async Task Immediate_VetoingHook_SuppressesTheAuditRecordAsync() {
    var coordinator = new CapturingWorkCoordinator();
    var sut = _immediate(coordinator, new FixedHook(AuditDecision.Skip));

    sut.QueueOutboxMessage(_auditedEvent());
    await sut.FlushAsync(WorkBatchOptions.None);

    await Assert.That(coordinator.Stored).IsNotEmpty();
    await Assert.That(_audits(coordinator.Stored)).IsEmpty();
  }

  [Test]
  public async Task Immediate_NamingHook_SetsTheActivityNameAsync() {
    var coordinator = new CapturingWorkCoordinator();
    var sut = _immediate(coordinator, new FixedHook(AuditDecision.Record(RENAMED)));

    sut.QueueOutboxMessage(_auditedEvent());
    await sut.FlushAsync(WorkBatchOptions.None);

    var audit = _audits(coordinator.Stored).Single();
    await Assert.That(audit.Envelope.Payload.GetProperty("ActivityName").GetString()).IsEqualTo(RENAMED);
  }

  // ---- Stream affinity ----

  [Test]
  public async Task StreamAffinity_VetoingHook_SuppressesTheAuditRecordAsync() {
    var batch = new RecordingBatch();
    var sut = new StreamAffinityWorkCoordinatorStrategy(
      inner: _scoped(new CapturingWorkCoordinator(), hook: null),
      outboxBatch: batch,
      logger: NullLogger.Instance,
      auditDecisionHook: new FixedHook(AuditDecision.Skip),
      systemEventOptions: _auditOptions());

    await sut.QueueOutboxMessageAsync(_auditedEvent());

    await Assert.That(batch.Appended).Count().IsEqualTo(1);
    await Assert.That(_audits(batch.Appended)).IsEmpty();
  }

  [Test]
  public async Task StreamAffinity_NamingHook_SetsTheActivityNameAsync() {
    var batch = new RecordingBatch();
    var sut = new StreamAffinityWorkCoordinatorStrategy(
      inner: _scoped(new CapturingWorkCoordinator(), hook: null),
      outboxBatch: batch,
      logger: NullLogger.Instance,
      auditDecisionHook: new FixedHook(AuditDecision.Record(RENAMED)),
      systemEventOptions: _auditOptions());

    await sut.QueueOutboxMessageAsync(_auditedEvent());

    var audit = _audits(batch.Appended).Single();
    await Assert.That(audit.Envelope.Payload.GetProperty("ActivityName").GetString()).IsEqualTo(RENAMED);
  }

  // ---- Factory wiring: the container's hook reaches every strategy it builds ----

  [Test]
  public async Task Factory_Scoped_PassesTheRegisteredHookAsync() {
    var coordinator = new CapturingWorkCoordinator();
    await using var sp = _container(coordinator, new FixedHook(AuditDecision.Skip)).BuildServiceProvider();

    var strategy = WorkCoordinatorStrategyFactory.Create(WorkCoordinatorStrategy.Scoped, sp);
    await strategy.QueueOutboxMessageAsync(_auditedEvent());
    await strategy.FlushAsync(WorkBatchOptions.None);
    await ((IAsyncDisposable)strategy).DisposeAsync();

    await Assert.That(coordinator.Stored).IsNotEmpty();
    await Assert.That(_audits(coordinator.Stored)).IsEmpty()
      .Because("the factory must hand the registered hook to the strategy, or a hand-built strategy silently ignores it");
  }

  [Test]
  public async Task Factory_Immediate_PassesTheRegisteredHookAsync() {
    var coordinator = new CapturingWorkCoordinator();
    await using var sp = _container(coordinator, new FixedHook(AuditDecision.Skip)).BuildServiceProvider();

    var strategy = WorkCoordinatorStrategyFactory.Create(WorkCoordinatorStrategy.Immediate, sp);
    await strategy.QueueOutboxMessageAsync(_auditedEvent());
    await strategy.FlushAsync(WorkBatchOptions.None);

    await Assert.That(coordinator.Stored).IsNotEmpty();
    await Assert.That(_audits(coordinator.Stored)).IsEmpty();
  }

  [Test]
  public async Task Factory_StreamAffinityWrap_PassesTheRegisteredHookAsync() {
    var batch = new RecordingBatch();
    var services = _container(new CapturingWorkCoordinator(), new FixedHook(AuditDecision.Skip));
    services.AddSingleton<IOutboxBatchStrategy>(batch);
    await using var sp = services.BuildServiceProvider();

    var inner = WorkCoordinatorStrategyFactory.Create(WorkCoordinatorStrategy.Scoped, sp);
    var wrapped = WorkCoordinatorStrategyFactory.WrapWithStreamAffinity(inner, sp);
    await wrapped.QueueOutboxMessageAsync(_auditedEvent());
    await ((IAsyncDisposable)inner).DisposeAsync();

    await Assert.That(batch.Appended).Count().IsEqualTo(1);
    await Assert.That(_audits(batch.Appended)).IsEmpty();
  }

  // ---- helpers ----

  private static SystemEventOptions _auditOptions() => new SystemEventOptions().EnableEventAudit();

  private static List<OutboxMessage> _audits(IEnumerable<OutboxMessage> messages) =>
    [.. messages.Where(m => m.MessageType.Contains(nameof(EventAudited), StringComparison.Ordinal))];

  private static ScopedWorkCoordinatorStrategy _scoped(IWorkCoordinator coordinator, IAuditDecisionHook? hook) =>
    new(
      coordinator: coordinator,
      instanceProvider: new ProbeInstanceProvider(),
      workChannelWriter: null,
      options: new WorkCoordinatorOptions { PartitionCount = 10000, LeaseSeconds = 300, AbandonStaleInstanceThresholdSeconds = 300 },
      logger: NullLogger<ScopedWorkCoordinatorStrategy>.Instance,
      inboxChannelWriter: new InboxChannelWriter(),
      dependencies: new ScopedWorkCoordinatorDependencies {
        SystemEventOptions = _auditOptions(),
        AuditDecisionHook = hook
      });

  private static ImmediateWorkCoordinatorStrategy _immediate(IWorkCoordinator coordinator, IAuditDecisionHook hook) =>
    new(
      coordinator: coordinator,
      instanceProvider: new ProbeInstanceProvider(),
      options: new WorkCoordinatorOptions { PartitionCount = 10000, LeaseSeconds = 300, AbandonStaleInstanceThresholdSeconds = 300 },
      logger: NullLogger<ImmediateWorkCoordinatorStrategy>.Instance,
      scopeFactory: new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
      lifecycleMessageDeserializer: new JsonLifecycleMessageDeserializer(),
      tracingOptions: new StaticOptionsMonitor<TracingOptions>(new TracingOptions()),
      deferredChannel: new DeferredOutboxChannel(),
      systemEventOptions: Options.Create(_auditOptions()),
      workChannelWriter: new WorkChannelWriter(),
      auditDecisionHook: hook);

  private static ServiceCollection _container(IWorkCoordinator coordinator, IAuditDecisionHook hook) {
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddSingleton(new WorkCoordinatorOptions { PartitionCount = 10000, LeaseSeconds = 300, AbandonStaleInstanceThresholdSeconds = 300 });
    services.AddSingleton(coordinator);
    services.AddSingleton<IServiceInstanceProvider, ProbeInstanceProvider>();
    services.AddSingleton(Options.Create(_auditOptions()));
    services.AddSingleton(hook);
    return services;
  }

  private static OutboxMessage _auditedEvent() {
    var messageId = TrackedGuid.New().Value;
    using var document = JsonDocument.Parse("{}");
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.From(messageId),
      Payload = document.RootElement.Clone(),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
    return new OutboxMessage {
      MessageId = messageId,
      Destination = "probe-topic",
      StreamId = TrackedGuid.New().Value,
      Envelope = envelope,
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Text.Json.JsonElement, System.Text.Json]], Whizbang.Core",
      MessageType = typeof(HookCallSiteProbeEvent).AssemblyQualifiedName!,
      Metadata = new EnvelopeMetadata { MessageId = MessageId.From(messageId), Hops = [] },
      IsEvent = true,
    };
  }

  private sealed class FixedHook(AuditDecision decision) : IAuditDecisionHook {
    public AuditDecision Decide(object payload, Type eventType) =>
      eventType == typeof(HookCallSiteProbeEvent) ? decision : AuditDecision.NoOpinion;
  }

  private sealed class RecordingBatch : IOutboxBatchStrategy {
    public List<OutboxMessage> Appended { get; } = [];

    public ValueTask AppendAsync(OutboxMessage message, CancellationToken cancellationToken = default) {
      Appended.Add(message);
      return ValueTask.CompletedTask;
    }

    public Task FlushAndStopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
  }

  private sealed class CapturingWorkCoordinator : IWorkCoordinator {
    public List<OutboxMessage> Stored { get; } = [];

    public Task StoreOutboxMessagesAsync(OutboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) {
      Stored.AddRange(messages);
      return Task.CompletedTask;
    }

    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());

    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) =>
      Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  private sealed class ProbeInstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = TrackedGuid.New().Value;
    public string ServiceName => "probe-service";
    public string HostName => "probe-host";
    public int ProcessId => 1;

    public ServiceInstanceInfo ToInfo() => new() {
      ServiceName = ServiceName,
      InstanceId = InstanceId,
      HostName = HostName,
      ProcessId = ProcessId
    };
  }
}
