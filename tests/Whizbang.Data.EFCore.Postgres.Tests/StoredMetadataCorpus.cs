// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Whizbang.Core.Observability;
using Whizbang.Core.Policies;
using Whizbang.Core.Security;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The shared corpus the route id and the slim form are checked against in SQL and in C# (#1725): every entry is a
/// real list of <see cref="MessageHop"/> values, serialized by the converter that writes them in production.
/// </summary>
internal static class StoredMetadataCorpus {
  /// <summary>The options the hops are written with: the generated contexts plus the id converters.</summary>
  public static readonly JsonSerializerOptions Options = JsonContextRegistry.CreateCombinedOptions();

  public static readonly DateTimeOffset Sent = new(2026, 3, 1, 9, 30, 0, TimeSpan.Zero);

  public static readonly ServiceInstanceInfo Orders = new() {
    ServiceName = "orders",
    InstanceId = Guid.Parse("01900000-0000-7000-8000-0000000000a1"),
    HostName = "orders-host-1",
    ProcessId = 101,
  };

  public static readonly ServiceInstanceInfo OrdersOtherPod = new() {
    ServiceName = "orders",
    InstanceId = Guid.Parse("01900000-0000-7000-8000-0000000000a2"),
    HostName = "orders-host-2",
    ProcessId = 202,
  };

  public static readonly ServiceInstanceInfo Billing = new() {
    ServiceName = "billing-ünïcode",
    InstanceId = Guid.Parse("01900000-0000-7000-8000-0000000000b1"),
    HostName = "billing-host",
    ProcessId = 303,
  };

  public static readonly MessageId Cause = MessageId.From(Guid.Parse("01900000-0000-7000-8000-0000000000c1"));
  public static readonly CorrelationId Correlation = CorrelationId.FromExternal(Guid.Parse("01900000-0000-7000-8000-0000000000c2"));

  public sealed record Entry(string Name, string EventType, List<MessageHop> Hops);

  public static PolicyDecisionTrail Trail(params (string Policy, string Rule, bool Matched)[] decisions) => new() {
    Decisions = [.. decisions.Select(d => new PolicyDecision {
      PolicyName = d.Policy, Rule = d.Rule, Matched = d.Matched, Reason = $"{d.Policy} said {d.Matched}", Timestamp = Sent,
    })],
  };

  private static ScopeContext _scope(string tenant, string? user = null, string[]? roles = null,
      Dictionary<string, string>? claims = null, string? actual = null) => new() {
        Scope = new Whizbang.Core.Lenses.PerspectiveScope { TenantId = tenant, UserId = user },
        Roles = new HashSet<string>(roles ?? []),
        Permissions = new HashSet<Permission>(),
        SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
        Claims = claims ?? [],
        ActualPrincipal = actual,
      };

  /// <summary>A Current hop carrying every field a hop can carry.</summary>
  public static MessageHop RichHop(ServiceInstanceInfo instance, int line = 41, string traceParent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01") => new() {
    Type = HopType.Current,
    ServiceInstance = instance,
    Timestamp = Sent,
    Topic = "orders.events",
    StreamId = "order-17",
    PartitionIndex = 4,
    SequenceNumber = 9,
    ExecutionStrategy = "SerialExecutor",
    CorrelationId = Correlation,
    CausationId = Cause,
    TraceParent = traceParent,
    CallerMemberName = "PlaceAsync",
    CallerFilePath = "/build/agent/src/Orders/PlaceOrderReceptor.cs",
    CallerLineNumber = line,
    Duration = TimeSpan.FromMilliseconds(12.5),
    Metadata = new Dictionary<string, JsonElement> { ["attempt"] = JsonSerializer.SerializeToElement(1, Options.GetTypeInfo(typeof(int))) },
    Trail = Trail(("Tenancy", "tenant-present", true), ("Routing", "topic-by-namespace", false)),
    Scope = ScopeDelta.CreateDelta(null, _scope("tenant-a", "user-a", ["reader"])),
  };

  public static IReadOnlyList<Entry> Entries { get; } = [
    new("no-hops", "MyApp.Orders.OrderPlaced, MyApp", []),
    new("one-rich-hop", "MyApp.Orders.OrderPlaced, MyApp", [RichHop(Orders)]),
    new("unknown-instance", "MyApp.Orders.OrderPlaced, MyApp", [
      new MessageHop { ServiceInstance = ServiceInstanceInfo.Unknown, Timestamp = Sent, CorrelationId = Correlation }]),
    new("windows-caller-no-extension", "MyApp.Orders+Nested.Shipped, MyApp", [
      new MessageHop {
        ServiceInstance = Billing, Timestamp = Sent, CallerFilePath = @"C:\src\Billing\ShipReceptor.cs", CallerMemberName = "Ship",
        Topic = "billing|topic", ExecutionStrategy = "Parallel",
      },
      new MessageHop { ServiceInstance = Billing, Timestamp = Sent.AddSeconds(1), CallerFilePath = "Makefile" },
    ]),
    new("causation-and-folded-scopes", "MyApp.Orders.OrderPaid, MyApp", [
      RichHop(Orders),
      new MessageHop {
        Type = HopType.Causation, ServiceInstance = Billing, Timestamp = Sent.AddSeconds(-5), CausationType = "MyApp.Orders.PayOrder",
        Scope = ScopeDelta.CreateDelta(null, _scope("causation-tenant")),
      },
      new MessageHop {
        ServiceInstance = OrdersOtherPod, Timestamp = Sent.AddSeconds(2), Topic = "orders.events",
        Scope = ScopeDelta.CreateDelta(
          _scope("tenant-a", "user-a", ["reader"]),
          _scope("tenant-a", "user-b", ["reader", "writer"], new Dictionary<string, string> { ["sub"] = "user-b" }, actual: "svc")),
      },
      new MessageHop {
        ServiceInstance = OrdersOtherPod, Timestamp = Sent.AddSeconds(3),
        Scope = ScopeDelta.CreateDelta(
          _scope("tenant-a", "user-b", ["reader", "writer"], new Dictionary<string, string> { ["sub"] = "user-b" }, actual: "svc"),
          _scope("tenant-a", "user-b", ["writer", "auditor"], new Dictionary<string, string> { ["iss"] = "issuer" }, actual: "svc")),
      },
    ]),
    new("no-scope-no-trace", "Global.Event", [
      new MessageHop { ServiceInstance = Orders, Timestamp = Sent, Trail = Trail(("Only", "rule", false)) }]),
  ];

  /// <summary>The hops as the hop writer writes them.</summary>
  public static string HopsJson(IReadOnlyList<MessageHop> hops) =>
    JsonSerializer.Serialize([.. hops], (System.Text.Json.Serialization.Metadata.JsonTypeInfo<List<MessageHop>>)Options.GetTypeInfo(typeof(List<MessageHop>)));

  /// <summary>Reads a stored metadata document as the event store reads it.</summary>
  public static EnvelopeMetadata Read(string json) =>
    (EnvelopeMetadata)JsonSerializer.Deserialize(json, Options.GetTypeInfo(typeof(EnvelopeMetadata)))!;

  /// <summary>Every field of a folded scope, so two scopes compare as values.</summary>
  public static string Describe(IScopeContext? scope) =>
    scope is null
      ? "none"
      : string.Join("|",
          scope.Scope.TenantId, scope.Scope.UserId, scope.Scope.CustomerId, scope.Scope.OrganizationId,
          string.Join(",", scope.Roles.Order(StringComparer.Ordinal)),
          string.Join(",", scope.Permissions.Select(p => p.Value).Order(StringComparer.Ordinal)),
          string.Join(",", scope.SecurityPrincipals.Select(p => p.Value).Order(StringComparer.Ordinal)),
          string.Join(",", scope.Claims.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => $"{c.Key}={c.Value}")),
          scope.ActualPrincipal, scope.EffectivePrincipal, scope.ContextType);
}
