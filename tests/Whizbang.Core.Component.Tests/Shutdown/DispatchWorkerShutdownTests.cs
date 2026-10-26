// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Routing;
using Whizbang.Core.Workers;
using Whizbang.Testing.Workers;
using MessageId = Whizbang.Core.ValueObjects.MessageId;

namespace Whizbang.Core.Tests.Shutdown;

/// <summary>
/// #1721: the inbox dispatch and outbox drain workers start detached lifecycle stages in the background, and a failing
/// one is reported on the failure channel. Nothing awaited them: the host disposed the container while they ran, and a
/// failure reported after the failure flush had stopped was lost. The worker now waits for them when it stops, before the
/// hosted services registered ahead of it (the failure flush among them) stop.
/// </summary>
[NotInParallel("WhizbangBackgroundServiceTests")]
public class DispatchWorkerShutdownTests {

  private sealed class NoOpInvoker : IReceptorInvoker {
    public ValueTask InvokeAsync(IMessageEnvelope envelope, LifecycleStage stage, ILifecycleContext? context = null,
        CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
  }

  private sealed class NoOpFailureChannel : IFailureChannel {
    public ValueTask EnqueueAsync(WorkCategory category, MessageFailure failure, CancellationToken cancellationToken = default) =>
      ValueTask.CompletedTask;
  }

  private sealed class NoOpCommitChannel : IInboxHandlerCommitChannel {
    public ValueTask EnqueueAsync(HandlerCommitRequest request, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
  }

  private sealed class NoOpCompletionChannel : IOutboxCompletionChannel {
    public ValueTask EnqueueAsync(Guid outboxMessageId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
  }

  /// <summary>Answers "a receptor is registered" for the given stages only.</summary>
  private sealed class StageRegistryQuery(params LifecycleStage[] stages) : IReceptorRegistryQuery {
    public bool HasReceptors(LifecycleStage stage, string messageType) => Array.IndexOf(stages, stage) >= 0;
    public bool HasInboxHandler(string messageType) => true;
    public bool HasAnyConsumer(string messageType) => true;
  }

  private static HostApplicationBuilder _builder(HeldWork held, LifecycleStage heldStage) {
    var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
    builder.Services.TryAddWhizbangDefaults();
    builder.Services.AddSingleton<ProviderLiveness>();
    builder.Services.AddSingleton<IServiceInstanceProvider>(
      new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()));
    builder.Services.AddScoped<IReceptorInvoker>(sp => new HeldStageInvoker(held, heldStage, sp));
    return builder;
  }

  private static MessageEnvelope<JsonElement> _envelope(MessageId messageId) => new() {
    MessageId = messageId,
    Payload = JsonElement.Parse("{}"),
    Hops = [],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
  };

  private static InboxDispatchWorker _inboxWorker(IServiceProvider sp, IReceptorRegistryQuery registry) => new(
    scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
    instanceProvider: sp.GetRequiredService<IServiceInstanceProvider>(),
    inboxChannelWriter: new InboxChannelWriter(),
    handlerCommitChannel: new NoOpCommitChannel(),
    failureChannel: new NoOpFailureChannel(),
    schemaReadyGate: SchemaReadyGate.AlreadyReady(),
    options: Options.Create(new InboxDispatchWorkerOptions()),
    coordinatorOptions: Options.Create(new WorkCoordinatorOptions()),
    logger: NullLogger<InboxDispatchWorker>.Instance,
    integrityOptions: Options.Create(new StreamIntegrityOptions()),
    lifecycleMessageDeserializer: new PassThroughLifecycleDeserializer(),
    leaseHandleOptions: Options.Create(new LeaseHandleOptions()),
    leaseRenewalOptions: Options.Create(new LeaseRenewalWorkerOptions()),
    receptorRegistry: registry,
    discardPolicy: new MessageDiscardPolicy(new PermissiveReceptorRegistryQuery(), NullLogger<MessageDiscardPolicy>.Instance,
      new System.Diagnostics.Metrics.Meter("shutdown-test"), Options.Create(new RoutingOptions()),
      new EventMarkerResolver(NullMessageTypeCatalog.Instance)),
    runtimeReceptorRegistry: NullReceptorRegistry.Instance,
    deadLetterStore: NullDeadLetterStore.Instance,
    generationProvider: new DefaultGenerationProvider());

  private static async Task<(IHost Host, InboxDispatchWorker Worker)> _startInboxAsync(HeldWork held, LifecycleStage heldStage,
      IReceptorRegistryQuery registry) {
    var builder = _builder(held, heldStage);
    builder.Services.AddSingleton(sp => _inboxWorker(sp, registry));
    builder.Services.AddHostedService(sp => sp.GetRequiredService<InboxDispatchWorker>());
    var host = builder.Build();
    await host.StartAsync();
    return (host, host.Services.GetRequiredService<InboxDispatchWorker>());
  }

  private static InboxWork _inboxWork(MessageEnvelope<JsonElement> envelope) => new() {
    MessageId = envelope.MessageId.Value,
    Envelope = envelope,
    MessageType = "System.Text.Json.JsonElement, System.Text.Json",
  };

  [Test]
  public async Task AnInboxDetachedStage_InFlight_HostStopWaitsForItAsync() {
    var held = new HeldWork();
    var (host, worker) = await _startInboxAsync(held, LifecycleStage.PostInboxDetached,
      new StageRegistryQuery(LifecycleStage.PostInboxDetached, LifecycleStage.PostInboxInline));
    using var _ = host;
    var envelope = _envelope(MessageId.New());

    await worker.InvokeInboxLifecycleStageAsync(_inboxWork(envelope), envelope, new NoOpInvoker(),
      LifecycleStage.PostInboxDetached, LifecycleStage.PostInboxInline, "PostInbox", CancellationToken.None, CancellationToken.None);
    await held.Entered.Task;

    var waited = await HostShutdown.StopWaitsForAsync(host, worker.DetachedWork, held);

    await Assert.That(waited).IsTrue()
      .Because("the detached stage resolves its receptor, and reports a failure, through services the host disposes");
    await Assert.That(await held.Finished.Task).IsTrue();
  }

  [Test]
  public async Task AnInboxImmediateDetachedStage_InFlight_HostStopWaitsForItAsync() {
    var held = new HeldWork();
    // No receptor at the detached PostInbox stage, so ImmediateDetached starts on its own after the inline stage
    var (host, worker) = await _startInboxAsync(held, LifecycleStage.ImmediateDetached,
      new StageRegistryQuery(LifecycleStage.PostInboxInline));
    using var _ = host;
    var envelope = _envelope(MessageId.New());

    await worker.InvokeInboxLifecycleStageAsync(_inboxWork(envelope), envelope, new NoOpInvoker(),
      LifecycleStage.PostInboxDetached, LifecycleStage.PostInboxInline, "PostInbox", CancellationToken.None, CancellationToken.None);
    await held.Entered.Task;

    var waited = await HostShutdown.StopWaitsForAsync(host, worker.DetachedWork, held);

    await Assert.That(waited).IsTrue();
    await Assert.That(await held.Finished.Task).IsTrue();
  }

  [Test]
  public async Task AnOutboxDetachedStage_InFlight_HostStopWaitsForItAsync() {
    var held = new HeldWork();
    var builder = _builder(held, LifecycleStage.PreOutboxDetached);
    var options = new OutboxDrainWorkerOptions { Enabled = true };
    builder.Services.AddSingleton(sp => new OutboxDrainWorker(
      scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
      instanceProvider: sp.GetRequiredService<IServiceInstanceProvider>(),
      drainChannel: new OutboxDrainChannel(),
      completionChannel: new NoOpCompletionChannel(),
      failureChannel: new NoOpFailureChannel(),
      schemaReadyGate: SchemaReadyGate.AlreadyReady(),
      options: Options.Create(options),
      jsonOptions: Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions(),
      logger: NullLogger<OutboxDrainWorker>.Instance,
      publishStrategy: NullMessagePublishStrategy.Instance,
      lifecycleMessageDeserializer: new PassThroughLifecycleDeserializer(),
      receptorRegistry: new StageRegistryQuery(LifecycleStage.PreOutboxDetached),
      runtimeReceptorRegistry: NullReceptorRegistry.Instance,
      deadLetterStore: NullDeadLetterStore.Instance,
      generationProvider: new DefaultGenerationProvider(),
      governor: OutboxDrainWorker.CreateDefaultGovernor(options),
      leaseRenewalOptions: Options.Create(new LeaseRenewalWorkerOptions())));
    builder.Services.AddHostedService(sp => sp.GetRequiredService<OutboxDrainWorker>());
    using var host = builder.Build();
    await host.StartAsync();
    var worker = host.Services.GetRequiredService<OutboxDrainWorker>();
    var envelope = _envelope(MessageId.New());
    var work = new OutboxWork {
      MessageId = envelope.MessageId.Value,
      Destination = "orders",
      Envelope = envelope,
      EnvelopeType = "MessageEnvelope`1[[System.Text.Json.JsonElement, System.Text.Json]], Whizbang.Core",
      MessageType = "System.Text.Json.JsonElement, System.Text.Json",
      Attempts = 0,
    };

    await worker.InvokeOutboxLifecycleStageAsync(work, envelope, new NoOpInvoker(),
      LifecycleStage.PreOutboxDetached, LifecycleStage.PreOutboxInline, "PreOutbox", CancellationToken.None);
    await held.Entered.Task;

    var waited = await HostShutdown.StopWaitsForAsync(host, worker.DetachedWork, held);

    await Assert.That(waited).IsTrue();
    await Assert.That(await held.Finished.Task).IsTrue();
  }
}
