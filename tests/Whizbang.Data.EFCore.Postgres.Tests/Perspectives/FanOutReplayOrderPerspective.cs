// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

#pragma warning disable CA1859 // the fan-out Apply returns the spec interface, as a generated invoker expects

using System.Linq.Expressions;
using Whizbang.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// The #2275 example as a perspective with a generated runner and table: an order row that copies its customer's name, and
/// the customer's renames that fan out to it (#2621). The fan-out itself is declared by the hand-written entry
/// <see cref="FanOutReplayOrderFanOut.Renamed"/>, which describes exactly what the generator emits for an
/// <c>IPerspectiveFanOutFor</c>, so no generated fan-out registry reaches the other hosts this assembly builds.
/// </summary>
public class FanOutReplayOrderPerspective :
    IPerspectiveFor<FanOutReplayOrder, FanOutReplayOrderPlaced>,
    IPerspectiveFor<FanOutReplayOrder, FanOutReplayOrderMoved>,
    IPerspectiveFor<FanOutReplayOrder, FanOutReplayOrderShipped> {

  /// <summary>An order takes its customer and the name the producer copied.</summary>
  public FanOutReplayOrder Apply(FanOutReplayOrder currentData, FanOutReplayOrderPlaced @event) => new() {
    Id = @event.OrderId,
    CustomerId = @event.CustomerId,
    CustomerName = @event.CustomerName,
    Status = "Placed",
  };

  /// <summary>An order moved to another customer takes that customer and the name the producer copied.</summary>
  public FanOutReplayOrder Apply(FanOutReplayOrder currentData, FanOutReplayOrderMoved @event) => new() {
    Id = currentData.Id,
    CustomerId = @event.CustomerId,
    CustomerName = @event.CustomerName,
    Status = currentData.Status,
  };

  /// <summary>
  /// The fan-out's set-based change, as <c>IPerspectiveFanOutFor.Apply</c> returns it, on the perspective whose rows it
  /// targets, as a declared fan-out is (so the rows' cursors are kept under this perspective's name).
  /// </summary>
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Minor Code Smell", "S2325:Methods and properties that don't access instance data should be static", Justification = "Invoked through the instance invoker the generator emits.")]
  public ICollectiveSpec<FanOutReplayOrder> Apply(FanOutReplayCustomerRenamed e) =>
    new FanOutReplayOrderFanOut.Spec(s => s.SetProperty(o => o.CustomerName, e.Name));

  /// <summary>Shipping changes the status alone.</summary>
  public FanOutReplayOrder Apply(FanOutReplayOrder currentData, FanOutReplayOrderShipped @event) => new() {
    Id = currentData.Id,
    CustomerId = currentData.CustomerId,
    CustomerName = currentData.CustomerName,
    Status = @event.Status,
  };
}

/// <summary>The order row: its key column is the customer it belongs to.</summary>
public class FanOutReplayOrder {
  [StreamId]
  public Guid Id { get; set; }

  [PhysicalField]
  [Indexed]
  public Guid CustomerId { get; set; }

  public string CustomerName { get; set; } = string.Empty;

  public string Status { get; set; } = string.Empty;
}

public record FanOutReplayOrderPlaced : IEvent {
  [StreamId]
  public required Guid OrderId { get; init; }
  public required Guid CustomerId { get; init; }
  public required string CustomerName { get; init; }
}

public record FanOutReplayOrderMoved : IEvent {
  [StreamId]
  public required Guid OrderId { get; init; }
  public required Guid CustomerId { get; init; }
  public required string CustomerName { get; init; }
}

public record FanOutReplayOrderShipped : IEvent {
  [StreamId]
  public required Guid OrderId { get; init; }
  public required string Status { get; init; }
}

/// <summary>The foreign event: on the customer's own stream.</summary>
public record FanOutReplayCustomerRenamed : IEvent {
  [StreamId]
  public required Guid CustomerId { get; init; }
  public required string Name { get; init; }
}

/// <summary>The fan-out entry the generator would emit for the perspective.</summary>
public static class FanOutReplayOrderFanOut {
  /// <summary>The order table, as the schema names it.</summary>
  public const string TABLE = "wh_per_fan_out_replay_order";

  internal sealed record Spec(Expression<Action<ICollectiveSetters<FanOutReplayOrder>>> Setters) : ICollectiveSpec<FanOutReplayOrder>;

  /// <summary>What the generator emits for <c>IPerspectiveFanOutFor&lt;FanOutReplayOrder, FanOutReplayCustomerRenamed&gt;</c>.</summary>
  public static readonly PerspectiveFanOutEntry Renamed = new(
    PerspectiveType: typeof(FanOutReplayOrderPerspective),
    ModelType: typeof(FanOutReplayOrder),
    EventType: typeof(FanOutReplayCustomerRenamed),
    KeyProperty: nameof(FanOutReplayOrder.CustomerId),
    KeyColumn: "customer_id",
    Invoker: static (perspective, evt) => ((FanOutReplayOrderPerspective)perspective).Apply((FanOutReplayCustomerRenamed)evt),
    KeySelector: (Expression<Func<FanOutReplayOrder, object?>>)(row => row.CustomerId),
    TableName: TABLE,
    CreateExecutor: static factory => factory.Create<FanOutReplayOrder>(TABLE)) {
    CreateInMemoryExecutor = static factory => factory.Create<FanOutReplayOrder>(),
  };
}
