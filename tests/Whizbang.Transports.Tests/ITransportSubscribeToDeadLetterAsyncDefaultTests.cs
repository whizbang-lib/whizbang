#pragma warning disable CA1707

using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;
using Whizbang.Core.Workers;

namespace Whizbang.Transports.Tests;

/// <summary>
/// Pins the <see cref="ITransport.SubscribeToDeadLetterAsync"/> default
/// implementation: when a transport doesn't override the method, calls MUST
/// throw <see cref="NotSupportedException"/> so callers (notably
/// <c>TransportDeadLetterDrainWorker</c>) can detect the limitation and fall
/// back to the polling path.
/// </summary>
/// <docs>messaging/transports/dlq-push-subscription</docs>
public class ITransportSubscribeToDeadLetterAsyncDefaultTests {

  [Test]
  public async Task DefaultImplementation_ThrowsNotSupportedAsync() {
    ITransport transport = new _legacyTransport();

    await Assert.That(async () => await transport.SubscribeToDeadLetterAsync(
        handler: (_, _) => Task.CompletedTask,
        destination: new TransportDestination("anywhere"),
        cancellationToken: CancellationToken.None))
      .Throws<NotSupportedException>()
      .Because("Transports that haven't been updated MUST surface a clear NotSupportedException so the drain worker can detect the legacy path and continue polling — silent no-ops would drop DLQ messages.");
  }

  [Test]
  public async Task DefaultImplementation_ExceptionMessageNamesTransportTypeAsync() {
    ITransport transport = new _legacyTransport();

    try {
      await transport.SubscribeToDeadLetterAsync(
        (_, _) => Task.CompletedTask,
        new TransportDestination("anywhere"),
        CancellationToken.None);
      throw new InvalidOperationException("Expected exception");
    } catch (NotSupportedException ex) {
      await Assert.That(ex.Message).Contains(nameof(_legacyTransport))
        .Because("The default exception MUST name the transport type so operator-facing logs/CI surfaces immediately tell ops which transport needs the push-DLQ implementation.");
      await Assert.That(ex.Message).Contains("polling")
        .Because("The exception MUST mention the polling fallback so callers reading the log know the system isn't broken — just running on the legacy cadence.");
    }
  }

  /// <summary>Transport that doesn't override SubscribeToDeadLetterAsync — exercises the default.</summary>
  private sealed class _legacyTransport : ITransport {
    public bool IsInitialized => true;
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public TransportCapabilities Capabilities => TransportCapabilities.None;
    public Task PublishAsync(IMessageEnvelope envelope, TransportDestination destination, string? envelopeType = null, ReadOnlyMemory<byte>? preSerializedBytes = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<ISubscription> SubscribeBatchAsync(Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler, TransportDestination destination, TransportBatchOptions batchOptions, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(IMessageEnvelope requestEnvelope, TransportDestination destination, CancellationToken cancellationToken = default) where TRequest : notnull where TResponse : notnull => throw new NotImplementedException();
  }
}
