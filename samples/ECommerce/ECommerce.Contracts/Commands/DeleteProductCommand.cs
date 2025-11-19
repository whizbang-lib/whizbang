using Whizbang.Core;

namespace ECommerce.Contracts.Commands;

/// <summary>
/// Command to soft-delete a product from catalog
/// </summary>
public record DeleteProductCommand : ICommand {
  public required string ProductId { get; init; }
}
