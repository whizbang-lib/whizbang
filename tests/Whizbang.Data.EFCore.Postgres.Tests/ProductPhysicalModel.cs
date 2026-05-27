using System;
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;
using Whizbang.Core.Perspectives;

#pragma warning disable WHIZ105

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Top-level TModel used by <see cref="AtomicUpsertPhysicalFieldsIntegrationTests"/>.
/// MessageJsonContextGenerator discovers it via the <see cref="ProductPhysicalPerspective"/>
/// stub below — without that perspective the generator wouldn't see the type and the
/// Path 1 atomic upsert wouldn't have JsonTypeInfo for it.
/// </summary>
public class ProductPhysicalModel {
  [StreamId]
  public required Guid Id { get; init; }
  public string Name { get; init; } = string.Empty;
  public decimal Price { get; init; }
  public string? Description { get; init; }
}

/// <summary>
/// Sample event for <see cref="ProductPhysicalPerspective"/>. Purely a discovery hook for
/// MessageJsonContextGenerator — the perspective never actually runs in test setup.
/// </summary>
public record ProductPhysicalCreatedEvent : IEvent {
  [StreamId]
  public required Guid Id { get; init; }
  public required string Name { get; init; }
  public required decimal Price { get; init; }
}

/// <summary>
/// Stub perspective binding <see cref="ProductPhysicalModel"/> to the discovery surface.
/// Required so MessageJsonContextGenerator emits JsonTypeInfo for the model — Path 1
/// uses that JsonTypeInfo when serializing the row's <c>data</c> JSONB column.
/// </summary>
public class ProductPhysicalPerspective : IPerspectiveFor<ProductPhysicalModel, ProductPhysicalCreatedEvent> {
  public ProductPhysicalModel Apply(ProductPhysicalModel currentData, ProductPhysicalCreatedEvent @event) =>
    new() { Id = @event.Id, Name = @event.Name, Price = @event.Price };

  public Task Update(ProductPhysicalCreatedEvent @event, CancellationToken cancellationToken = default) =>
    Task.CompletedTask;
}
