// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Component.Tests.Generated;
using Whizbang.Core.Component.Tests.LocalStageSends;
using Whizbang.Core.Component.Tests.LocalStageSends.Owned;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Routing;

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>
/// #1379 on the LOCAL dispatch path, against the REAL generated dispatcher and receptor registry: a
/// receptor that fires at a local lifecycle stage sends a command the service owns, handled here by
/// a receptor that returns no result. The command must be handled exactly once, in-process, and
/// never queued to the outbox. Before the fix SendAsync answered Accepted and dropped such a command
/// from every stage alike, because it only looked for a receptor that returns a result.
/// </summary>
/// <remarks>
/// <para>
/// ImmediateDetached fires after SendAsync's business receptor completes. PostLifecycleInline and
/// PostLifecycleDetached fire only from LocalInvokeAsync; with an <see cref="ILifecycleCoordinator"/>
/// registered the detached stage runs on its own task and scope, and without one it is awaited
/// inline, so both configurations are covered. A receptor declared at LocalImmediateInline is the
/// message's handler for local dispatch, so that trigger's own handler makes the send; one declared at
/// LocalImmediateDetached is a hook that runs in the background after the trigger's handler (#1392).
/// </para>
/// <para>
/// Every assertion waits on the owned command's own completion signal and on the hook's send
/// returning, never on time. A send that throws completes both signals with the exception.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Dispatcher.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Lifecycle/LifecycleCoordinator.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Lifecycle/LifecycleTrackingState.cs</code-under-test>
/// <docs>fundamentals/dispatcher/routing#owned-domain-routing</docs>
public class LocalStageOwnedCommandSendComponentTests {
  private const string OWNED_DOMAIN = "Whizbang.Core.Component.Tests.LocalStageSends.Owned";

  public enum TriggerEntry { Send, LocalInvoke }

  [Test]
  [Arguments(LifecycleStage.ImmediateDetached, TriggerEntry.Send, false)]
  [Arguments(LifecycleStage.ImmediateDetached, TriggerEntry.Send, true)]
  [Arguments(LifecycleStage.PostLifecycleInline, TriggerEntry.LocalInvoke, false)]
  [Arguments(LifecycleStage.PostLifecycleInline, TriggerEntry.LocalInvoke, true)]
  [Arguments(LifecycleStage.PostLifecycleDetached, TriggerEntry.LocalInvoke, false)]
  [Arguments(LifecycleStage.PostLifecycleDetached, TriggerEntry.LocalInvoke, true)]
  [Arguments(LifecycleStage.LocalImmediateInline, TriggerEntry.Send, false)]
  [Arguments(LifecycleStage.LocalImmediateInline, TriggerEntry.LocalInvoke, false)]
  [Arguments(LifecycleStage.LocalImmediateDetached, TriggerEntry.Send, false)]
  [Arguments(LifecycleStage.LocalImmediateDetached, TriggerEntry.LocalInvoke, false)]
  public async Task ReceptorAtLocalStage_SendsOwnedVoidCommand_HandledOnceAndNeverInTheOutboxAsync(
      LifecycleStage stage, TriggerEntry entry, bool withLifecycleCoordinator) {
    var outbox = new RecordingOutbox();
    await using var provider = _buildProvider(outbox, withLifecycleCoordinator);
    var dispatcher = provider.GetRequiredService<IDispatcher>();
    var trigger = LocalStageTriggers.For(stage);
    var id = LocalStageTriggers.IdOf(trigger);
    var handled = LocalStageSendProbe.Handled(id);
    var sendReturned = LocalStageSendProbe.SendReturned(id);

    if (entry == TriggerEntry.Send) {
      await dispatcher.SendAsync(trigger);
    } else {
      await dispatcher.LocalInvokeAsync(trigger);
    }

    // The owned command's receptor completing, and the hook's send returning, are the transitions
    // under test; a send the dispatcher dropped never completes them and a throw faults them
    await handled.Task.WaitAsync(TimeSpan.FromSeconds(30));
    await sendReturned.Task.WaitAsync(TimeSpan.FromSeconds(30));

    await Assert.That(LocalStageSendProbe.CountFor(id)).IsEqualTo(1)
      .Because("the owned command must be handled exactly once, in-process");
    await Assert.That(outbox.QueuedOf(typeof(OwnedLocalStageCommand))).IsEmpty()
      .Because("a command the service owns and handles never goes to the outbox");
  }

  private static ServiceProvider _buildProvider(RecordingOutbox outbox, bool withLifecycleCoordinator) {
    var services = new ServiceCollection();
    services.AddSingleton<IServiceInstanceProvider>(
      new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()));
    services.AddScoped<IWorkCoordinatorStrategy>(_ => outbox);
    services.AddSingleton<IEnvelopeSerializer, TypeRecordingEnvelopeSerializer>();
    services.Configure<RoutingOptions>(options => options.OwnDomains(OWNED_DOMAIN));
    if (withLifecycleCoordinator) {
      services.AddSingleton<ILifecycleCoordinator>(new LifecycleCoordinator(NullLogger<LifecycleCoordinator>.Instance));
    }
    services.AddReceptors();
    services.AddWhizbangDispatcher();
    return services.BuildServiceProvider();
  }

  /// <summary>
  /// Serializes only what the outbox assertion reads: the payload type name. Payload content is
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

    /// <summary>Outbox entries whose payload is of the given type.</summary>
    public IReadOnlyList<OutboxMessage> QueuedOf(Type messageType) {
      var typeName = TypeNameFormatter.AssemblyQualifiedName(messageType);
      lock (_queued) {
        return [.. _queued.Where(m => m.MessageType == typeName)];
      }
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
