using Whizbang.Core.Observability;
using Whizbang.Core.Transports;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Transports;

/// <summary>
/// Coverage for <see cref="NamespaceRoutingTransport"/>'s request/response routing and the two
/// disposal branches a plain <see cref="IAsyncDisposable"/> namespace transport never exercises: a
/// transport that implements only <see cref="IDisposable"/>, and one that implements neither.
/// Routing by namespace decides which transport a message leaves by — a wrong answer here sends a
/// request/response call to the wrong broker namespace and the caller waits on a reply that will
/// never arrive on that connection.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Transports/NamespaceRoutingTransport.cs</code-under-test>
[Category("Core")]
[Category("Transports")]
public class NamespaceRoutingTransportCoverageTests {
  private sealed class _testRequest;
  private sealed class _testResponse;

  [Test]
  public async Task SendAsync_StampedDestination_RoutesToThatNamespacesTransportAsync() {
    var @default = new _recordingSendTransport();
    var bulk = new _recordingSendTransport();
    var transport = new NamespaceRoutingTransport(
      @default, new Dictionary<string, ITransport>(StringComparer.Ordinal) { ["bulk"] = bulk });
    var destination = TransportNamespaces.Stamp(new TransportDestination("coverage-topic"), "bulk");

    await transport.SendAsync<_testRequest, _testResponse>(null!, destination);

    await Assert.That(bulk.SendCalls.Count).IsEqualTo(1);
    await Assert.That(@default.SendCalls.Count).IsEqualTo(0)
      .Because("a stamped destination must route to its namespace's transport, not the default");
  }

  // If disposal stopped at (or threw on) a namespace transport with no async disposal contract,
  // every namespace transport ordered after it would leak its connection forever.
  [Test]
  public async Task DisposeAsync_TransportImplementingOnlyIDisposable_CallsDisposeAsync() {
    var syncDisposable = new _syncDisposableTransport();
    var transport = new NamespaceRoutingTransport(syncDisposable, new Dictionary<string, ITransport>());

    await transport.DisposeAsync();

    await Assert.That(syncDisposable.DisposeCount).IsEqualTo(1);
  }

  // An in-process/no-op style transport implements neither disposal interface; disposal must still
  // complete cleanly for it rather than fail (or skip the rest of) the composition's shutdown.
  [Test]
  public async Task DisposeAsync_TransportsImplementingNeitherDisposable_CompletesWithoutThrowingAsync() {
    var transport = new NamespaceRoutingTransport(
      new _plainTransport(), new Dictionary<string, ITransport>(StringComparer.Ordinal) { ["bulk"] = new _plainTransport() });

    await Assert.That(async () => await transport.DisposeAsync()).ThrowsNothing()
      .Because("a namespace transport with no disposal contract must not block disposal of the composition");
  }

  private sealed class _recordingSendTransport : ITransport {
    public List<TransportDestination> SendCalls { get; } = [];
    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => TransportCapabilities.RequestResponse;
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

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
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(
        IMessageEnvelope requestEnvelope,
        TransportDestination destination,
        CancellationToken cancellationToken = default)
        where TRequest : notnull where TResponse : notnull {
      SendCalls.Add(destination);
      return Task.FromResult<IMessageEnvelope>(null!);
    }
  }

  private sealed class _syncDisposableTransport : ITransport, IDisposable {
    public int DisposeCount { get; private set; }
    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe;
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

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
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(
        IMessageEnvelope requestEnvelope,
        TransportDestination destination,
        CancellationToken cancellationToken = default)
        where TRequest : notnull where TResponse : notnull => throw new NotSupportedException();

    public void Dispose() => DisposeCount++;
  }

  private sealed class _plainTransport : ITransport {
    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe;
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

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
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(
        IMessageEnvelope requestEnvelope,
        TransportDestination destination,
        CancellationToken cancellationToken = default)
        where TRequest : notnull where TResponse : notnull => throw new NotSupportedException();
  }
}
