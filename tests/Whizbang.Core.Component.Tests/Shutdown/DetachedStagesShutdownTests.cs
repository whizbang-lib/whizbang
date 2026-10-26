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
using Whizbang.Core.Component.Tests.Generated;
using Whizbang.Core.Component.Tests.ShutdownHeld;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.SystemEvents;
using Whizbang.Core.Tags;
using Whizbang.Core.Tests.Workers;
using Whizbang.Core.Tracing;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Shutdown;

/// <summary>
/// #1721: the detached lifecycle stages the framework starts outside the outbox batcher are waited for while the host
/// stops, before it disposes the service provider they resolve their receptors from. Each test holds one site's
/// detached work in flight, stops the host, and checks that the stop waited and that the work then ran against a live
/// provider. A stop that does not wait finishes, container disposed, while the work still holds, and the work then
/// finds the provider gone.
/// </summary>
[NotInParallel("WhizbangBackgroundServiceTests")]
public class DetachedStagesShutdownTests {

  private static HostApplicationBuilder _builder() {
    var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
    builder.Services.TryAddWhizbangDefaults();
    builder.Services.AddSingleton<ProviderLiveness>();
    builder.Services.AddSingleton<IServiceInstanceProvider>(
      new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()));
    return builder;
  }

  /// <summary>
  /// The work coordinator strategies' flush starts PreDistribute, Distribute and PostDistribute detached stages. They
  /// were started without the host's record of detached work, so nothing waited for them.
  /// </summary>
  [Test]
  public async Task AStrategyFlush_DetachedStageInFlight_HostStopWaitsForItAsync() {
    var held = new HeldWork();
    var builder = _builder();
    builder.Services.AddSingleton<ILifecycleMessageDeserializer>(new PassThroughLifecycleDeserializer());
    builder.Services.AddScoped<IReceptorInvoker>(sp => new HeldStageInvoker(held, LifecycleStage.PostDistributeDetached, sp));
    using var host = builder.Build();
    await host.StartAsync();
    var services = host.Services;
    var strategy = new ImmediateWorkCoordinatorStrategy(
      coordinator: new NoOpWorkCoordinator(),
      instanceProvider: services.GetRequiredService<IServiceInstanceProvider>(),
      options: new WorkCoordinatorOptions(),
      logger: NullLogger<ImmediateWorkCoordinatorStrategy>.Instance,
      scopeFactory: services.GetRequiredService<IServiceScopeFactory>(),
      lifecycleMessageDeserializer: services.GetRequiredService<ILifecycleMessageDeserializer>(),
      tracingOptions: services.GetRequiredService<IOptionsMonitor<TracingOptions>>(),
      deferredChannel: new DeferredOutboxChannel(),
      systemEventOptions: Options.Create(new SystemEventOptions()),
      workChannelWriter: new WorkChannelWriter(),
      auditDecisionHook: null,
      detachedWork: services.GetRequiredService<DetachedLifecycleWork>());

    strategy.QueueOutboxMessage(HostShutdown.OutboxMessage());
    await strategy.FlushAsync(WorkBatchOptions.None);
    await held.Entered.Task;

    var waited = await HostShutdown.StopWaitsForAsync(host, services.GetRequiredService<DetachedLifecycleWork>(), held);

    await Assert.That(waited).IsTrue()
      .Because("the host disposes its container once the stop returns, and the detached stage still needs it");
    await Assert.That(await held.Finished.Task).IsTrue()
      .Because("the detached stage finishes against a live service provider");
  }

  /// <summary>
  /// A send or local invoke starts its ImmediateDetached stages in the background with no token: the dispatcher did not
  /// record them anywhere, so a stopping host could not wait for them.
  /// </summary>
  [Test]
  public async Task ADispatch_ImmediateDetachedInFlight_HostStopWaitsForItAsync() {
    var id = Guid.CreateVersion7();
    var held = ShutdownHeldProbe.For(id);
    var builder = _builder();
    builder.Services.AddReceptors();
    builder.Services.AddWhizbangDispatcher();
    using var host = builder.Build();
    await host.StartAsync();

    await host.Services.GetRequiredService<IDispatcher>().LocalInvokeAsync(new ShutdownHeldCommand(id));
    await held.Entered.Task;

    var waited = await HostShutdown.StopWaitsForAsync(host, host.Services.GetRequiredService<DetachedLifecycleWork>(), held);

    await Assert.That(waited).IsTrue()
      .Because("the dispatcher's detached stage resolves its receptor from a scope of the container being disposed");
    await Assert.That(await held.Finished.Task).IsTrue();
  }

  /// <summary>
  /// The receptor invoker hands a slow tag handler to the background so it cannot hold up the pipeline; the observing
  /// task was not recorded, so the host disposed the container while the handler still ran.
  /// </summary>
  [Test]
  public async Task ATagHandler_InFlight_HostStopWaitsForItAsync() {
    var held = new HeldWork();
    var builder = _builder();
    builder.Services.AddSingleton<IMessageTagProcessor>(sp => new HeldTagProcessor(held, sp));
    using var host = builder.Build();
    await host.StartAsync();
    await using var scope = host.Services.CreateAsyncScope();
    var invoker = new ReceptorInvoker(NullReceptorRegistry.Instance, scope.ServiceProvider);

    await invoker.InvokeAsync(_envelope(new ShutdownPing(Guid.CreateVersion7())), LifecycleStage.PostLifecycleInline);
    await held.Entered.Task;

    var waited = await HostShutdown.StopWaitsForAsync(host, host.Services.GetRequiredService<DetachedLifecycleWork>(), held);

    await Assert.That(waited).IsTrue()
      .Because("a tag handler still running uses services from the container the host is about to dispose");
    await Assert.That(await held.Finished.Task).IsTrue();
  }

  /// <summary>
  /// The destruction stages run their Detached half in the background, in a scope of its own, while the reap or the
  /// period close goes on; nothing recorded it.
  /// </summary>
  [Test]
  public async Task ADestructionStage_DetachedInFlight_HostStopWaitsForItAsync() {
    var held = new HeldWork();
    var builder = _builder();
    builder.Services.AddScoped<IReceptorInvoker>(sp => new HeldStageInvoker(held, LifecycleStage.PreDestructionDetached, sp));
    using var host = builder.Build();
    await host.StartAsync();
    await using var scope = host.Services.CreateAsyncScope();
    MessageEnvelope<IEvent> envelope = new() {
      MessageId = MessageId.New(),
      Payload = new ShutdownDestroyed(Guid.CreateVersion7()),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
    };

    await DestructionStageReceptors.FirePreAsync(scope.ServiceProvider, [envelope], CancellationToken.None);
    await held.Entered.Task;

    var waited = await HostShutdown.StopWaitsForAsync(host, host.Services.GetRequiredService<DetachedLifecycleWork>(), held);

    await Assert.That(waited).IsTrue()
      .Because("the detached destruction stage resolves its receptor from a scope of the container being disposed");
    await Assert.That(await held.Finished.Task).IsTrue();
  }

  /// <summary>A container without the host's record (no <c>TryAddWhizbangDefaults</c>) still runs the stage.</summary>
  [Test]
  public async Task ADestructionStage_WithoutTheHostsRecord_StillRunsAsync() {
    var held = new HeldWork();
    var services = new ServiceCollection();
    services.AddSingleton<ProviderLiveness>();
    services.AddScoped<IReceptorInvoker>(sp => new HeldStageInvoker(held, LifecycleStage.PostDestructionDetached, sp));
    await using var provider = services.BuildServiceProvider();
    MessageEnvelope<IEvent> envelope = new() {
      MessageId = MessageId.New(),
      Payload = new ShutdownDestroyed(Guid.CreateVersion7()),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
    };

    await DestructionStageReceptors.FirePostAsync(provider, [envelope], CancellationToken.None);
    await held.Entered.Task;
    held.Release();

    await Assert.That(await held.Finished.Task).IsTrue()
      .Because("with nothing to record it in, the stage runs untracked, as before");
  }

  private static MessageEnvelope<IMessage> _envelope(IMessage payload) => new() {
    MessageId = MessageId.New(),
    Payload = payload,
    Hops = [],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
  };

  private sealed record ShutdownPing(Guid Id) : IMessage;

  private sealed record ShutdownDestroyed(Guid Id) : IEvent;

  /// <summary>A tag processor whose handling holds, then checks the provider it would resolve its handlers from.</summary>
  private sealed class HeldTagProcessor(HeldWork held, IServiceProvider services) : IMessageTagProcessor {
    public async ValueTask ProcessTagsAsync(object message, Type messageType, LifecycleStage stage, IScopeContext? scope = null,
        CancellationToken ct = default) => await held.HoldAsync(services);
  }
}
