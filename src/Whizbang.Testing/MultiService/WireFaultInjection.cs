using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;
using Whizbang.Core.Workers;

namespace Whizbang.Testing.MultiService;

/// <summary>
/// Per-service delivery-fault registry for the multi-service harness — simulates the outage class
/// stream integrity exists to repair: the broker delivered, but ONE service never received. Faults
/// are registered against a service name and consulted at that service's receive seam
/// (<see cref="ServiceWireTap"/>); publishing is never affected, so every other subscriber still
/// receives the message — a per-consumer delivery loss, not a publish failure. Register via
/// <see cref="MultiServiceHarness.SuppressDeliveries"/>; dispose the registration to lift the fault.
/// </summary>
/// <docs>testing/multi-service-harness</docs>
/// <tests>tests/Whizbang.Core.Tests/MultiService/StreamIntegrityRedeliveryE2ETests.cs</tests>
public sealed class WireFaultInjector {
  private readonly Lock _lock = new();
  private readonly Dictionary<string, List<Registration>> _faults = [];

  internal bool ShouldDrop(string serviceName, TransportMessage message) {
    lock (_lock) {
      if (!_faults.TryGetValue(serviceName, out var registrations)) {
        return false;
      }
      foreach (var registration in registrations) {
        if (registration.Matches(message)) {
          return true;
        }
      }
      return false;
    }
  }

  internal IDisposable Register(string serviceName, Func<TransportMessage, bool> shouldDrop) {
    var registration = new Registration(this, serviceName, shouldDrop);
    lock (_lock) {
      if (!_faults.TryGetValue(serviceName, out var registrations)) {
        registrations = [];
        _faults[serviceName] = registrations;
      }
      registrations.Add(registration);
    }
    return registration;
  }

  private void _remove(Registration registration) {
    lock (_lock) {
      if (_faults.TryGetValue(registration.ServiceName, out var registrations)) {
        registrations.Remove(registration);
      }
    }
  }

  private sealed class Registration(WireFaultInjector owner, string serviceName, Func<TransportMessage, bool> shouldDrop) : IDisposable {
    public string ServiceName { get; } = serviceName;
    public bool Matches(TransportMessage message) => shouldDrop(message);
    public void Dispose() => owner._remove(this);
  }
}

/// <summary>
/// The per-service receive seam over the shared wire: delegates everything to the shared transport
/// but filters DELIVERED batches through the harness's <see cref="WireFaultInjector"/>, so an
/// injected fault drops messages before this service's consumer worker ever sees them — every
/// other service's deliveries are untouched.
/// </summary>
internal sealed class ServiceWireTap(ITransport inner, string serviceName, WireFaultInjector faults) : ITransport {
  public bool IsInitialized => inner.IsInitialized;
  public TransportCapabilities Capabilities => inner.Capabilities;
  public Task InitializeAsync(CancellationToken cancellationToken = default) => inner.InitializeAsync(cancellationToken);

  public Task PublishAsync(
      IMessageEnvelope envelope,
      TransportDestination destination,
      string? envelopeType = null,
      ReadOnlyMemory<byte>? preSerializedBytes = null,
      CancellationToken cancellationToken = default) =>
    inner.PublishAsync(envelope, destination, envelopeType, preSerializedBytes, cancellationToken);

  public Task<ISubscription> SubscribeBatchAsync(
      Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler,
      TransportDestination destination,
      TransportBatchOptions batchOptions,
      CancellationToken cancellationToken = default) =>
    inner.SubscribeBatchAsync(async (messages, ct) => {
      List<TransportMessage>? kept = null;
      foreach (var message in messages) {
        if (!faults.ShouldDrop(serviceName, message)) {
          (kept ??= []).Add(message);
        }
      }
      if (kept is { Count: > 0 }) {
        await batchHandler(kept, ct).ConfigureAwait(false);
      }
    }, destination, batchOptions, cancellationToken);

  public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(
      IMessageEnvelope requestEnvelope,
      TransportDestination destination,
      CancellationToken cancellationToken = default)
      where TRequest : notnull where TResponse : notnull =>
    inner.SendAsync<TRequest, TResponse>(requestEnvelope, destination, cancellationToken);
}
