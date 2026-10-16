// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

#pragma warning disable CA1707 // Test method names use underscores by convention

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Integration.Tests.Generated;
using Whizbang.Core.Integration.Tests.SendShapes;
using Whizbang.Core.Integration.Tests.SendShapes.Elsewhere;
using Whizbang.Core.Integration.Tests.SendShapes.Owned;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Routing;

namespace Whizbang.Core.Integration.Tests;

/// <summary>
/// SendAsync against the REAL generated dispatcher, with the service's owned domains configured
/// the way a deployed service configures them (#1379). The unit tests substitute the generated
/// lookup tables; these prove the generator actually hands a void or sync receptor to SendAsync.
/// Before the fix, an owned command handled by a receptor that returns no result was answered
/// with Accepted and never processed, and an unowned one went through the outbox.
/// </summary>
/// <docs>fundamentals/dispatcher/routing#owned-domain-routing</docs>
[Category("Integration")]
public class SendReceptorShapeIntegrationTests {
  private const string OWNED_DOMAIN = "Whizbang.Core.Integration.Tests.SendShapes.Owned";

  public enum SendOverload { Untyped, UntypedWithOptions, Typed, TypedWithOptions }

  // ========================================
  // Every receptor shape, every SendAsync entry point
  // ========================================

  [Test]
  [Arguments(SendOverload.Untyped)]
  [Arguments(SendOverload.UntypedWithOptions)]
  [Arguments(SendOverload.Typed)]
  [Arguments(SendOverload.TypedWithOptions)]
  public async Task SendAsync_OwnedVoidReceptor_RunsOnceInProcessAsync(SendOverload overload) {
    var outbox = new RecordingOutbox();
    var dispatcher = _buildDispatcher(outbox);
    var command = new OwnedVoidCommand(Guid.CreateVersion7());

    var receipt = await _sendAsync(dispatcher, command, overload);

    await Assert.That(SendShapeInvocations.CountFor(command.Id)).IsEqualTo(1)
      .Because("the generated void lookup must reach SendAsync; before #1379 the command was dropped");
    await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Delivered);
    await Assert.That(outbox.QueuedOf(command)).IsEmpty();
  }

  [Test]
  [Arguments(SendOverload.Untyped)]
  [Arguments(SendOverload.UntypedWithOptions)]
  [Arguments(SendOverload.Typed)]
  [Arguments(SendOverload.TypedWithOptions)]
  public async Task SendAsync_OwnedSyncReceptor_RunsOnceInProcessAsync(SendOverload overload) {
    var outbox = new RecordingOutbox();
    var dispatcher = _buildDispatcher(outbox);
    var command = new OwnedSyncCommand(Guid.CreateVersion7());

    var receipt = await _sendAsync(dispatcher, command, overload);

    await Assert.That(SendShapeInvocations.CountFor(command.Id)).IsEqualTo(1);
    await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Delivered);
    await Assert.That(outbox.QueuedOf(command)).IsEmpty();
  }

  [Test]
  [Arguments(SendOverload.Untyped)]
  [Arguments(SendOverload.UntypedWithOptions)]
  [Arguments(SendOverload.Typed)]
  [Arguments(SendOverload.TypedWithOptions)]
  public async Task SendAsync_OwnedVoidSyncReceptor_RunsOnceInProcessAsync(SendOverload overload) {
    var outbox = new RecordingOutbox();
    var dispatcher = _buildDispatcher(outbox);
    var command = new OwnedVoidSyncCommand(Guid.CreateVersion7());

    var receipt = await _sendAsync(dispatcher, command, overload);

    await Assert.That(SendShapeInvocations.CountFor(command.Id)).IsEqualTo(1);
    await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Delivered);
    await Assert.That(outbox.QueuedOf(command)).IsEmpty();
  }

  [Test]
  [Arguments(SendOverload.Untyped)]
  [Arguments(SendOverload.UntypedWithOptions)]
  [Arguments(SendOverload.Typed)]
  [Arguments(SendOverload.TypedWithOptions)]
  public async Task SendAsync_UnownedVoidReceptor_RunsInProcessInsteadOfTheOutboxAsync(SendOverload overload) {
    // A local receptor wins over the outbox whether or not the namespace is owned
    var outbox = new RecordingOutbox();
    var dispatcher = _buildDispatcher(outbox);
    var command = new UnownedVoidCommand(Guid.CreateVersion7());

    var receipt = await _sendAsync(dispatcher, command, overload);

    await Assert.That(SendShapeInvocations.CountFor(command.Id)).IsEqualTo(1);
    await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Delivered);
    await Assert.That(outbox.QueuedOf(command)).IsEmpty()
      .Because("before #1379 an unowned void-receptor command took an outbox round trip");
  }

  [Test]
  [Arguments(SendOverload.Untyped)]
  [Arguments(SendOverload.Typed)]
  public async Task SendAsync_OwnedCommandWithNoReceptor_ThrowsAndWritesNothingAsync(SendOverload overload) {
    var outbox = new RecordingOutbox();
    var dispatcher = _buildDispatcher(outbox);
    var command = new OwnedUnhandledCommand(Guid.CreateVersion7());

    await Assert.That(async () => await _sendAsync(dispatcher, command, overload))
      .ThrowsExactly<ReceptorNotFoundException>();
    await Assert.That(outbox.QueuedOf(command)).IsEmpty();
  }

  [Test]
  [Arguments(SendOverload.UntypedWithOptions)]
  [Arguments(SendOverload.TypedWithOptions)]
  public async Task SendAsync_WithOptions_OwnedCommandWithNoReceptor_StillRoutesToTheOutboxAsync(SendOverload overload) {
    // Records the deliberate scope of #1379: the overloads taking DispatchOptions never had the
    // owned-domain short-circuit and keep routing an unhandled owned command to the outbox.
    var outbox = new RecordingOutbox();
    var dispatcher = _buildDispatcher(outbox);
    var command = new OwnedUnhandledCommand(Guid.CreateVersion7());

    var receipt = await _sendAsync(dispatcher, command, overload);

    await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Accepted);
    await Assert.That(outbox.QueuedOf(command)).Count().IsEqualTo(1);
  }

  // ========================================
  // Code a void receptor now reaches through SendAsync
  // ========================================

  [Test]
  [Arguments(SendOverload.Untyped)]
  [Arguments(SendOverload.UntypedWithOptions)]
  [Arguments(SendOverload.Typed)]
  [Arguments(SendOverload.TypedWithOptions)]
  public async Task SendAsync_VoidReceptorThrows_ExceptionReachesTheCallerAsync(SendOverload overload) {
    var outbox = new RecordingOutbox();
    var dispatcher = _buildDispatcher(outbox);
    var command = new OwnedThrowingVoidCommand(Guid.CreateVersion7());

    await Assert.That(async () => await _sendAsync(dispatcher, command, overload))
      .ThrowsExactly<InvalidOperationException>();
    await Assert.That(SendShapeInvocations.CountFor(command.Id)).IsEqualTo(1);
    await Assert.That(outbox.QueuedOf(command)).IsEmpty()
      .Because("a failed local receptor must not fall back to the outbox");
  }

  [Test]
  [Arguments(SendOverload.Untyped)]
  [Arguments(SendOverload.UntypedWithOptions)]
  [Arguments(SendOverload.Typed)]
  [Arguments(SendOverload.TypedWithOptions)]
  public async Task SendAsync_VoidReceptor_FiresImmediateDetachedReceptorsAsync(SendOverload overload) {
    var outbox = new RecordingOutbox();
    var provider = _buildProvider(outbox);
    var dispatcher = provider.GetRequiredService<IDispatcher>();
    var registry = provider.GetRequiredService<IReceptorRegistry>();
    var stageTracker = new StageTracker<OwnedVoidCommand>(static c => c.Id);
    registry.Register<OwnedVoidCommand>(stageTracker, LifecycleStage.ImmediateDetached);
    var command = new OwnedVoidCommand(Guid.CreateVersion7());

    try {
      await _sendAsync(dispatcher, command, overload);
    } finally {
      registry.Unregister<OwnedVoidCommand>(stageTracker, LifecycleStage.ImmediateDetached);
    }

    await Assert.That(stageTracker.FiredFor).IsEquivalentTo([command.Id])
      .Because("ImmediateDetached runs after the business receptor, which SendAsync now finds");
  }

  // ========================================
  // A lifecycle hook is not the command's handler
  // ========================================

  [Test]
  [Arguments(SendOverload.Untyped)]
  [Arguments(SendOverload.UntypedWithOptions)]
  [Arguments(SendOverload.Typed)]
  [Arguments(SendOverload.TypedWithOptions)]
  public async Task SendAsync_CommandWhoseOnlyVoidReceptorIsALifecycleHook_RoutesToTheOutboxAsync(SendOverload overload) {
    // The hook fires at PostOutboxDetached for a command another service handles. Treating it as
    // the business receptor would run the hook in place of delivering the command to its owner.
    var outbox = new RecordingOutbox();
    var dispatcher = _buildDispatcher(outbox);
    var command = new ObservedOutgoingCommand(Guid.CreateVersion7());

    var receipt = await _sendAsync(dispatcher, command, overload);

    await Assert.That(outbox.QueuedOf(command)).Count().IsEqualTo(1);
    await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Accepted);
    await Assert.That(SendShapeInvocations.CountFor(command.Id)).IsEqualTo(0)
      .Because("a [FireAt] lifecycle hook must not run as the command's handler");
  }

  [Test]
  [Arguments(SendOverload.Untyped)]
  [Arguments(SendOverload.UntypedWithOptions)]
  [Arguments(SendOverload.Typed)]
  [Arguments(SendOverload.TypedWithOptions)]
  public async Task SendAsync_CommandWhoseOnlyResultReceptorIsALifecycleHook_RoutesToTheOutboxAsync(SendOverload overload) {
    var outbox = new RecordingOutbox();
    var dispatcher = _buildDispatcher(outbox);
    var command = new ObservedOutgoingResultCommand(Guid.CreateVersion7());

    var receipt = await _sendAsync(dispatcher, command, overload);

    await Assert.That(outbox.QueuedOf(command)).Count().IsEqualTo(1);
    await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Accepted);
    await Assert.That(SendShapeInvocations.CountFor(command.Id)).IsEqualTo(0)
      .Because("a [FireAt] lifecycle hook must not run as the command's handler");
  }

  [Test]
  [Arguments(SendOverload.Untyped, true)]
  [Arguments(SendOverload.UntypedWithOptions, true)]
  [Arguments(SendOverload.Typed, true)]
  [Arguments(SendOverload.TypedWithOptions, true)]
  [Arguments(SendOverload.Untyped, false)]
  [Arguments(SendOverload.UntypedWithOptions, false)]
  [Arguments(SendOverload.Typed, false)]
  [Arguments(SendOverload.TypedWithOptions, false)]
  public async Task SendAsync_ReceptorDeclaredAtALocalDispatchStage_IsTheHandlerAsync(SendOverload overload, bool inline) {
    // [FireAt(LocalImmediateInline/Detached)] names the stage local dispatch fires: the receptor is
    // the command's handler, unlike a hook at any other stage
    var outbox = new RecordingOutbox();
    var dispatcher = _buildDispatcher(outbox);
    object command = inline ? new OwnedLocalInlineCommand(Guid.CreateVersion7()) : new OwnedLocalDetachedCommand(Guid.CreateVersion7());
    var id = inline ? ((OwnedLocalInlineCommand)command).Id : ((OwnedLocalDetachedCommand)command).Id;

    var receipt = overload switch {
      SendOverload.Untyped => await dispatcher.SendAsync(command, MessageContext.New()),
      SendOverload.UntypedWithOptions => await dispatcher.SendAsync(command, MessageContext.New(), new DispatchOptions()),
      SendOverload.Typed => inline ? await dispatcher.SendAsync((OwnedLocalInlineCommand)command) : await dispatcher.SendAsync((OwnedLocalDetachedCommand)command),
      _ => inline ? await dispatcher.SendAsync((OwnedLocalInlineCommand)command, new DispatchOptions()) : await dispatcher.SendAsync((OwnedLocalDetachedCommand)command, new DispatchOptions())
    };

    await Assert.That(SendShapeInvocations.CountFor(id)).IsEqualTo(1);
    await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Delivered);
    await Assert.That(outbox.QueuedOf(command)).IsEmpty();
  }

  [Test]
  [Arguments(true)]
  [Arguments(false)]
  public async Task LocalInvokeAsync_ReceptorDeclaredAtALocalDispatchStage_IsTheHandlerAsync(bool inline) {
    var outbox = new RecordingOutbox();
    var dispatcher = _buildDispatcher(outbox);
    var id = Guid.CreateVersion7();

    if (inline) {
      await dispatcher.LocalInvokeAsync(new OwnedLocalInlineCommand(id));
    } else {
      await dispatcher.LocalInvokeAsync(new OwnedLocalDetachedCommand(id));
    }

    await Assert.That(SendShapeInvocations.CountFor(id)).IsEqualTo(1);
  }

  // ========================================
  // SendManyAsync
  // ========================================

  [Test]
  [Arguments(false)]
  [Arguments(true)]
  public async Task SendManyAsync_OwnedVoidReceptor_RunsOnceAndStaysOutOfTheOutboxAsync(bool typed) {
    // An owned command is handled by this service. Publishing a second copy only to discard it on
    // receipt costs a round trip, and when the service name is unknown the receive-side echo guard
    // fails open and runs the receptor again.
    var outbox = new RecordingOutbox();
    var dispatcher = _buildDispatcher(outbox);
    var command = new OwnedVoidCommand(Guid.CreateVersion7());

    var receipts = typed
      ? await dispatcher.SendManyAsync<OwnedVoidCommand>([command])
      : await dispatcher.SendManyAsync(new object[] { command });

    await Assert.That(SendShapeInvocations.CountFor(command.Id)).IsEqualTo(1);
    await Assert.That(receipts.Single().Status).IsEqualTo(DeliveryStatus.Delivered);
    await Assert.That(outbox.QueuedOf(command)).IsEmpty();
  }

  [Test]
  [Arguments(false)]
  [Arguments(true)]
  public async Task SendManyAsync_OwnedCommandWithNoReceptor_ThrowsAndWritesNothingAsync(bool typed) {
    // Same contract as SendAsync: an owned command nobody handles is a wiring error
    var outbox = new RecordingOutbox();
    var dispatcher = _buildDispatcher(outbox);
    var command = new OwnedUnhandledCommand(Guid.CreateVersion7());

    await Assert.That(async () => {
      _ = typed
        ? await dispatcher.SendManyAsync<OwnedUnhandledCommand>([command])
        : await dispatcher.SendManyAsync(new object[] { command });
    }).ThrowsExactly<ReceptorNotFoundException>();
    await Assert.That(outbox.QueuedOf(command)).IsEmpty();
  }

  // ========================================
  // AsSystem().ForTenant(t).SendAsync
  // ========================================

  [Test]
  [Arguments("plain")]
  [Arguments("context")]
  [Arguments("options")]
  public async Task AsSystemForTenant_SendAsync_OwnedVoidReceptor_RunsOnceInProcessAsync(string overload) {
    var outbox = new RecordingOutbox();
    var dispatcher = _buildDispatcher(outbox);
    var command = new OwnedVoidCommand(Guid.CreateVersion7());
    var asTenant = dispatcher.AsSystem().ForTenant("tenant-send-shapes");

    var receipt = overload switch {
      "plain" => await asTenant.SendAsync(command),
      "context" => await asTenant.SendAsync(command, MessageContext.New()),
      _ => await asTenant.SendAsync(command, new DispatchOptions())
    };

    await Assert.That(SendShapeInvocations.CountFor(command.Id)).IsEqualTo(1);
    await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Delivered);
    await Assert.That(outbox.QueuedOf(command)).IsEmpty();
  }

  [Test]
  public async Task AsSystemForTenant_SendAsync_OwnedCommandWithNoReceptor_ThrowsAsync() {
    var outbox = new RecordingOutbox();
    var dispatcher = _buildDispatcher(outbox);
    var command = new OwnedUnhandledCommand(Guid.CreateVersion7());

    await Assert.That(async () => await dispatcher.AsSystem().ForTenant("tenant-send-shapes").SendAsync(command))
      .ThrowsExactly<ReceptorNotFoundException>();
    await Assert.That(outbox.QueuedOf(command)).IsEmpty();
  }

  // ========================================
  // Helpers
  // ========================================

  private static Task<IDeliveryReceipt> _sendAsync<TCommand>(IDispatcher dispatcher, TCommand command, SendOverload overload)
    where TCommand : notnull =>
    overload switch {
      SendOverload.Untyped => dispatcher.SendAsync((object)command, MessageContext.New()),
      SendOverload.UntypedWithOptions => dispatcher.SendAsync((object)command, MessageContext.New(), new DispatchOptions()),
      SendOverload.Typed => dispatcher.SendAsync(command),
      SendOverload.TypedWithOptions => dispatcher.SendAsync(command, new DispatchOptions()),
      _ => throw new ArgumentOutOfRangeException(nameof(overload))
    };

  private static IDispatcher _buildDispatcher(RecordingOutbox outbox) =>
    _buildProvider(outbox).GetRequiredService<IDispatcher>();

  private static ServiceProvider _buildProvider(RecordingOutbox outbox) {
    var services = new ServiceCollection();
    services.AddSingleton<IServiceInstanceProvider>(
      new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()));
    services.AddScoped<IWorkCoordinatorStrategy>(_ => outbox);
    services.AddSingleton<IEnvelopeSerializer, TypeRecordingEnvelopeSerializer>();
    services.Configure<RoutingOptions>(options => options.OwnDomains(OWNED_DOMAIN));
    services.AddReceptors();
    services.AddWhizbangDispatcher();
    return services.BuildServiceProvider();
  }

  /// <summary>
  /// Runtime-registered stage receptor. Generic, so the source generator cannot discover it and
  /// it never becomes a business receptor for the command it observes.
  /// </summary>
  private sealed class StageTracker<TMessage>(Func<TMessage, Guid> idOf) : IReceptor<TMessage> {
    private readonly List<Guid> _firedFor = [];

    public IReadOnlyList<Guid> FiredFor {
      get {
        lock (_firedFor) {
          return [.. _firedFor];
        }
      }
    }

    public ValueTask HandleAsync(TMessage message, CancellationToken cancellationToken = default) {
      lock (_firedFor) {
        _firedFor.Add(idOf(message));
      }
      return ValueTask.CompletedTask;
    }
  }

  /// <summary>
  /// Serializes only what the outbox assertions read: the payload type name. Payload content is
  /// irrelevant to routing and nothing here is ever delivered.
  /// </summary>
  private sealed class TypeRecordingEnvelopeSerializer : IEnvelopeSerializer {
    public SerializedEnvelope SerializeEnvelope<TMessage>(IMessageEnvelope<TMessage> envelope) {
      var jsonEnvelope = new MessageEnvelope<System.Text.Json.JsonElement> {
        MessageId = envelope.MessageId,
        Payload = System.Text.Json.JsonSerializer.SerializeToElement(new { }),
        Hops = [],
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Local }
      };
      var payloadType = envelope.Payload?.GetType() ?? typeof(TMessage);
      return new SerializedEnvelope(
        jsonEnvelope,
        EnvelopeTypeNameHelper.Format(TypeNameFormatter.AssemblyQualifiedName(payloadType)),
        TypeNameFormatter.AssemblyQualifiedName(payloadType));
    }

    public object DeserializeMessage(MessageEnvelope<System.Text.Json.JsonElement> jsonEnvelope, string messageTypeName) =>
      throw new NotSupportedException("Outbox entries recorded by these tests are never delivered");
  }

  /// <summary>Records what the dispatcher queues to the outbox; never delivers anything.</summary>
  private sealed class RecordingOutbox : IWorkCoordinatorStrategy {
    private readonly List<OutboxMessage> _queued = [];

    public IReadOnlyList<OutboxMessage> Queued {
      get {
        lock (_queued) {
          return [.. _queued];
        }
      }
    }

    /// <summary>
    /// Outbox entries carrying this command, matched by payload type and the command's id. A
    /// receptor's message result is cascaded to the outbox by design, so tests assert on copies
    /// of the command, not on an empty outbox.
    /// </summary>
    public IReadOnlyList<OutboxMessage> QueuedOf(object command) {
      var typeName = TypeNameFormatter.AssemblyQualifiedName(command.GetType());
      return [.. Queued.Where(m => m.MessageType == typeName)];
    }

    public void QueueOutboxMessage(OutboxMessage message) {
      lock (_queued) {
        _queued.Add(message);
      }
    }

    public void QueueInboxMessage(InboxMessage message) { }
    public void QueueOutboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
    public void QueueInboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
    public void QueueOutboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }
    public void QueueInboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }

    public Task FlushAsync(WorkBatchOptions flags, CancellationToken ct = default) => FlushAndGetBatchAsync(flags, ct);

    public Task<WorkBatch> FlushAndGetBatchAsync(WorkBatchOptions flags, CancellationToken ct = default) =>
      Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] });
  }
}
