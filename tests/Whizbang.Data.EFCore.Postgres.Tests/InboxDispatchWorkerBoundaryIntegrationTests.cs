using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;

#pragma warning disable CA1707 // Test method names use underscores by convention.

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The definitive ambient-absent BOUNDARY proof for the cascade-context-carry fix: drives the REAL
/// <see cref="InboxDispatchWorker"/> against real PostgreSQL. An inbox message arrives carrying identity
/// (CorrelationId + CausationId + tenant/user scope) ONLY on its envelope hop — the worker's poll-loop
/// context has NO ambient AsyncLocal, exactly as in production. The worker establishes context from the hop
/// (<c>SecurityContextHelper.TryEstablishFullContextWithTimeoutAsync</c>) and fires the inbound event's
/// receptor, which <c>PublishAsync</c>-es a child. The child's PERSISTED outbox hop must inherit co + ca +
/// scope from the carried hop — proving the capture-and-carry design survives the boundary the old
/// pull-from-ambient design could not cross.
/// </summary>
/// <remarks>
/// Real PG via Testcontainers (L6). Real Dispatcher + ReceptorInvoker + ILifecycleMessageDeserializer +
/// EFCoreWorkCoordinator + message-security establishment. Only the inbox CHANNEL/lease plumbing is faked
/// (feeding <see cref="InboxWork"/> straight onto <see cref="IInboxChannelWriter"/>, as
/// <c>InboxDispatchWorkerLeaseIntegrationTests</c> does) — the dispatch, establishment and outbox write all
/// run on the worker's real execution-context flow, which is where a synthetic harness could not reproduce
/// establishment persistence.
///
/// RED on 0.832 (outbox hop dropped co/ca); GREEN with #1/#2/#4/#5.
/// The receptor fires at <c>PreInboxInline</c> — inline, BEFORE the worker enqueues the EventStored commit —
/// so awaiting the commit signal deterministically guarantees the child is durable in wh_outbox.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Workers/InboxDispatchWorker.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Dispatcher.cs</code-under-test>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
public class InboxDispatchWorkerBoundaryIntegrationTests : EFCoreTestBase {

  /// <summary>Inbound event consumed by the worker — its hop is the ONLY carrier of identity at the boundary.</summary>
  public record ParentEvent([property: StreamId] Guid Id) : IEvent;

  /// <summary>Child published by the receptor while handling the parent — must inherit the parent hop's identity.</summary>
  public record ChildEvent([property: StreamId] Guid Id) : IEvent;

  /// <summary>
  /// Receptor for the inbound event that PUBLISHES a child — the saga-trigger shape. Pinned to
  /// <c>PreInboxInline</c> so it fires inline (awaited) before the worker enqueues its EventStored commit,
  /// making the child write deterministic. PreInbox is also exempt from the same-service PostInbox skip.
  /// </summary>
  [FireAt(LifecycleStage.PreInboxInline)]
  public sealed class ChildEmittingReceptor(IDispatcher dispatcher) : IReceptor<ParentEvent> {
    public async ValueTask HandleAsync(ParentEvent message, CancellationToken cancellationToken = default) {
      await dispatcher.PublishAsync(new ChildEvent(message.Id));
    }
  }

  /// <summary>Inbound event for the DETACHED-stage variant — the exact production saga-trigger shape.</summary>
  public record DetachedParentEvent([property: StreamId] Guid Id) : IEvent;

  /// <summary>Child published by the detached receptor — must inherit the parent hop's identity.</summary>
  public record DetachedChildEvent([property: StreamId] Guid Id) : IEvent;

  /// <summary>
  /// Default-stage receptor (NO <c>[FireAt]</c>) — registered at <c>PostInboxDetached</c> + <c>LocalImmediateDetached</c>,
  /// so on the inbox path it fires FIRE-AND-FORGET via <c>BackgroundStageDispatch.StartLongRunning</c> (a fresh task).
  /// This is exactly a consumer's saga-trigger-handler shape. Signals a TCS after publishing so the
  /// test can await the fire-and-forget completion deterministically.
  /// </summary>
  public sealed class DetachedChildEmittingReceptor(IDispatcher dispatcher) : IReceptor<DetachedParentEvent> {
    public static TaskCompletionSource Done { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async ValueTask HandleAsync(DetachedParentEvent message, CancellationToken cancellationToken = default) {
      try {
        await dispatcher.PublishAsync(new DetachedChildEvent(message.Id));
      } finally {
        Done.TrySetResult();
      }
    }
  }

  /// <summary>Inbound event for the COLLECTIVE variant (#6) — a consumer's overlay-activation saga-trigger shape.</summary>
  public record CollectiveParentEvent([property: StreamId] Guid Id) : IEvent;

  /// <summary>
  /// A real collective event (produced via ordinary PublishAsync + EventFlags.Collective). Its cohort Scope is
  /// producer-set (the WHERE predicate); its STORE hop's co+ca+scope must be inherited from the carried inbound
  /// hop the same as any event — that's the lineage identity a consumer currently works around with a manual attach.
  /// </summary>
  public sealed record BoundaryCollectiveChildEvent : CollectiveEventBase;

  /// <summary>
  /// Detached-stage receptor (the saga-trigger shape) that imperatively PUBLISHES a collective event — exactly
  /// like a consumer's saga-trigger handler emitting an overlay-applied collective event.
  /// </summary>
  public sealed class CollectiveEmittingReceptor(IDispatcher dispatcher) : IReceptor<CollectiveParentEvent> {
    public static TaskCompletionSource Done { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async ValueTask HandleAsync(CollectiveParentEvent message, CancellationToken cancellationToken = default) {
      try {
        await dispatcher.PublishAsync(new BoundaryCollectiveChildEvent { Scope = new TenantCollectiveScope("tenant-456") });
      } finally {
        Done.TrySetResult();
      }
    }
  }

  [Test]
  public async Task InboxWorker_Boundary_NoAmbient_ChildInheritsIdentityFromHopAsync() {
    // Arrange — real services over real PG + the real InboxDispatchWorker.
    var (serviceProvider, jsonOptions) = await _createServicesAsync();

    var expectedCorrelation = CorrelationId.New();
    var causation = MessageId.New();
    var streamId = (Guid)TrackedGuid.NewMedo();
    var messageId = (Guid)TrackedGuid.NewMedo();
    var parent = new ParentEvent(streamId);

    // The inbound envelope: identity + scope ride ONLY the Current hop. The ServiceName is a FOREIGN service
    // (so any same-service skip never applies), mimicking a message transported in from another service.
    var hop = new MessageHop {
      Type = HopType.Current,
      Timestamp = DateTimeOffset.UtcNow,
      ServiceInstance = new ServiceInstanceInfo {
        InstanceId = Guid.CreateVersion7(),
        ServiceName = "upstream-service",
        HostName = "upstream-host",
        ProcessId = 1
      },
      CorrelationId = expectedCorrelation,
      CausationId = causation,
      Scope = ScopeDelta.FromSecurityContext(new SecurityContext { TenantId = "tenant-456", UserId = "user-123" })
    };
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.From(messageId),
      Payload = JsonSerializer.SerializeToElement(parent, jsonOptions),
      Hops = [hop],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Inbox }
    };
    var work = new InboxWork {
      MessageId = messageId,
      Envelope = envelope,
      MessageType = typeof(ParentEvent).AssemblyQualifiedName!,
      StreamId = streamId,
      PartitionNumber = 0,
      Attempts = 1,
      Status = MessageProcessingStatus.Stored,
      Flags = WorkBatchOptions.None
    };

    var inbox = new FakeInboxChannelWriter();
    var handlerCommit = new FakeHandlerCommitChannel();
    var failure = new FakeFailureChannel();
    var gate = new SchemaReadyGate();
    gate.MarkReady();

    // Boundary invariant: the worker's poll loop never establishes an ambient context. Clear both AsyncLocals
    // so the ONLY identity the worker can carry is what it reads off the inbound hop.
    ScopeContextAccessor.CurrentInitiatingContext = null;
    ScopeContextAccessor.CurrentContext = null;

    var worker = new InboxDispatchWorker(
      serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      serviceProvider.GetRequiredService<IServiceInstanceProvider>(),
      inbox,
      handlerCommit,
      failure,
      gate,
      Options.Create(new InboxDispatchWorkerOptions()),
      Options.Create(new WorkCoordinatorOptions()),
      NullLogger<InboxDispatchWorker>.Instance,
      lifecycleMessageDeserializer: serviceProvider.GetRequiredService<ILifecycleMessageDeserializer>(),
      leaseHandleOptions: Options.Create(new LeaseHandleOptions { LeaseGraceSeconds = 30, MaxRenewalsPerWork = 6 }),
      leaseRenewalOptions: Options.Create(new LeaseRenewalWorkerOptions { LeaseSeconds = 60 }),
      leaseRegistry: new LeaseRegistry());

    using var cts = new CancellationTokenSource();
    try {
      // Act — start the real worker, feed one inbox message, wait for the (post-PreInbox) commit signal.
      await worker.StartAsync(cts.Token);
      await inbox.WriteAsync(work, cts.Token);
      await handlerCommit.First.Task.WaitAsync(TimeSpan.FromSeconds(30));

      // Assert — the child the receptor published carries identity inherited from the inbound hop.
      await using var dbContext = CreateDbContext();
      var outboxMessages = await dbContext.Outbox.ToListAsync();
      var expectedType = typeof(ChildEvent).AssemblyQualifiedName;
      var child = outboxMessages.FirstOrDefault(m => m.MessageType == expectedType);
      await Assert.That(child).IsNotNull()
        .Because("The receptor's PublishAsync'd child must be durable in the outbox after the inbox worker dispatched the parent.");

      var childEnvelope = new MessageEnvelope<JsonElement> {
        MessageId = child!.MessageData.MessageId,
        Payload = child.MessageData.Payload,
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
        Hops = child.MessageData.Hops
      };
      await Assert.That(childEnvelope.GetCorrelationId()).IsEqualTo(expectedCorrelation)
        .Because("At the worker boundary (no ambient), the child must inherit correlation from the CARRIED inbound hop, not fabricate a fresh root (the production signature).");
      await Assert.That(childEnvelope.GetCausationId()).IsNotNull()
        .Because("Causation must survive the boundary on the carried hop, not be left ca=None.");
      await Assert.That(childEnvelope.GetCurrentScope()?.Scope?.TenantId).IsEqualTo("tenant-456")
        .Because("Tenant scope must survive the boundary on the carried hop, with no manual attach.");
    } finally {
      await cts.CancelAsync();
      await worker.StopAsync(CancellationToken.None);
      ScopeContextAccessor.CurrentInitiatingContext = null;
      ScopeContextAccessor.CurrentContext = null;
      await serviceProvider.DisposeAsync();
    }
  }

  [Test]
  public async Task InboxWorker_DetachedStage_NoAmbient_ChildInheritsIdentityFromHopAsync() {
    // The PRODUCTION cell: a default-stage receptor fires FIRE-AND-FORGET at PostInboxDetached via
    // BackgroundStageDispatch.StartLongRunning (a fresh task, ambient absent), then PublishAsync-es a child.
    // Even through that detached hop, the child must inherit co + ca + scope from the carried inbound hop —
    // proving the establish-side fix reaches receptors on the detached-Task.Run path too (not just inline).
    DetachedChildEmittingReceptor.Done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var (serviceProvider, jsonOptions) = await _createServicesAsync();

    var expectedCorrelation = CorrelationId.New();
    var streamId = (Guid)TrackedGuid.NewMedo();
    var messageId = (Guid)TrackedGuid.NewMedo();
    var parent = new DetachedParentEvent(streamId);

    var hop = new MessageHop {
      Type = HopType.Current,
      Timestamp = DateTimeOffset.UtcNow,
      ServiceInstance = new ServiceInstanceInfo {
        InstanceId = Guid.CreateVersion7(),
        ServiceName = "upstream-service",
        HostName = "upstream-host",
        ProcessId = 1
      },
      CorrelationId = expectedCorrelation,
      CausationId = MessageId.New(),
      Scope = ScopeDelta.FromSecurityContext(new SecurityContext { TenantId = "tenant-456", UserId = "user-123" })
    };
    var work = new InboxWork {
      MessageId = messageId,
      Envelope = new MessageEnvelope<JsonElement> {
        MessageId = MessageId.From(messageId),
        Payload = JsonSerializer.SerializeToElement(parent, jsonOptions),
        Hops = [hop],
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Inbox }
      },
      MessageType = typeof(DetachedParentEvent).AssemblyQualifiedName!,
      StreamId = streamId,
      PartitionNumber = 0,
      Attempts = 1,
      Status = MessageProcessingStatus.Stored,
      Flags = WorkBatchOptions.None
    };

    var inbox = new FakeInboxChannelWriter();
    var handlerCommit = new FakeHandlerCommitChannel();
    var failure = new FakeFailureChannel();
    var gate = new SchemaReadyGate();
    gate.MarkReady();

    ScopeContextAccessor.CurrentInitiatingContext = null;
    ScopeContextAccessor.CurrentContext = null;

    var worker = new InboxDispatchWorker(
      serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      serviceProvider.GetRequiredService<IServiceInstanceProvider>(),
      inbox, handlerCommit, failure, gate,
      Options.Create(new InboxDispatchWorkerOptions()),
      Options.Create(new WorkCoordinatorOptions()),
      NullLogger<InboxDispatchWorker>.Instance,
      lifecycleMessageDeserializer: serviceProvider.GetRequiredService<ILifecycleMessageDeserializer>(),
      leaseHandleOptions: Options.Create(new LeaseHandleOptions { LeaseGraceSeconds = 30, MaxRenewalsPerWork = 6 }),
      leaseRenewalOptions: Options.Create(new LeaseRenewalWorkerOptions { LeaseSeconds = 60 }),
      leaseRegistry: new LeaseRegistry());

    using var cts = new CancellationTokenSource();
    try {
      await worker.StartAsync(cts.Token);
      await inbox.WriteAsync(work, cts.Token);
      // The detached receptor fires fire-and-forget AFTER the commit — await its own completion signal.
      await DetachedChildEmittingReceptor.Done.Task.WaitAsync(TimeSpan.FromSeconds(30));

      await using var dbContext = CreateDbContext();
      var outboxMessages = await dbContext.Outbox.ToListAsync();
      var child = outboxMessages.FirstOrDefault(m => m.MessageType == typeof(DetachedChildEvent).AssemblyQualifiedName);
      await Assert.That(child).IsNotNull()
        .Because("The detached receptor's PublishAsync'd child must be durable in the outbox.");

      var childEnvelope = new MessageEnvelope<JsonElement> {
        MessageId = child!.MessageData.MessageId,
        Payload = child.MessageData.Payload,
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
        Hops = child.MessageData.Hops
      };
      await Assert.That(childEnvelope.GetCorrelationId()).IsEqualTo(expectedCorrelation)
        .Because("Through the detached Task.Run hop (ambient absent), the child must inherit correlation from the carried inbound hop, not fabricate a fresh root.");
      await Assert.That(childEnvelope.GetCausationId()).IsNotNull()
        .Because("Causation must survive the detached boundary on the carried hop.");
      await Assert.That(childEnvelope.GetCurrentScope()?.Scope?.TenantId).IsEqualTo("tenant-456")
        .Because("Tenant scope must survive the detached boundary on the carried hop.");
    } finally {
      await cts.CancelAsync();
      await worker.StopAsync(CancellationToken.None);
      ScopeContextAccessor.CurrentInitiatingContext = null;
      ScopeContextAccessor.CurrentContext = null;
      await serviceProvider.DisposeAsync();
    }
  }

  [Test]
  public async Task InboxWorker_DetachedStage_CollectiveChild_InheritsIdentityFromHopAsync() {
    // #6: a collective event is produced via ORDINARY PublishAsync (+ EventFlags.Collective) — there is no special
    // collective-produce path. Emitted imperatively from a detached saga-trigger receptor at the worker boundary
    // (a consumer's overlay-applied shape), its persisted STORE hop must inherit co+ca+scope from the carried inbound
    // hop, same as any event. Proves #6's produce-side is covered by the #1/#2/#7 carry — so a consumer's manual
    // lineage-scope attach for the collective event is no longer needed.
    CollectiveEmittingReceptor.Done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var (serviceProvider, jsonOptions) = await _createServicesAsync();

    var expectedCorrelation = CorrelationId.New();
    var streamId = (Guid)TrackedGuid.NewMedo();
    var messageId = (Guid)TrackedGuid.NewMedo();
    var parent = new CollectiveParentEvent(streamId);

    var hop = new MessageHop {
      Type = HopType.Current,
      Timestamp = DateTimeOffset.UtcNow,
      ServiceInstance = new ServiceInstanceInfo {
        InstanceId = Guid.CreateVersion7(),
        ServiceName = "upstream-service",
        HostName = "upstream-host",
        ProcessId = 1
      },
      CorrelationId = expectedCorrelation,
      CausationId = MessageId.New(),
      Scope = ScopeDelta.FromSecurityContext(new SecurityContext { TenantId = "tenant-456", UserId = "user-123" })
    };
    var work = new InboxWork {
      MessageId = messageId,
      Envelope = new MessageEnvelope<JsonElement> {
        MessageId = MessageId.From(messageId),
        Payload = JsonSerializer.SerializeToElement(parent, jsonOptions),
        Hops = [hop],
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Inbox }
      },
      MessageType = typeof(CollectiveParentEvent).AssemblyQualifiedName!,
      StreamId = streamId,
      PartitionNumber = 0,
      Attempts = 1,
      Status = MessageProcessingStatus.Stored,
      Flags = WorkBatchOptions.None
    };

    var inbox = new FakeInboxChannelWriter();
    var handlerCommit = new FakeHandlerCommitChannel();
    var failure = new FakeFailureChannel();
    var gate = new SchemaReadyGate();
    gate.MarkReady();

    ScopeContextAccessor.CurrentInitiatingContext = null;
    ScopeContextAccessor.CurrentContext = null;

    var worker = new InboxDispatchWorker(
      serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      serviceProvider.GetRequiredService<IServiceInstanceProvider>(),
      inbox, handlerCommit, failure, gate,
      Options.Create(new InboxDispatchWorkerOptions()),
      Options.Create(new WorkCoordinatorOptions()),
      NullLogger<InboxDispatchWorker>.Instance,
      lifecycleMessageDeserializer: serviceProvider.GetRequiredService<ILifecycleMessageDeserializer>(),
      leaseHandleOptions: Options.Create(new LeaseHandleOptions { LeaseGraceSeconds = 30, MaxRenewalsPerWork = 6 }),
      leaseRenewalOptions: Options.Create(new LeaseRenewalWorkerOptions { LeaseSeconds = 60 }),
      leaseRegistry: new LeaseRegistry());

    using var cts = new CancellationTokenSource();
    try {
      await worker.StartAsync(cts.Token);
      await inbox.WriteAsync(work, cts.Token);
      await CollectiveEmittingReceptor.Done.Task.WaitAsync(TimeSpan.FromSeconds(30));

      await using var dbContext = CreateDbContext();
      var outboxMessages = await dbContext.Outbox.ToListAsync();
      var child = outboxMessages.FirstOrDefault(m => m.MessageType == typeof(BoundaryCollectiveChildEvent).AssemblyQualifiedName);
      await Assert.That(child).IsNotNull()
        .Because("The detached receptor's PublishAsync'd collective event must be durable in the outbox.");

      var childEnvelope = new MessageEnvelope<JsonElement> {
        MessageId = child!.MessageData.MessageId,
        Payload = child.MessageData.Payload,
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
        Hops = child.MessageData.Hops
      };
      await Assert.That(childEnvelope.GetCorrelationId()).IsEqualTo(expectedCorrelation)
        .Because("A collective event emitted at the worker boundary must inherit correlation from the carried hop (the lineage bug a consumer worked around by fabricating fresh).");
      await Assert.That(childEnvelope.GetCausationId()).IsNotNull()
        .Because("Causation must survive onto the collective event's store hop.");
      await Assert.That(childEnvelope.GetCurrentScope()?.Scope?.TenantId).IsEqualTo("tenant-456")
        .Because("The collective event's STORE-hop scope must inherit the tenant from the carried hop — no manual attach — so a consumer can drop its ForCurrentTenant lineage workaround.");
    } finally {
      await cts.CancelAsync();
      await worker.StopAsync(CancellationToken.None);
      ScopeContextAccessor.CurrentInitiatingContext = null;
      ScopeContextAccessor.CurrentContext = null;
      await serviceProvider.DisposeAsync();
    }
  }

  private async Task<(ServiceProvider Provider, JsonSerializerOptions JsonOptions)> _createServicesAsync() {
    await base.SetupAsync();

    var services = new ServiceCollection();
    services.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(configuration: null));
    services.AddScoped(_ => CreateDbContext());

    var jsonOptions = JsonContextRegistry.CreateCombinedOptions();
    services.AddSingleton(jsonOptions);
    services.AddSingleton<IEnvelopeSerializer, EnvelopeSerializer>();
    services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));

    services.AddScoped<IWorkCoordinator>(sp =>
      new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
        sp.GetRequiredService<WorkCoordinationDbContext>(), jsonOptions));

    services.AddScoped<IWorkCoordinatorStrategy>(sp =>
      new ScopedWorkCoordinatorStrategy(
        sp.GetRequiredService<IWorkCoordinator>(),
        sp.GetRequiredService<IServiceInstanceProvider>(),
        workChannelWriter: null,
        new WorkCoordinatorOptions { LeaseSeconds = 30, AbandonStaleInstanceThresholdSeconds = 300, PartitionCount = 4 },
        sp.GetService<ILogger<ScopedWorkCoordinatorStrategy>>()));

    services.AddReceptors();
    services.AddWhizbangDispatcher();                  // IDispatcher, IReceptorInvoker
    services.AddWhizbangLifecycleMessageDeserializer(); // JsonLifecycleMessageDeserializer (deserializes the inbox payload)
    services.AddWhizbangMessageSecurity();             // establishment of co/ca/scope from the hop
    services.AddSingleton<IScopeContextAccessor, ScopeContextAccessor>();

    return (services.BuildServiceProvider(), jsonOptions);
  }

  // ============================================================
  // Inbox channel/lease fakes — feed InboxWork + capture the commit signal (real PG for the outbox write).
  // ============================================================

  private sealed class FakeInboxChannelWriter : IInboxChannelWriter {
    // Not exercised by this fake: it tracks no in-flight work, so there is nothing to gate on.
    public int InFlightCount => 0;
    public int PruneInFlightOlderThan(TimeSpan age) => 0;
    private readonly Channel<InboxWork> _channel = Channel.CreateUnbounded<InboxWork>();
    public ChannelReader<InboxWork> Reader => _channel.Reader;
    public ValueTask WriteAsync(InboxWork work, CancellationToken ct = default) => _channel.Writer.WriteAsync(work, ct);
    public bool TryWrite(InboxWork work) => _channel.Writer.TryWrite(work);
    public bool IsInFlight(Guid messageId) => false;
    public void RemoveInFlight(Guid messageId) { }
    public bool ShouldRenewLease(Guid messageId) => false;
    public void Complete() => _channel.Writer.Complete();
    public event Action? OnNewInboxWorkAvailable;
    public void SignalNewInboxWorkAvailable() => OnNewInboxWorkAvailable?.Invoke();
  }

  private sealed class FakeHandlerCommitChannel : IInboxHandlerCommitChannel {
    public TaskCompletionSource<HandlerCommitRequest> First { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ValueTask EnqueueAsync(HandlerCommitRequest request, CancellationToken ct = default) {
      First.TrySetResult(request);
      return ValueTask.CompletedTask;
    }
  }

  private sealed class FakeFailureChannel : IFailureChannel {
    public ConcurrentBag<MessageFailure> All { get; } = [];
    public ValueTask EnqueueAsync(WorkCategory category, MessageFailure failure, CancellationToken ct = default) {
      All.Add(failure);
      return ValueTask.CompletedTask;
    }
  }
}
