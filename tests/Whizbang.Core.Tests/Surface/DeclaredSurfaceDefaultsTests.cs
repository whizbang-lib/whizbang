// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Configuration;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Routing;
using Whizbang.Core.Tags;

namespace Whizbang.Core.Tests.Surface;

/// <summary>
/// Pins the documented defaults and the carried values of public option, attribute and record members
/// that no other test reads. Each one is a member whose accessor the whole-library coverage pass found
/// had never run: not dead code, but a contract nothing asserted, so a change to a documented default
/// would have gone unnoticed.
/// </summary>
/// <remarks>
/// These are deliberately assertions about the <em>documented</em> value, not round-trips. "Set it and
/// read it back" would prove nothing about an auto-property. What each test here claims is the thing the
/// XML docs promise a caller: that the default is this value, or that the member carries what the
/// constructor was given, for members where no other test establishes it.
/// </remarks>
public class DeclaredSurfaceDefaultsTests {

  [Test]
  public async Task WhizbangGuardrailsOptions_PersistInvocations_DefaultsToEnvelopeAsync() {
    var options = new WhizbangGuardrailsOptions();

    await Assert.That(options.PersistInvocations).IsEqualTo(InvocationPersistence.Envelope);
  }

  [Test]
  public async Task GenerateStreamIdAttribute_OnlyIfEmpty_DefaultsToFalseAsync() {
    var attribute = new GenerateStreamIdAttribute();

    await Assert.That(attribute.OnlyIfEmpty).IsFalse();
  }

  [Test]
  public async Task PhysicalFieldAttribute_ColumnType_DefaultsToNullAsync() {
    var attribute = new PhysicalFieldAttribute();

    await Assert.That(attribute.ColumnType).IsNull();
  }

  [Test]
  public async Task MessageTagRegistration_ExtraJson_DefaultsToNullAsync() {
    var registration = new MessageTagRegistration {
      MessageType = typeof(string),
      AttributeType = typeof(DeclaredSurfaceTagAttribute),
      Tag = "tag",
      PayloadBuilder = _ => JsonSerializer.SerializeToElement(new Dictionary<string, object?>()),
      AttributeFactory = () => new DeclaredSurfaceTagAttribute { Tag = "tag" }
    };

    await Assert.That(registration.ExtraJson).IsNull();
  }

  [Test]
  public async Task ReceptorInfo_CallerInfo_DefaultsToNullAsync() {
    var info = new ReceptorInfo(
      typeof(string),
      "receptor",
      (_, _, _, _, _) => ValueTask.FromResult<object?>(null));

    await Assert.That(info.CallerInfo).IsNull();
  }

  [Test]
  public async Task GapObservation_ExpectedCount_CarriesTheConstructedValueAsync() {
    var observation = new IntegrityRepairPolicy.GapObservation(
      OriginServiceId: Guid.Empty,
      EventType: "Event",
      TenantScope: null,
      FromCommitSequence: 1,
      ToCommitSequence: 9,
      ExpectedCount: 8,
      ActualCount: 3,
      ServiceBacklogDepth: 0,
      ConsumerLag: TimeSpan.Zero,
      ActiveLeaseCount: 0);

    await Assert.That(observation.ExpectedCount).IsEqualTo(8);
  }

  [Test]
  public async Task StreamPurgeBatch_BatchIndex_CarriesTheConstructedValueAsync() {
    var batch = new StreamPurgeBatch(
      BatchIndex: 4,
      StreamIds: [],
      Ran: false,
      RowsByTable: new Dictionary<string, long>());

    await Assert.That(batch.BatchIndex).IsEqualTo(4);
  }

  [Test]
  public async Task BacklogAgeFinding_Transport_CarriesTheConstructedValueAsync() {
    var finding = new BacklogAgeFinding(
      Entity: "entity",
      Transport: "rabbitmq",
      TransportNamespace: "ns",
      TrafficClass: "class",
      Depth: 0,
      OldestAge: TimeSpan.Zero);

    await Assert.That(finding.Transport).IsEqualTo("rabbitmq");
  }

  [Test]
  public async Task CollectiveApplyEntry_SpecKind_CarriesTheConstructedValueAsync() {
    var entry = new CollectiveApplyEntry(
      ModelType: typeof(string),
      EventType: typeof(string),
      HandlerType: typeof(string),
      MethodName: "Apply",
      ScopeHandling: CollectiveScopeHandling.Framework,
      SpecKind: CollectiveSpecKind.Linq,
      Invoker: (_, _, _) => new object());

    await Assert.That(entry.SpecKind).IsEqualTo(CollectiveSpecKind.Linq);
  }

  [Test]
  public async Task PerspectiveRegistrationInfo_FullyQualifiedName_CarriesTheConstructedValueAsync() {
    var info = new PerspectiveRegistrationInfo(
      ClrTypeName: "MyApp.Orders",
      FullyQualifiedName: "global::MyApp.Orders",
      ModelType: "global::MyApp.OrderModel",
      EventTypes: []);

    await Assert.That(info.FullyQualifiedName).IsEqualTo("global::MyApp.Orders");
  }

  [Test]
  public async Task PoisonDetectionCapabilityFinding_Detail_CarriesTheConstructedValueAsync() {
    var finding = new PoisonDetectionCapabilityFinding(
      Transport: "rabbitmq",
      Entity: "entity",
      Detail: "no broker delivery count");

    await Assert.That(finding.Detail).IsEqualTo("no broker delivery count");
  }

  [Test]
  public async Task TopologyDriftFinding_Detail_CarriesTheConstructedValueAsync() {
    var finding = new TopologyDriftFinding(
      Entity: "topic",
      ObservedSubscription: "foreign-sub",
      Detail: "a competing claim");

    await Assert.That(finding.Detail).IsEqualTo("a competing claim");
  }

  /// <summary>
  /// The default implementation names the type that did not supply one, so an operator reading the
  /// exception knows which coordinator to look at. A coordinator that predates the member reaches this
  /// body, which is why the member has a default implementation rather than being abstract.
  /// </summary>
  [Test]
  public async Task ReleaseUnstartedLeasesAsync_WithoutAnImplementation_ThrowsNamingTheCoordinatorAsync() {
    IWorkCoordinator coordinator = new CoordinatorWithoutLeaseRelease();

    var thrown = await Assert.ThrowsAsync<NotImplementedException>(
      async () => await coordinator.ReleaseUnstartedLeasesAsync(
        Guid.Empty, [], [], CancellationToken.None));

    await Assert.That(thrown).IsNotNull();
    await Assert.That(thrown!.Message).Contains(nameof(CoordinatorWithoutLeaseRelease));
  }

  /// <summary>
  /// Implements only the members <see cref="IWorkCoordinator"/> leaves abstract, so every member with a
  /// default implementation (including the one under test) resolves to the interface's own body.
  /// </summary>
  private sealed class CoordinatorWithoutLeaseRelease : IWorkCoordinator {
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default)
      => Task.CompletedTask;

    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default)
      => Task.FromResult(new WorkCoordinatorStatistics());

    public Task StoreInboxMessagesAsync(
        InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default)
      => Task.CompletedTask;

    public Task ReportPerspectiveCompletionAsync(
        PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default)
      => Task.CompletedTask;

    public Task ReportPerspectiveFailureAsync(
        PerspectiveCursorFailure failure, CancellationToken cancellationToken = default)
      => Task.CompletedTask;

    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(
        Guid streamId, string perspectiveName, CancellationToken cancellationToken = default)
      => Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  /// <summary>A concrete tag attribute, because <see cref="MessageTagAttribute"/> is abstract.</summary>
  [AttributeUsage(AttributeTargets.Class)]
  private sealed class DeclaredSurfaceTagAttribute : MessageTagAttribute { }
}
