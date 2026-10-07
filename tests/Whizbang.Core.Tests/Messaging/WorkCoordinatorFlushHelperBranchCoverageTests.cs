// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Branch coverage for the partition count <see cref="WorkCoordinatorFlushHelper.ExecuteFlushAsync"/>
/// stores rows under. A count that is not positive is never used: a direct coordinator falls back
/// to the documented 10000, and a scoped one first skips a non-positive claim-worker count, then
/// takes the work-coordinator count when that is positive and 10000 when it is not.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/WorkCoordinatorFlushHelper.cs</code-under-test>
public class WorkCoordinatorFlushHelperBranchCoverageTests {

  [Test]
  public async Task DirectCoordinator_NonPositiveConfiguredCount_StoresUnderTheDefaultAsync() {
    var coordinator = new CapturingWorkCoordinator();

    await WorkCoordinatorFlushHelper.ExecuteFlushAsync(
      _ctx(coordinator, scopeFactory: null, new WorkCoordinatorOptions { PartitionCount = 0 }), default);

    await Assert.That(coordinator.LastStoredPartitionCount).IsEqualTo(10000)
      .Because("zero partitions would place every row in no partition at all, so the default stands in");
  }

  [Test]
  public async Task ScopedCoordinator_NonPositiveClaimCount_FallsBackToTheWorkCoordinatorCountAsync() {
    var coordinator = new CapturingWorkCoordinator();
    await using var provider = _scopedProvider(coordinator, claimPartitionCount: 0);

    await WorkCoordinatorFlushHelper.ExecuteFlushAsync(
      _ctx(null, provider.GetRequiredService<IServiceScopeFactory>(), new WorkCoordinatorOptions { PartitionCount = 7 }), default);

    await Assert.That(coordinator.LastStoredPartitionCount).IsEqualTo(7)
      .Because("a claim-worker count that is not positive is no override; the work-coordinator count applies");
  }

  [Test]
  public async Task ScopedCoordinator_NoPositiveCountAnywhere_StoresUnderTheDefaultAsync() {
    var coordinator = new CapturingWorkCoordinator();
    await using var provider = _scopedProvider(coordinator, claimPartitionCount: 0);

    await WorkCoordinatorFlushHelper.ExecuteFlushAsync(
      _ctx(null, provider.GetRequiredService<IServiceScopeFactory>(), new WorkCoordinatorOptions { PartitionCount = 0 }), default);

    await Assert.That(coordinator.LastStoredPartitionCount).IsEqualTo(10000)
      .Because("with neither count positive, the default is the only usable partition count");
  }

  private static ServiceProvider _scopedProvider(IWorkCoordinator coordinator, int claimPartitionCount) {
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddSingleton(coordinator);
    services.AddOptions<ClaimWorkerOptions>().Configure(o => o.PartitionCount = claimPartitionCount);
    return services.BuildServiceProvider();
  }

  private static FlushContext _ctx(IWorkCoordinator? coordinator, IServiceScopeFactory? scopeFactory, WorkCoordinatorOptions options) => new(
    coordinator,
    scopeFactory,
    new FakeInstanceProvider(),
    options,
    [_outbox()],
    [],
    [],
    [],
    [],
    [],
    LifecycleMessageDeserializer: null,
    Logger: null,
    TracingOptions: null,
    LifecycleMetrics: null,
    WorkChannelWriter: null,
    PendingAuditMessages: null,
    SkipLifecycle: true);

  private static OutboxMessage _outbox() {
    var id = Guid.CreateVersion7();
    return new OutboxMessage {
      MessageId = id,
      Destination = "test-topic",
      Envelope = new TestEnvelope { MessageId = MessageId.From(id), Hops = [] },
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Object, System.Private.CoreLib]], Whizbang.Core",
      StreamId = Guid.CreateVersion7(),
      IsEvent = true,
      MessageType = "TestMessage, TestAssembly",
      Metadata = new EnvelopeMetadata {
        MessageId = MessageId.From(id),
        Hops = []
      }
    };
  }

  private sealed class TestEnvelope : IMessageEnvelope<JsonElement> {
    public int Version => 1;
    public MessageDispatchContext DispatchContext { get; } = new() { Mode = DispatchModes.Local, Source = MessageSource.Local };
    public required MessageId MessageId { get; init; }
    public required List<MessageHop> Hops { get; init; }
    public JsonElement Payload { get; init; } = JsonDocument.Parse("{}").RootElement;
    object IMessageEnvelope.Payload => Payload;
    public void AddHop(MessageHop hop) => Hops.Add(hop);
    public DateTimeOffset GetMessageTimestamp() => Hops.Count > 0 ? Hops[0].Timestamp : DateTimeOffset.UtcNow;
    public CorrelationId? GetCorrelationId() => Hops.Count > 0 ? Hops[0].CorrelationId : null;
    public MessageId? GetCausationId() => Hops.Count > 0 ? Hops[0].CausationId : null;
    public JsonElement? GetMetadata(string key) => null;
    public SecurityContext? GetCurrentSecurityContext() => null;
    public ScopeContext? GetCurrentScope() => null;
  }

  private sealed class FakeInstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = Guid.CreateVersion7();
    public string ServiceName => "TestService";
    public string HostName => "test-host";
    public int ProcessId => 12345;
    public ServiceInstanceInfo ToInfo() => new() {
      InstanceId = InstanceId,
      ServiceName = ServiceName,
      HostName = HostName,
      ProcessId = ProcessId
    };
  }

  private sealed class CapturingWorkCoordinator : IWorkCoordinator {
    public int LastStoredPartitionCount { get; private set; }

    public Task StoreOutboxMessagesAsync(OutboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) {
      LastStoredPartitionCount = partitionCount;
      return Task.CompletedTask;
    }

    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) {
      LastStoredPartitionCount = partitionCount;
      return Task.CompletedTask;
    }

    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) =>
      Task.FromResult<PerspectiveCursorInfo?>(null);
  }
}
