// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions.Extensions;
using TUnit.Assertions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Resilience;
using Whizbang.Core.Routing;
using Whizbang.Core.RunControl;
using Whizbang.Core.Security;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Core;
using Whizbang.Testing.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The transport consumer obeys the instance's one run permit (#2045). It is called back by its transport rather than
/// looping, so it obeys by acting on each change: a close pauses every subscription before the change completes (the
/// run-control path writes its phase only after that), and a reopening resumes them. A permit already closed when the
/// consumer subscribes leaves the subscriptions paused before the consumer reports them ready.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/TransportConsumerWorker.cs</code-under-test>
[Category("Component")]
[Category("RunControl")]
public class TransportConsumerWorkerRunPermitTests {
  private static readonly TimeSpan _deadline = TimeSpan.FromSeconds(30);

  private static TransportConsumerWorker _worker(ITransport transport, WhizbangRunPermit permit) {
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("topic1"));
    options.Destinations.Add(new TransportDestination("topic2"));
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    var provider = services.BuildServiceProvider();
    return new TransportConsumerWorker(
      transport: transport,
      options: options,
      resilienceOptions: new SubscriptionResilienceOptions(),
      scopeFactory: provider.GetRequiredService<IServiceScopeFactory>(),
      jsonOptions: new JsonSerializerOptions(),
      orderedProcessor: new OrderedStreamProcessor(logger: NullLogger<OrderedStreamProcessor>.Instance, parallelizeStreams: false),
      metrics: null,
      logger: NullLogger<TransportConsumerWorker>.Instance,
      serviceInstanceProvider: new Whizbang.Core.Observability.ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()),
      routingOptions: Options.Create(new RoutingOptions()),
      workChannelWriter: new WorkChannelWriter(),
      claimWorkerOptions: Options.Create(new ClaimWorkerOptions()),
      receptorRegistry: new PermissiveReceptorRegistryQuery(),
      runtimeReceptorRegistry: NullReceptorRegistry.Instance,
      ephemeralModeResolver: new EphemeralModeResolver(NullMessageTypeCatalog.Instance),
      eventMarkerResolver: new EventMarkerResolver(NullMessageTypeCatalog.Instance),
      controlClass: Options.Create(new ControlClassOptions()),
      runPermit: permit);
  }

  [Test]
  public async Task AClose_PausesEverySubscriptionBeforeTheChangeCompletes_AndAReopeningResumesThemAsync() {
    var transport = new FakeTransport();
    var permit = new WhizbangRunPermit();
    var worker = _worker(transport, permit);

    await worker.StartAsync(CancellationToken.None);
    try {
      await worker.SubscriptionsReady.WaitAsync(_deadline);

      await permit.ChangeAsync(RunState.Paused, CancellationToken.None);
      foreach (var subscription in transport.Subscriptions) {
        await Assert.That(subscription.PauseCallCount).IsEqualTo(1)
          .Because("nothing is received while the permit is closed, and the close waits for the pause");
      }

      await permit.ChangeAsync(RunState.Running, CancellationToken.None);
      foreach (var subscription in transport.Subscriptions) {
        await Assert.That(subscription.ResumeCallCount).IsEqualTo(1);
      }
      await Assert.That(((IObeysRunPermit)worker).RunPermit).IsSameReferenceAs(permit);
    } finally {
      await worker.StopAsync(CancellationToken.None);
    }
  }

  [Test]
  public async Task APermitClosedBeforeSubscribing_LeavesTheSubscriptionsPausedAsync() {
    var transport = new FakeTransport();
    var permit = new WhizbangRunPermit();
    await permit.ChangeAsync(RunState.Paused, CancellationToken.None);
    var worker = _worker(transport, permit);

    await worker.StartAsync(CancellationToken.None);
    try {
      await worker.SubscriptionsReady.WaitAsync(_deadline);

      await Assert.That(transport.Subscriptions).Count().IsEqualTo(2);
      foreach (var subscription in transport.Subscriptions) {
        await Assert.That(subscription.IsActive).IsFalse()
          .Because("a consumer started under a closed permit receives nothing until it reopens");
      }
    } finally {
      await worker.StopAsync(CancellationToken.None);
    }
  }

  [Test]
  public async Task AfterDispose_AChangeOfThePermit_TouchesNoSubscriptionAsync() {
    var transport = new FakeTransport();
    var permit = new WhizbangRunPermit();
    var worker = _worker(transport, permit);
    await worker.StartAsync(CancellationToken.None);
    await worker.SubscriptionsReady.WaitAsync(_deadline);
    await worker.StopAsync(CancellationToken.None);
    worker.Dispose();

    await permit.ChangeAsync(RunState.Paused, CancellationToken.None);

    foreach (var subscription in transport.Subscriptions) {
      await Assert.That(subscription.PauseCallCount).IsEqualTo(0);
    }
  }

  [Test]
  public async Task ASubscriptionThatCannotBePaused_UnderAClosedPermit_IsLogged_AndTheConsumerStillStartsAsync() {
    var transport = new UnpausableTransport();
    var permit = new WhizbangRunPermit();
    await permit.ChangeAsync(RunState.Paused, CancellationToken.None);
    var worker = _worker(transport, permit);

    await worker.StartAsync(CancellationToken.None);
    try {
      await worker.SubscriptionsReady.WaitAsync(_deadline);
      await Assert.That(transport.PauseAttempts).IsEqualTo(2)
        .Because("each subscription established under the closed permit was asked to pause; the failure is logged, not thrown");
    } finally {
      await worker.StopAsync(CancellationToken.None);
    }
  }

  private sealed class UnpausableTransport : ITransport {
    private int _pauseAttempts;
    public int PauseAttempts => Volatile.Read(ref _pauseAttempts);
    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe;
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task PublishAsync(IMessageEnvelope envelope, TransportDestination destination, string? envelopeType = null,
      ReadOnlyMemory<byte>? preSerializedBytes = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<ISubscription> SubscribeBatchAsync(Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler,
        TransportDestination destination, TransportBatchOptions batchOptions, CancellationToken cancellationToken = default) =>
      Task.FromResult<ISubscription>(new Unpausable(this));
    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(IMessageEnvelope requestEnvelope, TransportDestination destination,
      CancellationToken cancellationToken = default) where TRequest : notnull where TResponse : notnull => throw new NotSupportedException();

    private sealed class Unpausable(UnpausableTransport owner) : ISubscription {
#pragma warning disable CS0067 // required by the interface; this subscription never disconnects
      public event EventHandler<SubscriptionDisconnectedEventArgs>? OnDisconnected;
#pragma warning restore CS0067
      public bool IsActive => true;
      public Task PauseAsync() {
        Interlocked.Increment(ref owner._pauseAttempts);
        return Task.FromException(new InvalidOperationException("the broker refused the pause"));
      }
      public Task ResumeAsync() => Task.CompletedTask;
      public void Dispose() { }
    }
  }
}
