// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Branch coverage for <see cref="StreamRedeliveryRequester"/> built without a clock (the DI
/// registration supplies none): the request's hop is stamped from the system clock, so its
/// timestamp falls between the moments read just before and just after the call.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/StreamRedeliveryRequester.cs</code-under-test>
[Category("Unit")]
[Category("StreamIntegrity")]
public class StreamRedeliveryRequesterBranchCoverageTests {

  [Test]
  public async Task RequestAsync_NoTimeProvider_StampsTheHopFromTheSystemClockAsync() {
    var transport = new CaptureTransport();
    var services = new ServiceCollection();
    services.AddSingleton<ITransport>(transport);
    services.AddSingleton<IEnvelopeSerializer>(new EnvelopeSerializer(JsonContextRegistry.CreateCombinedOptions()));
    services.AddSingleton<IServiceInstanceProvider>(new InstanceProvider("receiver-svc"));
    await using var provider = services.BuildServiceProvider();
    var requester = new StreamRedeliveryRequester(
      provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<StreamRedeliveryRequester>.Instance);

    var before = DateTimeOffset.UtcNow;
    var receipt = await requester.RequestAsync(new StreamRedeliveryRequest {
      OriginService = "origin-svc",
      StreamIds = [TrackedGuid.New().Value],
      OriginRequestTopic = "origin.requests",
      ReplyTopic = "replies",
    });
    var after = DateTimeOffset.UtcNow;

    await Assert.That(receipt.Requests).IsEqualTo(1);
    await Assert.That(transport.Published.Count).IsEqualTo(1);
    var stamped = transport.Published[0].Hops[0].Timestamp;
    await Assert.That(stamped).IsGreaterThanOrEqualTo(before)
      .Because("with no clock supplied the hop is stamped by the system clock at the time of the request");
    await Assert.That(stamped).IsLessThanOrEqualTo(after);
  }

  private sealed class CaptureTransport : ITransport {
    public List<IMessageEnvelope> Published { get; } = [];
    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe;
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task PublishAsync(IMessageEnvelope envelope, TransportDestination destination, string? envelopeType = null, ReadOnlyMemory<byte>? preSerializedBytes = null, CancellationToken cancellationToken = default) {
      Published.Add(envelope);
      return Task.CompletedTask;
    }
    public Task<ISubscription> SubscribeBatchAsync(Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler, TransportDestination destination, TransportBatchOptions batchOptions, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(IMessageEnvelope requestEnvelope, TransportDestination destination, CancellationToken cancellationToken = default) where TRequest : notnull where TResponse : notnull => throw new NotSupportedException();
  }

  private sealed class InstanceProvider(string serviceName) : IServiceInstanceProvider {
    public Guid InstanceId { get; } = TrackedGuid.New().Value;
    public string ServiceName => serviceName;
    public string HostName => "test-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() { InstanceId = InstanceId, ServiceName = ServiceName, HostName = HostName, ProcessId = ProcessId };
  }
}
