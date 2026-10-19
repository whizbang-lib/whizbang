// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Commands.System;
using Whizbang.Core.Component.Tests.Generated;
using Whizbang.Core.Component.Tests.RuntimeLocalReceptors;
using Whizbang.Core.Component.Tests.RuntimeLocalReceptors.Owned;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Routing;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>
/// #1385, against the REAL generated dispatcher and receptor registry: a receptor registered into
/// <see cref="IReceptorRegistry"/> at runtime, at a local dispatch stage, is the message's handler
/// for local dispatch exactly as a generated receptor without <c>[FireAt]</c> is. Before the fix the
/// local paths consulted only the generated lookups, so such a command was never handled in-process:
/// <c>LocalInvokeAsync</c> threw, an owned <c>SendAsync</c> threw, and an unowned one went to the
/// outbox.
/// </summary>
/// <remarks>
/// <para>
/// Framework system commands are the exception to local-first sending: they are always broadcast.
/// The sender still handles its own copy in-process when it has a handler, and the outbox copy then
/// carries <see cref="DispatchModes.LocalDispatch"/> so the sender's PreOutbox stage does not run the
/// handler a second time.
/// </para>
/// <para>
/// Every handled-assertion waits on the receptor's own completion signal; nothing waits on time.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Dispatcher.cs</code-under-test>
/// <docs>fundamentals/dispatcher/routing#system-commands</docs>
public class RuntimeReceptorLocalDispatchComponentTests {
  private const string OWNED_DOMAIN = "Whizbang.Core.Component.Tests.RuntimeLocalReceptors.Owned";

  [Test]
  [Arguments(LifecycleStage.LocalImmediateInline)]
  [Arguments(LifecycleStage.LocalImmediateDetached)]
  public async Task SendAsync_UnownedCommandWithOnlyARuntimeReceptor_HandlesItLocallyAndSendsAMarkedCopyAsync(LifecycleStage stage) {
    var outbox = new RecordingOutbox();
    await using var provider = _buildProvider(outbox);
    _registry(provider).Register(new RecordingRuntimeReceptor<UnownedRuntimeCommand>(), stage);
    var command = new UnownedRuntimeCommand(Guid.CreateVersion7());

    var receipt = await provider.GetRequiredService<IDispatcher>().SendAsync(command);

    await Assert.That(RuntimeReceptorProbe.CountFor(command.Id)).IsEqualTo(1)
      .Because("the runtime receptor is the command's local handler and runs before SendAsync returns");
    await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Delivered);
    var queued = outbox.QueuedOf(typeof(UnownedRuntimeCommand));
    await Assert.That(queued).Count().IsEqualTo(1)
      .Because("an unowned command this service handles also goes to the outbox for the other services that handle it (#1396)");
    await Assert.That(queued[0].Envelope.DispatchContext.Mode.HasFlag(DispatchModes.LocalDispatch)).IsTrue()
      .Because("the copy is marked as handled here, so this service does not run it again");
  }

  [Test]
  public async Task SendAsync_OwnedCommandWithOnlyARuntimeReceptor_HandlesItLocallyAsync() {
    var outbox = new RecordingOutbox();
    await using var provider = _buildProvider(outbox);
    _registry(provider).Register(new RecordingRuntimeReceptor<OwnedRuntimeCommand>(), LifecycleStage.LocalImmediateInline);
    var command = new OwnedRuntimeCommand(Guid.CreateVersion7());

    await provider.GetRequiredService<IDispatcher>().SendAsync(command);

    await Assert.That(RuntimeReceptorProbe.CountFor(command.Id)).IsEqualTo(1);
    await Assert.That(outbox.QueuedOf(typeof(OwnedRuntimeCommand))).IsEmpty();
  }

  [Test]
  public async Task SendAsync_WithOptions_OwnedCommandWithOnlyARuntimeReceptor_HandlesItLocallyAsync() {
    var outbox = new RecordingOutbox();
    await using var provider = _buildProvider(outbox);
    _registry(provider).Register(new RecordingRuntimeReceptor<OwnedRuntimeCommand>(), LifecycleStage.LocalImmediateInline);
    var command = new OwnedRuntimeCommand(Guid.CreateVersion7());

    await provider.GetRequiredService<IDispatcher>().SendAsync(command, new DispatchOptions());

    await Assert.That(RuntimeReceptorProbe.CountFor(command.Id)).IsEqualTo(1);
    await Assert.That(outbox.QueuedOf(typeof(OwnedRuntimeCommand))).IsEmpty();
  }

  [Test]
  public async Task SendAsync_WithOptions_OwnedCommandWithNoReceptor_ThrowsAndQueuesNothingAsync() {
    var outbox = new RecordingOutbox();
    await using var provider = _buildProvider(outbox);
    var dispatcher = provider.GetRequiredService<IDispatcher>();

    await Assert.That(async () => await dispatcher.SendAsync(new OwnedUnhandledCommand(Guid.CreateVersion7()), new DispatchOptions()))
      .Throws<ReceptorNotFoundException>()
      .Because("an owned command with no handler is a wiring error on every SendAsync overload, not only the ones without options");
    await Assert.That(async () => await dispatcher.SendAsync((object)new OwnedUnhandledCommand(Guid.CreateVersion7()), new DispatchOptions()))
      .Throws<ReceptorNotFoundException>();
    await Assert.That(outbox.QueuedOf(typeof(OwnedUnhandledCommand))).IsEmpty();
  }

  [Test]
  [Arguments(LifecycleStage.LocalImmediateInline)]
  [Arguments(LifecycleStage.LocalImmediateDetached)]
  public async Task LocalInvokeAsync_CommandWithOnlyARuntimeReceptor_HandlesItAsync(LifecycleStage stage) {
    await using var provider = _buildProvider(new RecordingOutbox());
    _registry(provider).Register(new RecordingRuntimeReceptor<UnownedRuntimeCommand>(), stage);
    var command = new UnownedRuntimeCommand(Guid.CreateVersion7());

    await provider.GetRequiredService<IDispatcher>().LocalInvokeAsync(command);

    await Assert.That(RuntimeReceptorProbe.CountFor(command.Id)).IsEqualTo(1);
  }

  [Test]
  public async Task LocalInvokeAsync_WithAResult_RuntimeReceptorAnswersAsync() {
    await using var provider = _buildProvider(new RecordingOutbox());
    _registry(provider).Register(new AnsweringRuntimeReceptor<RuntimeResultCommand>(), LifecycleStage.LocalImmediateInline);
    var command = new RuntimeResultCommand(Guid.CreateVersion7());

    var result = await provider.GetRequiredService<IDispatcher>().LocalInvokeAsync<RuntimeResultCommand, RuntimeResult>(command);

    await Assert.That(result).IsEqualTo(new RuntimeResult(command.Id, "answered at runtime"));
    await Assert.That(RuntimeReceptorProbe.CountFor(command.Id)).IsEqualTo(1);
  }

  [Test]
  public async Task SendManyAsync_OwnedCommandsWithOnlyARuntimeReceptor_HandlesEachLocallyAsync() {
    var outbox = new RecordingOutbox();
    await using var provider = _buildProvider(outbox);
    _registry(provider).Register(new RecordingRuntimeReceptor<OwnedRuntimeCommand>(), LifecycleStage.LocalImmediateInline);
    OwnedRuntimeCommand[] commands = [new(Guid.CreateVersion7()), new(Guid.CreateVersion7())];

    await provider.GetRequiredService<IDispatcher>().SendManyAsync(commands);

    foreach (var command in commands) {
      await Assert.That(RuntimeReceptorProbe.CountFor(command.Id)).IsEqualTo(1);
    }
    await Assert.That(outbox.QueuedOf(typeof(OwnedRuntimeCommand))).IsEmpty();
  }

  [Test]
  public async Task SendManyAsync_UnownedCommandHandledLocally_FlagsItsOutboxCopyAsLocallyDispatchedAsync() {
    var outbox = new RecordingOutbox();
    await using var provider = _buildProvider(outbox);
    _registry(provider).Register(new RecordingRuntimeReceptor<UnownedRuntimeCommand>(), LifecycleStage.LocalImmediateInline);
    var command = new UnownedRuntimeCommand(Guid.CreateVersion7());

    await provider.GetRequiredService<IDispatcher>().SendManyAsync([command]);

    await Assert.That(RuntimeReceptorProbe.CountFor(command.Id)).IsEqualTo(1);
    var queued = outbox.QueuedOf(typeof(UnownedRuntimeCommand));
    await Assert.That(queued).Count().IsEqualTo(1)
      .Because("SendManyAsync handles a command locally AND sends it on; only LocalSendManyAsync stays local");
    await Assert.That(queued[0].Envelope.DispatchContext.Mode.HasFlag(DispatchModes.LocalDispatch)).IsTrue()
      .Because("the sender already ran the handler, so its PreOutbox stage must not run it again");
  }

  [Test]
  public async Task SendManyAsync_UntypedUnownedCommandHandledLocally_FlagsItsOutboxCopyAsLocallyDispatchedAsync() {
    var outbox = new RecordingOutbox();
    await using var provider = _buildProvider(outbox);
    _registry(provider).Register(new RecordingRuntimeReceptor<UnownedRuntimeCommand>(), LifecycleStage.LocalImmediateInline);
    var command = new UnownedRuntimeCommand(Guid.CreateVersion7());

    await provider.GetRequiredService<IDispatcher>().SendManyAsync(new object[] { command });

    await Assert.That(RuntimeReceptorProbe.CountFor(command.Id)).IsEqualTo(1);
    var queued = outbox.QueuedOf(typeof(UnownedRuntimeCommand));
    await Assert.That(queued).Count().IsEqualTo(1);
    await Assert.That(queued[0].Envelope.DispatchContext.Mode.HasFlag(DispatchModes.LocalDispatch)).IsTrue();
  }

  [Test]
  public async Task PreOutbox_LocallyDispatchedCopy_SkipsARuntimeReceptorThatAlsoRegisteredLocallyAsync() {
    // No owned domain is configured: the blanket owned-domain skip is off, so this is the narrow rule
    // alone. A framework registrar registers one receptor at a local stage AND at PreOutbox; once the
    // local dispatch ran it, the sender's outbox copy must not run it again.
    await using var provider = _buildProvider(new RecordingOutbox(), ownedDomain: null);
    var receptor = new RecordingRuntimeReceptor<UnownedRuntimeCommand>();
    _registry(provider).Register(receptor, LifecycleStage.LocalImmediateInline);
    _registry(provider).Register(receptor, LifecycleStage.PreOutboxInline);
    var command = new UnownedRuntimeCommand(Guid.CreateVersion7());

    await _invokeAtPreOutboxAsync(provider, command, DispatchModes.Both);

    await Assert.That(RuntimeReceptorProbe.CountFor(command.Id)).IsEqualTo(0)
      .Because("the receptor already ran in the local dispatch that produced this outbox copy");
  }

  [Test]
  public async Task PreOutbox_UnflaggedCopy_RunsARuntimeReceptorThatAlsoRegisteredLocallyAsync() {
    // The outbox copy of a message nothing handled locally (a send with no local handler, or a
    // framework worker writing the outbox directly): the sender's handler runs here.
    await using var provider = _buildProvider(new RecordingOutbox(), ownedDomain: null);
    var receptor = new RecordingRuntimeReceptor<UnownedRuntimeCommand>();
    _registry(provider).Register(receptor, LifecycleStage.LocalImmediateInline);
    _registry(provider).Register(receptor, LifecycleStage.PreOutboxInline);
    var command = new UnownedRuntimeCommand(Guid.CreateVersion7());

    await _invokeAtPreOutboxAsync(provider, command, DispatchModes.Outbox);

    await Assert.That(RuntimeReceptorProbe.CountFor(command.Id)).IsEqualTo(1);
  }

  [Test]
  public async Task PreOutbox_LocallyDispatchedCopy_StillRunsARuntimeReceptorRegisteredOnlyAtPreOutboxAsync() {
    // A hook that observes the outbox never ran during local dispatch, so the narrow rule leaves it be.
    await using var provider = _buildProvider(new RecordingOutbox(), ownedDomain: null);
    _registry(provider).Register(new RecordingRuntimeReceptor<UnownedRuntimeCommand>(), LifecycleStage.PreOutboxInline);
    var command = new UnownedRuntimeCommand(Guid.CreateVersion7());

    await _invokeAtPreOutboxAsync(provider, command, DispatchModes.Both);

    await Assert.That(RuntimeReceptorProbe.CountFor(command.Id)).IsEqualTo(1);
  }

  [Test]
  public async Task PreOutbox_LocallyDispatchedCopy_SkipsAReceptorRegisteredAtTheDetachedLocalStageButRunsTheRestAsync() {
    // The same instance at LocalImmediateDetached (not Inline) still counts as having run locally;
    // a second receptor registered only at PreOutbox still runs on the same copy.
    await using var provider = _buildProvider(new RecordingOutbox(), ownedDomain: null);
    var ranLocally = new RecordingRuntimeReceptor<UnownedRuntimeCommand>();
    _registry(provider).Register(ranLocally, LifecycleStage.LocalImmediateDetached);
    _registry(provider).Register(ranLocally, LifecycleStage.PreOutboxInline);
    _registry(provider).Register(new AnsweringRuntimeReceptor<UnownedRuntimeCommand>(), LifecycleStage.PreOutboxInline);
    var command = new UnownedRuntimeCommand(Guid.CreateVersion7());

    await _invokeAtPreOutboxAsync(provider, command, DispatchModes.Both);

    await Assert.That(RuntimeReceptorProbe.CountFor(command.Id)).IsEqualTo(1)
      .Because("only the receptor that never ran locally runs at PreOutbox");
  }

  /// <summary>A runtime receptor that returns nothing answers a typed local invoke with the default, having run once.</summary>
  [Test]
  public async Task LocalInvokeTyped_RuntimeReceptorWithNoResult_AnswersTheDefaultAsync() {
    await using var provider = _buildProvider(new RecordingOutbox());
    _registry(provider).Register(new RecordingRuntimeReceptor<UnownedRuntimeCommand>(), LifecycleStage.LocalImmediateInline);
    var command = new UnownedRuntimeCommand(Guid.CreateVersion7());

    var result = await provider.GetRequiredService<IDispatcher>().LocalInvokeAsync<RuntimeResult>((object)command, new DispatchOptions());

    await Assert.That(result).IsNull();
    await Assert.That(RuntimeReceptorProbe.CountFor(command.Id)).IsEqualTo(1);
  }

  /// <summary>
  /// A runtime receptor whose result holds the requested type answers with it, run once: the result is
  /// not the requested type whole, and the response is extracted instead of running the receptor again.
  /// </summary>
  [Test]
  public async Task LocalInvokeTyped_RuntimeReceptorReturningATuple_AnswersWithTheRequestedPartOnceAsync() {
    await using var provider = _buildProvider(new RecordingOutbox());
    _registry(provider).Register(new TupleAnsweringRuntimeReceptor<RuntimeResultCommand>(), LifecycleStage.LocalImmediateInline);
    var command = new RuntimeResultCommand(Guid.CreateVersion7());

    var result = await provider.GetRequiredService<IDispatcher>().LocalInvokeAsync<RuntimeResult>((object)command, new DispatchOptions());

    await Assert.That(result).IsEqualTo(new RuntimeResult(command.Id, "inside a tuple"));
    await Assert.That(RuntimeReceptorProbe.CountFor(command.Id)).IsEqualTo(1);
  }

  [Test]
  public async Task LocalInvokeTypedWithOptions_RuntimeReceptorWithAResult_AnswersAsync() {
    await using var provider = _buildProvider(new RecordingOutbox());
    _registry(provider).Register(new AnsweringRuntimeReceptor<RuntimeResultCommand>(), LifecycleStage.LocalImmediateInline);
    var command = new RuntimeResultCommand(Guid.CreateVersion7());

    var result = await provider.GetRequiredService<IDispatcher>().LocalInvokeAsync<RuntimeResult>((object)command, new DispatchOptions());

    await Assert.That(result.Id).IsEqualTo(command.Id);
    await Assert.That(RuntimeReceptorProbe.CountFor(command.Id)).IsEqualTo(1);
  }

  [Test]
  [Arguments(false)]
  [Arguments(true)]
  public async Task LocalInvokeWithReceipt_RuntimeReceptorWithAResult_AnswersWithAReceiptAsync(bool withOptions) {
    await using var provider = _buildProvider(new RecordingOutbox());
    _registry(provider).Register(new AnsweringRuntimeReceptor<RuntimeResultCommand>(), LifecycleStage.LocalImmediateInline);
    var command = new RuntimeResultCommand(Guid.CreateVersion7());
    var dispatcher = provider.GetRequiredService<IDispatcher>();

    var invoked = withOptions
      ? await dispatcher.LocalInvokeWithReceiptAsync<RuntimeResult>((object)command, new DispatchOptions())
      : await dispatcher.LocalInvokeWithReceiptAsync<RuntimeResult>((object)command);

    await Assert.That(invoked.Value.Id).IsEqualTo(command.Id);
    await Assert.That(invoked.Receipt).IsNotNull();
    await Assert.That(RuntimeReceptorProbe.CountFor(command.Id)).IsEqualTo(1);
  }

  [Test]
  public async Task LocalInvokeVoidWithOptions_SyncReceptorBesideAnImmediateDetachedHook_RunsBothOnceAsync() {
    await using var provider = _buildProvider(new RecordingOutbox());
    var command = new SyncVoidCommand(Guid.CreateVersion7());

    await provider.GetRequiredService<IDispatcher>().LocalInvokeAsync((object)command, new DispatchOptions());
    await SyncVoidCommandImmediateDetachedHook.Fired(command.Id);

    await Assert.That(RuntimeReceptorProbe.CountFor(command.Id)).IsEqualTo(1);
    await Assert.That(SyncVoidCommandImmediateDetachedHook.CountFor(command.Id)).IsEqualTo(1)
      .Because("a sync handler with an ImmediateDetached hook takes the full path, which fires the hook once");
  }

  [Test]
  [Arguments(false)]
  [Arguments(true)]
  public async Task SendObject_SystemCommandWithARuntimeReceptor_HandlesItAndBroadcastsAMarkedCopyAsync(bool withOptions) {
    var outbox = new RecordingOutbox();
    await using var provider = _buildProvider(outbox);
    _registry(provider).Register(new SystemCommandRuntimeReceptor<ClearCacheCommand>(), LifecycleStage.LocalImmediateInline);
    var id = Guid.CreateVersion7();
    var command = new ClearCacheCommand(CacheKey: id.ToString());
    var dispatcher = provider.GetRequiredService<IDispatcher>();

    if (withOptions) {
      await dispatcher.SendAsync((object)command, MessageContext.New(), new DispatchOptions());
    } else {
      await dispatcher.SendAsync((object)command, MessageContext.New());
    }

    await Assert.That(RuntimeReceptorProbe.CountFor(id)).IsEqualTo(1);
    var queued = outbox.QueuedOf(typeof(ClearCacheCommand));
    await Assert.That(queued).Count().IsEqualTo(1);
    await Assert.That(queued[0].Envelope.DispatchContext.Mode.HasFlag(DispatchModes.LocalDispatch)).IsTrue();
  }

  [Test]
  public async Task SendManyObject_SystemCommandHandledLocally_QueuesOneMarkedCopyAsync() {
    var outbox = new RecordingOutbox();
    await using var provider = _buildProvider(outbox);
    _registry(provider).Register(new SystemCommandRuntimeReceptor<ClearCacheCommand>(), LifecycleStage.LocalImmediateInline);
    var id = Guid.CreateVersion7();

    await provider.GetRequiredService<IDispatcher>().SendManyAsync([(object)new ClearCacheCommand(CacheKey: id.ToString())]);

    await Assert.That(RuntimeReceptorProbe.CountFor(id)).IsEqualTo(1);
    await Assert.That(outbox.QueuedOf(typeof(ClearCacheCommand))).Count().IsEqualTo(1)
      .Because("the batch queues the copy itself; the local send inside it must not queue a second");
  }

  [Test]
  [Arguments(false)]
  [Arguments(true)]
  public async Task Send_SystemCommandHandledLocallyWithNoOutbox_ReturnsTheLocalReceiptAsync(bool untyped) {
    await using var provider = _buildProviderWithoutOutbox();
    _registry(provider).Register(new SystemCommandRuntimeReceptor<ClearCacheCommand>(), LifecycleStage.LocalImmediateInline);
    var id = Guid.CreateVersion7();
    var command = new ClearCacheCommand(CacheKey: id.ToString());
    var dispatcher = provider.GetRequiredService<IDispatcher>();

    var receipt = untyped
      ? await dispatcher.SendAsync((object)command, MessageContext.New())
      : await dispatcher.SendAsync(command);

    await Assert.That(RuntimeReceptorProbe.CountFor(id)).IsEqualTo(1);
    await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Delivered)
      .Because("with no outbox to broadcast through, the local run is the delivery");
  }

  private static ServiceProvider _buildProviderWithoutOutbox() {
    var services = new ServiceCollection();
    services.AddSingleton<IServiceInstanceProvider>(
      new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()));
    services.AddSingleton<IEnvelopeSerializer, ContextKeepingEnvelopeSerializer>();
    services.AddReceptors();
    services.AddWhizbangDispatcher();
    return services.BuildServiceProvider();
  }

  private static async Task _invokeAtPreOutboxAsync(ServiceProvider provider, UnownedRuntimeCommand command, DispatchModes mode) {
    await using var scope = provider.CreateAsyncScope();
    var invoker = scope.ServiceProvider.GetRequiredService<IReceptorInvoker>();
    var envelope = new MessageEnvelope<UnownedRuntimeCommand> {
      MessageId = MessageId.New(),
      Payload = command,
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = mode, Source = MessageSource.Outbox }
    };
    await invoker.InvokeAsync(envelope, LifecycleStage.PreOutboxInline,
      new LifecycleExecutionContext { CurrentStage = LifecycleStage.PreOutboxInline, MessageSource = MessageSource.Outbox });
  }

  [Test]
  public async Task SendAsync_SystemCommandWithARuntimeReceptor_HandlesItLocallyOnceAndBroadcastsItAsync() {
    var outbox = new RecordingOutbox();
    await using var provider = _buildProvider(outbox);
    _registry(provider).Register(new SystemCommandRuntimeReceptor<ClearCacheCommand>(), LifecycleStage.LocalImmediateInline);
    var id = Guid.CreateVersion7();

    await provider.GetRequiredService<IDispatcher>().SendAsync(new ClearCacheCommand(CacheKey: id.ToString()));

    await Assert.That(RuntimeReceptorProbe.CountFor(id)).IsEqualTo(1)
      .Because("the sending service handles its own copy of a broadcast system command");
    var queued = outbox.QueuedOf(typeof(ClearCacheCommand));
    await Assert.That(queued).Count().IsEqualTo(1)
      .Because("a system command is always broadcast, even when the sender handles it");
    await Assert.That(queued[0].Envelope.DispatchContext.Mode.HasFlag(DispatchModes.LocalDispatch)).IsTrue();
  }

  [Test]
  public async Task SendAsync_SystemCommandWithAGeneratedReceptor_HandlesItLocallyOnceAndBroadcastsItAsync() {
    var outbox = new RecordingOutbox();
    await using var provider = _buildProvider(outbox);
    var id = Guid.CreateVersion7();

    await provider.GetRequiredService<IDispatcher>().SendAsync(new DiagnosticsCommand(DiagnosticType.HealthCheck, id));

    await Assert.That(RuntimeReceptorProbe.CountFor(id)).IsEqualTo(1);
    var queued = outbox.QueuedOf(typeof(DiagnosticsCommand));
    await Assert.That(queued).Count().IsEqualTo(1)
      .Because("a local handler must not stop a system command from reaching every other service");
    await Assert.That(queued[0].Envelope.DispatchContext.Mode.HasFlag(DispatchModes.LocalDispatch)).IsTrue();
  }

  [Test]
  public async Task SendAsync_WithOptions_SystemCommandWithAGeneratedReceptor_BroadcastsItAsync() {
    var outbox = new RecordingOutbox();
    await using var provider = _buildProvider(outbox);
    var id = Guid.CreateVersion7();

    await provider.GetRequiredService<IDispatcher>().SendAsync(new DiagnosticsCommand(DiagnosticType.HealthCheck, id), new DispatchOptions());

    await Assert.That(RuntimeReceptorProbe.CountFor(id)).IsEqualTo(1);
    await Assert.That(outbox.QueuedOf(typeof(DiagnosticsCommand))).Count().IsEqualTo(1);
  }

  [Test]
  public async Task SendAsync_SystemCommandWithNoLocalHandler_BroadcastsItUnflaggedAsync() {
    var outbox = new RecordingOutbox();
    await using var provider = _buildProvider(outbox);

    await provider.GetRequiredService<IDispatcher>().SendAsync(new ClearCacheCommand(CacheKey: Guid.CreateVersion7().ToString()));

    var queued = outbox.QueuedOf(typeof(ClearCacheCommand));
    await Assert.That(queued).Count().IsEqualTo(1);
    await Assert.That(queued[0].Envelope.DispatchContext.Mode.HasFlag(DispatchModes.LocalDispatch)).IsFalse()
      .Because("nothing ran locally, so the sender's PreOutbox stage is where its own handlers run");
  }

  [Test]
  public async Task SendManyAsync_SystemCommandWithAGeneratedReceptor_HandlesItLocallyOnceAndBroadcastsItFlaggedAsync() {
    var outbox = new RecordingOutbox();
    await using var provider = _buildProvider(outbox);
    var id = Guid.CreateVersion7();

    await provider.GetRequiredService<IDispatcher>().SendManyAsync([new DiagnosticsCommand(DiagnosticType.HealthCheck, id)]);

    await Assert.That(RuntimeReceptorProbe.CountFor(id)).IsEqualTo(1);
    var queued = outbox.QueuedOf(typeof(DiagnosticsCommand));
    await Assert.That(queued).Count().IsEqualTo(1);
    await Assert.That(queued[0].Envelope.DispatchContext.Mode.HasFlag(DispatchModes.LocalDispatch)).IsTrue();
  }

  private static IReceptorRegistry _registry(IServiceProvider provider) =>
    provider.GetRequiredService<IReceptorRegistry>();

  private static ServiceProvider _buildProvider(RecordingOutbox outbox, string? ownedDomain = OWNED_DOMAIN) {
    var services = new ServiceCollection();
    services.AddSingleton<IServiceInstanceProvider>(
      new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()));
    services.AddScoped<IWorkCoordinatorStrategy>(_ => outbox);
    services.AddSingleton<IEnvelopeSerializer, ContextKeepingEnvelopeSerializer>();
    if (ownedDomain is not null) {
      services.Configure<RoutingOptions>(options => options.OwnDomains(ownedDomain));
    }
    services.AddReceptors();
    services.AddWhizbangDispatcher();
    return services.BuildServiceProvider();
  }

  /// <summary>
  /// Keeps what the assertions read: the payload type name and the dispatch context. Payload content
  /// is irrelevant to routing and nothing here is ever delivered.
  /// </summary>
  private sealed class ContextKeepingEnvelopeSerializer : IEnvelopeSerializer {
    public SerializedEnvelope SerializeEnvelope<TMessage>(IMessageEnvelope<TMessage> envelope) {
      var jsonEnvelope = new MessageEnvelope<System.Text.Json.JsonElement> {
        MessageId = envelope.MessageId,
        Payload = System.Text.Json.JsonSerializer.SerializeToElement(new { }),
        Hops = [],
        DispatchContext = envelope.DispatchContext
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
