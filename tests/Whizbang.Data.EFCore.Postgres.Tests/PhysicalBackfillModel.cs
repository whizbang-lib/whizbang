using System;
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;
using Whizbang.Core.Perspectives;

#pragma warning disable WHIZ105

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// A perspective model with one property of every type a physical column can be backfilled for, so the
/// persistence serializer writes each one in its stored form. Used by PhysicalColumnBackfillIntegrationTests.
/// </summary>
public class PhysicalBackfillModel {
  [StreamId]
  public required Guid Id { get; init; }
  public string? Text { get; init; }
  public Guid Ref { get; init; }
  public int Count { get; init; }
  public long Big { get; init; }
  public short Small { get; init; }
  public bool Flag { get; init; }
  public decimal Amount { get; init; }
  public double Ratio { get; init; }
  public float Weight { get; init; }
  public DateTime At { get; init; }
  public DateTimeOffset AtOffset { get; init; }
  public DateOnly Day { get; init; }
  public TimeOnly Clock { get; init; }
  public int? Maybe { get; init; }
}

public record PhysicalBackfillCreatedEvent : IEvent {
  [StreamId]
  public required Guid Id { get; init; }
}

public class PhysicalBackfillPerspective : IPerspectiveFor<PhysicalBackfillModel, PhysicalBackfillCreatedEvent> {
  public PhysicalBackfillModel Apply(PhysicalBackfillModel currentData, PhysicalBackfillCreatedEvent @event) =>
    new() { Id = @event.Id };

  public static Task Update(PhysicalBackfillCreatedEvent @event, CancellationToken cancellationToken = default) =>
    Task.CompletedTask;
}
