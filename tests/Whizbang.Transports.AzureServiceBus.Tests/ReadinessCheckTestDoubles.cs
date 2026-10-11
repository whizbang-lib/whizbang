// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Azure.Messaging.ServiceBus;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;
using Whizbang.Core.Workers;

namespace Whizbang.Transports.AzureServiceBus.Tests;

/// <summary>
/// Test implementation of ITransport for testing readiness checks.
/// </summary>
internal sealed class TestTransport(bool isInitialized) : ITransport {

  public bool IsInitialized { get; } = isInitialized;
  public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe;

  public Task InitializeAsync(CancellationToken cancellationToken = default) {
    return Task.CompletedTask;
  }

  public Task PublishAsync(IMessageEnvelope envelope, TransportDestination destination, string? envelopeType = null, ReadOnlyMemory<byte>? preSerializedBytes = null, CancellationToken cancellationToken = default) {
    throw new NotImplementedException();
  }

  public Task<ISubscription> SubscribeBatchAsync(
    Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler,
    TransportDestination destination,
    TransportBatchOptions batchOptions,
    CancellationToken cancellationToken = default) =>
    throw new NotSupportedException();

  public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(IMessageEnvelope requestEnvelope, TransportDestination destination, CancellationToken cancellationToken = default)
    where TRequest : notnull where TResponse : notnull {
    throw new NotImplementedException();
  }
}

/// <summary>
/// Test implementation of ServiceBusClient for testing readiness checks.
/// </summary>
internal sealed class TestServiceBusClient(bool isHealthy) : ServiceBusClient {
  private readonly bool _isHealthy = isHealthy;
  public int IsClosedAccessCount { get; private set; }

  public override bool IsClosed {
    get {
      IsClosedAccessCount++;
      return !_isHealthy;
    }
  }
}
