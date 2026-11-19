// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Transports;

/// <summary>
/// Branch coverage for <see cref="NamespaceRoutingTransport"/>: <see cref="NamespaceRoutingTransport.IsInitialized"/>
/// must read false while ANY namespace (default or not) is still uninitialized, and a consume-key
/// projection that reports the default key must not mirror the default namespace onto itself.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Transports/NamespaceRoutingTransport.cs</code-under-test>
[Category("Core")]
[Category("Transports")]
public class NamespaceRoutingTransportBranchCoverageTests {
  [Test]
  public async Task IsInitialized_DefaultNotInitialized_IsFalseAsync() {
    var @default = new FakeTransport();
    var bulk = new FakeTransport();
    var transport = new NamespaceRoutingTransport(@default, _map(("bulk", bulk)));
    await bulk.InitializeAsync();

    await Assert.That(transport.IsInitialized).IsFalse()
      .Because("the default namespace carries all unstamped traffic, so the composition is not ready without it");
  }

  [Test]
  public async Task IsInitialized_ANonDefaultNamespaceNotInitialized_IsFalseAsync() {
    var @default = new FakeTransport();
    var bulk = new FakeTransport();
    var transport = new NamespaceRoutingTransport(@default, _map(("bulk", bulk)));
    await @default.InitializeAsync();

    await Assert.That(transport.IsInitialized).IsFalse()
      .Because("a namespace that cannot yet publish must not let the composition read as ready");
  }

  [Test]
  public async Task SubscribeBatchAsync_ConsumeKeysReportTheDefaultKey_DoesNotMirrorTheDefaultOntoItselfAsync() {
    var @default = new FakeTransport();
    var bulk = new FakeTransport();
    IReadOnlyList<string> consumeKeys = [TransportNamespaces.DefaultKey];
    var transport = new NamespaceRoutingTransport(@default, _map(("bulk", bulk)), () => consumeKeys);

    var subscription = await transport.SubscribeBatchAsync(
      (_, _) => Task.CompletedTask, new TransportDestination("inbox.myapp.orders.commands"), new TransportBatchOptions());

    await Assert.That(@default.SubscribeCount).IsEqualTo(1)
      .Because("the default namespace is always subscribed exactly once; reporting it as a consume key must not add a second");
    await Assert.That(bulk.SubscribeCount).IsEqualTo(0);
    await Assert.That(subscription).IsSameReferenceAs(@default.LastSubscription)
      .Because("with nothing to mirror the caller gets the default transport's own subscription, unwrapped");
  }

  private static Dictionary<string, ITransport> _map(params (string Key, ITransport Transport)[] peers) =>
    peers.ToDictionary(p => p.Key, p => p.Transport, StringComparer.Ordinal);

  private sealed class FakeSubscription : ISubscription {
    public bool IsActive => true;

#pragma warning disable CS0067 // never raised: these tests do not exercise disconnects
    public event EventHandler<SubscriptionDisconnectedEventArgs>? OnDisconnected;
#pragma warning restore CS0067

    public Task PauseAsync() => Task.CompletedTask;

    public Task ResumeAsync() => Task.CompletedTask;

    public void Dispose() { }
  }

  private sealed class FakeTransport : ITransport {
    public int SubscribeCount { get; private set; }
    public ISubscription? LastSubscription { get; private set; }
    public bool IsInitialized { get; private set; }
    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe;

    public Task InitializeAsync(CancellationToken cancellationToken = default) {
      IsInitialized = true;
      return Task.CompletedTask;
    }

    public Task PublishAsync(
        IMessageEnvelope envelope,
        TransportDestination destination,
        string? envelopeType = null,
        ReadOnlyMemory<byte>? preSerializedBytes = null,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<ISubscription> SubscribeBatchAsync(
        Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler,
        TransportDestination destination,
        TransportBatchOptions batchOptions,
        CancellationToken cancellationToken = default) {
      SubscribeCount++;
      var subscription = new FakeSubscription();
      LastSubscription = subscription;
      return Task.FromResult<ISubscription>(subscription);
    }

    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(
        IMessageEnvelope requestEnvelope,
        TransportDestination destination,
        CancellationToken cancellationToken = default)
        where TRequest : notnull where TResponse : notnull => throw new NotSupportedException();
  }
}
