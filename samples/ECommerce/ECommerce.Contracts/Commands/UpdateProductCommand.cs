using Whizbang.Core;

namespace ECommerce.Contracts.Commands;

/// <summary>
/// Command to update product details
/// </summary>
public record UpdateProductCommand : ICommand {
  [AggregateId]
  public required Guid ProductId { get; init; }
  public string? Name { get; init; }
  public string? Description { get; init; }
  public decimal? Price { get; init; }
  public string? ImageUrl { get; init; }
}
