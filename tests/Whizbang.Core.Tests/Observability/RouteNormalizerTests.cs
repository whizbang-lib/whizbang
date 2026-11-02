// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using System.Text;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Policies;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// #1725: the route id is a hash of the event's type and its hops in route form, so it is the same for every event
/// taking one path, whatever instance, line or message, and differs when the path differs.
/// </summary>
/// <docs>data/event-store#routes-and-traces</docs>
/// <code-under-test>src/Whizbang.Core/Observability/RouteNormalizer.cs</code-under-test>
[Category("Observability")]
public class RouteNormalizerTests {
  private const string TYPE = "MyApp.Orders.OrderPlaced, MyApp";
  private static readonly DateTimeOffset _sent = new(2026, 3, 1, 9, 30, 0, TimeSpan.Zero);

  private static ServiceInstanceInfo _instance(string service, string guid, int pid) => new() {
    ServiceName = service,
    InstanceId = Guid.Parse(guid),
    HostName = $"{service}-host",
    ProcessId = pid,
  };

  private static PolicyDecisionTrail _trail(params (string Policy, string Rule, bool Matched)[] decisions) => new() {
    Decisions = [.. decisions.Select(d => new PolicyDecision {
      PolicyName = d.Policy,
      Rule = d.Rule,
      Matched = d.Matched,
      Reason = $"{d.Policy} decided",
      Timestamp = _sent,
    })],
  };

  private static MessageHop _hop(
      ServiceInstanceInfo? instance = null, string topic = "orders.events", string member = "PlaceAsync",
      string file = "/src/Orders/PlaceOrderReceptor.cs", int line = 10, PolicyDecisionTrail? trail = null,
      string strategy = "Serial", HopType type = HopType.Current) => new() {
        Type = type,
        ServiceInstance = instance ?? _instance("orders", "01900000-0000-7000-8000-000000000001", 1),
        Timestamp = _sent,
        Topic = topic,
        ExecutionStrategy = strategy,
        CallerMemberName = member,
        CallerFilePath = file,
        CallerLineNumber = line,
        Trail = trail ?? _trail(("Tenancy", "present", true)),
      };

  [Test]
  public async Task CanonicalText_KeepsTheRouteFields_InOrder_AndNothingElseAsync() {
    var text = RouteNormalizer.CanonicalText(TYPE, [
      _hop(trail: _trail(("Tenancy", "present", true), ("Routing", "by-namespace", false))),
      _hop(instance: ServiceInstanceInfo.Unknown, topic: "", member: null!, file: null!, trail: new PolicyDecisionTrail(), strategy: "", type: HopType.Causation),
    ]);

    await Assert.That(text).IsEqualTo(
      "MyApp.Orders.OrderPlaced"
      + "\u001E0\u001Forders\u001Forders.events\u001FSerial\u001FPlaceOrderReceptor\u001FPlaceAsync\u001F"
      + "Tenancy\u001Dpresent\u001D1\u001CRouting\u001Dby-namespace\u001D0"
      + "\u001E1\u001F\u001F\u001F\u001F\u001F\u001F");
  }

  [Test]
  public async Task CanonicalText_NoHopsOrNull_IsTheTypeNameAloneAsync() {
    await Assert.That(RouteNormalizer.CanonicalText(TYPE, null)).IsEqualTo("MyApp.Orders.OrderPlaced");
    await Assert.That(RouteNormalizer.CanonicalText(TYPE, [])).IsEqualTo("MyApp.Orders.OrderPlaced");
    await Assert.That(RouteNormalizer.CanonicalText(TYPE, [new MessageHop { ServiceInstance = ServiceInstanceInfo.Unknown, Timestamp = _sent }]))
      .IsEqualTo("MyApp.Orders.OrderPlaced\u001E0\u001F\u001F\u001F\u001F\u001F\u001F")
      .Because("a hop with no trail has no decisions, and every missing field is empty");
  }

  [Test]
  public async Task RouteId_IgnoresAPolicyDecisionsReasonConfigurationAndTimeAsync() {
    var decided = new PolicyDecision { PolicyName = "Tenancy", Rule = "present", Matched = true, Reason = "first", Timestamp = _sent };
    var again = decided with { Reason = "second", Configuration = "something", Timestamp = _sent.AddHours(1) };

    await Assert.That(RouteNormalizer.RouteId(TYPE, [_hop(trail: new PolicyDecisionTrail { Decisions = [again] })]))
      .IsEqualTo(RouteNormalizer.RouteId(TYPE, [_hop(trail: new PolicyDecisionTrail { Decisions = [decided] })]))
      .Because("only the policy, the rule and whether it matched are part of a route");
  }

  [Test]
  public async Task RouteId_IsTheFirstSixteenBytesOfTheSha256_AsAUuidAsync() {
    var hops = new List<MessageHop> { _hop() };
    var hash = SHA256.HashData(Encoding.UTF8.GetBytes(RouteNormalizer.CanonicalText(TYPE, hops)));

    var expected = Convert.ToHexStringLower(hash, 0, 16);

    await Assert.That(RouteNormalizer.RouteId(TYPE, hops).ToString("N")).IsEqualTo(expected)
      .Because("SQL reads the same sixteen bytes as hex into a uuid, so the bytes keep their order");
  }

  [Test]
  public async Task RouteId_IsStable_AcrossInstancesLinesAndPerMessageValuesAsync() {
    var one = _hop(line: 10) with {
      PartitionIndex = 1,
      SequenceNumber = 1,
      Duration = TimeSpan.FromMilliseconds(1),
      TraceParent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01",
      CorrelationId = CorrelationId.New(),
      CausationId = MessageId.New(),
      StreamId = "a",
    };
    var otherPod = _instance("orders", "01900000-0000-7000-8000-000000000002", 2);
    var other = _hop(instance: otherPod, file: @"D:\agent\work\src\Orders\PlaceOrderReceptor.cs", line: 99) with {
      PartitionIndex = 7,
      SequenceNumber = 70,
      Duration = TimeSpan.FromSeconds(3),
      Timestamp = _sent.AddDays(3),
      TraceParent = "00-1af7651916cd43dd8448eb211c80319c-c7ad6b7169203331-01",
      CorrelationId = CorrelationId.New(),
      CausationId = MessageId.New(),
      StreamId = "b",
    };

    await Assert.That(RouteNormalizer.RouteId(TYPE, [other])).IsEqualTo(RouteNormalizer.RouteId(TYPE, [one]))
      .Because("another pod, another line, another folder and other per-message values are the same route");
    await Assert.That(RouteNormalizer.RouteId("MyApp.Orders.OrderPlaced, MyApp.V2", [one])).IsEqualTo(RouteNormalizer.RouteId(TYPE, [one]))
      .Because("the assembly is not part of the route; the type name is");
  }

  [Test]
  public async Task RouteId_Differs_WhenAServiceMemberTopicPolicyOrTypeDiffersAsync() {
    var baseline = RouteNormalizer.RouteId(TYPE, [_hop()]);

    await Assert.That(RouteNormalizer.RouteId(TYPE, [_hop(instance: _instance("billing", "01900000-0000-7000-8000-000000000003", 3))])).IsNotEqualTo(baseline);
    await Assert.That(RouteNormalizer.RouteId(TYPE, [_hop(member: "CancelAsync")])).IsNotEqualTo(baseline);
    await Assert.That(RouteNormalizer.RouteId(TYPE, [_hop(file: "/src/Orders/OtherReceptor.cs")])).IsNotEqualTo(baseline);
    await Assert.That(RouteNormalizer.RouteId(TYPE, [_hop(topic: "orders.audit")])).IsNotEqualTo(baseline);
    await Assert.That(RouteNormalizer.RouteId(TYPE, [_hop(strategy: "Parallel")])).IsNotEqualTo(baseline);
    await Assert.That(RouteNormalizer.RouteId(TYPE, [_hop(trail: _trail(("Tenancy", "present", false)))])).IsNotEqualTo(baseline);
    await Assert.That(RouteNormalizer.RouteId(TYPE, [_hop(type: HopType.Causation)])).IsNotEqualTo(baseline);
    await Assert.That(RouteNormalizer.RouteId("MyApp.Orders.OrderCancelled, MyApp", [_hop()])).IsNotEqualTo(baseline);
    await Assert.That(RouteNormalizer.RouteId(TYPE, [_hop(), _hop()])).IsNotEqualTo(baseline);
  }

  [Test]
  public async Task TypeNamePart_IsEverythingBeforeTheFirstCommaAsync() {
    await Assert.That(RouteNormalizer.TypeNamePart("A.B, Asm, Version=1")).IsEqualTo("A.B");
    await Assert.That(RouteNormalizer.TypeNamePart("A.B")).IsEqualTo("A.B");
    await Assert.That(() => RouteNormalizer.TypeNamePart(null!)).Throws<ArgumentNullException>();
    await Assert.That(() => RouteNormalizer.CanonicalText(null!, [])).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task CallerClass_IsTheFileNameWithoutFoldersOrExtensionAsync() {
    await Assert.That(RouteNormalizer.CallerClass("/src/Orders/PlaceOrderReceptor.cs")).IsEqualTo("PlaceOrderReceptor");
    await Assert.That(RouteNormalizer.CallerClass(@"C:\src\Orders\Ship.Receptor.cs")).IsEqualTo("Ship.Receptor");
    await Assert.That(RouteNormalizer.CallerClass("Makefile")).IsEqualTo("Makefile");
    await Assert.That(RouteNormalizer.CallerClass(null)).IsEqualTo(string.Empty);
    await Assert.That(RouteNormalizer.CallerClass("")).IsEqualTo(string.Empty);
  }
}
