using Whizbang.Core;

namespace ECommerce.Contracts.Commands;

/// <summary>
/// Command to process payment for an order after inventory is reserved
/// </summary>
public record ProcessPaymentCommand : ICommand {
  public required string OrderId { get; init; }
  public required string CustomerId { get; init; }
  public decimal Amount { get; init; }
}
