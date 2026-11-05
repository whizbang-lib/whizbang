// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.RunControl;
using Whizbang.Core.Transports;
using Whizbang.Core.Workers;
using Whizbang.Testing.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The host-registered Service Bus consumer obeys the instance's one run permit (#2045), as the transport consumer does:
/// a close pauses every subscription before the change completes, a reopening resumes them, and a subscription
/// established while the permit is closed is paused at once. Its public pause and resume are not a second path: with
/// the run-control path registered they take it (a pause of the whole instance), and only a host without one pauses
/// the subscriptions alone.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/ServiceBusConsumerWorker.cs</code-under-test>
[Category("Component")]
[Category("RunControl")]
public class ServiceBusConsumerWorkerRunPermitTests {
  private static readonly TimeSpan _deadline = TimeSpan.FromSeconds(30);

  private static ServiceBusConsumerWorker _worker(ITransport transport, WhizbangRunPermit? permit, IServiceProvider? provider = null) {
    var scopeFactory = (provider ?? new ServiceCollection().BuildServiceProvider()).GetRequiredService<IServiceScopeFactory>();
    return new ServiceBusConsumerWorker(
      transport: transport,
      scopeFactory: scopeFactory,
      logger: NullLogger<ServiceBusConsumerWorker>.Instance,
      orderedProcessor: new OrderedStreamProcessor(logger: NullLogger<OrderedStreamProcessor>.Instance, parallelizeStreams: false),
      lifecycleMessageDeserializer: new JsonLifecycleMessageDeserializer(),
      envelopeSerializer: new EnvelopeSerializer(),
      receptorRegistry: new PermissiveReceptorRegistryQuery(),
      runtimeReceptorRegistry: NullReceptorRegistry.Instance,
      eventMarkerResolver: new EventMarkerResolver(NullMessageTypeCatalog.Instance),
      ephemeralModeResolver: new EphemeralModeResolver(NullMessageTypeCatalog.Instance),
      options: new ServiceBusConsumerOptions {
        Subscriptions = [new TopicSubscription("topic-1", "sub-1"), new TopicSubscription("topic-2", "sub-2")],
      },
      runPermit: permit);
  }

  [Test]
  public async Task AClose_PausesEverySubscriptionBeforeTheChangeCompletes_AndAReopeningResumesThemAsync() {
    var transport = new PermitTransport();
    var permit = new WhizbangRunPermit();
    var worker = _worker(transport, permit);

    await worker.StartAsync(CancellationToken.None);
    try {
      await worker.SubscriptionsReady.WaitAsync(_deadline);

      await permit.ChangeAsync(RunState.Paused, CancellationToken.None);
      await Assert.That(transport.Subscriptions.Select(s => s.IsActive)).IsEquivalentTo([false, false])
        .Because("nothing is received while the permit is closed, and the close waits for the pause");

      await permit.ChangeAsync(RunState.Running, CancellationToken.None);
      await Assert.That(transport.Subscriptions.Select(s => s.IsActive)).IsEquivalentTo([true, true]);
      await Assert.That(((IObeysRunPermit)worker).RunPermit).IsSameReferenceAs(permit);
    } finally {
      await worker.StopAsync(CancellationToken.None);
    }
  }

  [Test]
  public async Task APermitClosedBeforeSubscribing_LeavesEverySubscriptionPausedAsync() {
    var transport = new PermitTransport();
    var permit = new WhizbangRunPermit();
    await permit.ChangeAsync(RunState.Paused, CancellationToken.None);
    var worker = _worker(transport, permit);

    await worker.StartAsync(CancellationToken.None);
    try {
      await worker.SubscriptionsReady.WaitAsync(_deadline);

      await Assert.That(transport.Subscriptions.Select(s => s.IsActive)).IsEquivalentTo([false, false])
        .Because("a subscription established while the permit is closed is paused before the consumer reports ready");
    } finally {
      await worker.StopAsync(CancellationToken.None);
    }
  }

  [Test]
  public async Task ASubscriptionThatCannotBePaused_UnderAClosedPermit_IsLogged_AndTheConsumerStillStartsAsync() {
    var transport = new PermitTransport { RefusePause = true };
    var permit = new WhizbangRunPermit();
    await permit.ChangeAsync(RunState.Paused, CancellationToken.None);
    var worker = _worker(transport, permit);

    await worker.StartAsync(CancellationToken.None);
    try {
      await worker.SubscriptionsReady.WaitAsync(_deadline);

      await Assert.That(transport.PauseAttempts).IsEqualTo(2);
    } finally {
      await worker.StopAsync(CancellationToken.None);
    }
  }

  [Test]
  public async Task PauseAll_WithTheRunControlPathRegistered_TakesThePath_AndResumeAllReopensItAsync() {
    // Not a second path: the public pause halts the instance through the one path, whose close pauses the subscriptions.
    var transport = new PermitTransport();
    var services = new ServiceCollection();
    services.AddWhizbangRunControl();
    var provider = services.BuildServiceProvider();
    var path = provider.GetRequiredService<IWhizbangRunController>();
    var worker = _worker(transport, path.Permit, provider);

    await worker.StartAsync(CancellationToken.None);
    try {
      await worker.SubscriptionsReady.WaitAsync(_deadline);

      await worker.PauseAllSubscriptionsAsync();
      await Assert.That(path.HaltedFor).IsEqualTo(RunHaltReason.Pause);
      await Assert.That(transport.Subscriptions.Select(s => s.IsActive)).IsEquivalentTo([false, false]);

      await worker.ResumeAllSubscriptionsAsync();
      await Assert.That(path.HaltedFor).IsNull();
      await Assert.That(transport.Subscriptions.Select(s => s.IsActive)).IsEquivalentTo([true, true]);
    } finally {
      await worker.StopAsync(CancellationToken.None);
    }
  }

  [Test]
  public async Task PauseAll_WithAPathOverAnotherPermit_PausesOnlyThisConsumersSubscriptionsAsync() {
    // A worker built by hand with a permit of its own is not under the registered path; halting the instance would not
    // pause it, so its own subscriptions are paused directly.
    var transport = new PermitTransport();
    var services = new ServiceCollection();
    services.AddWhizbangRunControl();
    var provider = services.BuildServiceProvider();
    var worker = _worker(transport, new WhizbangRunPermit(), provider);
    var path = provider.GetRequiredService<IWhizbangRunController>();

    await worker.StartAsync(CancellationToken.None);
    try {
      await worker.SubscriptionsReady.WaitAsync(_deadline);

      await worker.PauseAllSubscriptionsAsync();
      await Assert.That(transport.Subscriptions.Select(s => s.IsActive)).IsEquivalentTo([false, false]);
      await Assert.That(path.HaltedFor).IsNull();
    } finally {
      await worker.StopAsync(CancellationToken.None);
    }
  }

  [Test]
  public async Task AfterStop_AChangeOfThePermit_TouchesNoSubscriptionAsync() {
    var transport = new PermitTransport();
    var permit = new WhizbangRunPermit();
    var worker = _worker(transport, permit);
    await worker.StartAsync(CancellationToken.None);
    await worker.SubscriptionsReady.WaitAsync(_deadline);
    await worker.StopAsync(CancellationToken.None);
    worker.Dispose();
    var pausesBefore = transport.PauseAttempts;

    await permit.ChangeAsync(RunState.Paused, CancellationToken.None);

    await Assert.That(transport.PauseAttempts).IsEqualTo(pausesBefore);
  }

  [Test]
  public async Task StopAndDispose_OfAConsumerThatNeverStarted_ReleaseNothingAndThrowNothingAsync() {
    // The consumer starts obeying the run permit when it starts. One stopped and disposed without ever starting (a host
    // that stopped first, or a worker built with no permit and never hosted) has no obedience to end: both are harmless,
    // and a later change of the permit reaches no subscription.
    var transport = new PermitTransport();
    var worker = _worker(transport, permit: null);

    await worker.StopAsync(CancellationToken.None);
    worker.Dispose();
    worker.Dispose();
    await worker.RunPermit.ChangeAsync(RunState.Paused, CancellationToken.None);

    await Assert.That(transport.Subscriptions).IsEmpty();
    await Assert.That(transport.PauseAttempts).IsEqualTo(0);
  }

  private sealed class PermitTransport : ITransport {
    private int _pauseAttempts;
    public bool RefusePause { get; init; }
    public List<PermitSubscription> Subscriptions { get; } = [];
    public int PauseAttempts => Volatile.Read(ref _pauseAttempts);
    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe;
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task PublishAsync(IMessageEnvelope envelope, TransportDestination destination, string? envelopeType = null,
      ReadOnlyMemory<byte>? preSerializedBytes = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<ISubscription> SubscribeBatchAsync(Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler,
        TransportDestination destination, TransportBatchOptions batchOptions, CancellationToken cancellationToken = default) {
      var subscription = new PermitSubscription(this);
      Subscriptions.Add(subscription);
      return Task.FromResult<ISubscription>(subscription);
    }
    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(IMessageEnvelope requestEnvelope, TransportDestination destination,
      CancellationToken cancellationToken = default) where TRequest : notnull where TResponse : notnull => throw new NotSupportedException();

    internal void CountPause() => Interlocked.Increment(ref _pauseAttempts);
  }

  private sealed class PermitSubscription(PermitTransport owner) : ISubscription {
#pragma warning disable CS0067 // required by the interface; this subscription never disconnects
    public event EventHandler<SubscriptionDisconnectedEventArgs>? OnDisconnected;
#pragma warning restore CS0067
    public bool IsActive { get; private set; } = true;
    public Task PauseAsync() {
      owner.CountPause();
      if (owner.RefusePause) {
        return Task.FromException(new InvalidOperationException("the broker refused the pause"));
      }
      IsActive = false;
      return Task.CompletedTask;
    }
    public Task ResumeAsync() {
      IsActive = true;
      return Task.CompletedTask;
    }
    public void Dispose() { }
  }
}
