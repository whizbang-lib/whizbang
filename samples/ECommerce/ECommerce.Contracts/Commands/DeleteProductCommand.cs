using Whizbang.Core;

namespace ECommerce.Contracts.Commands;

/// <summary>
/// Command to soft-delete a product from catalog
/// </summary>
public record DeleteProductCommand : ICommand {
  [AggregateId]
  public required Guid ProductId { get; init; }
}
