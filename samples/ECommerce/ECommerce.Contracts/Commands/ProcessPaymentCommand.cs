using Whizbang.Core;
using Whizbang.Core.Attributes;

namespace ECommerce.Contracts.Commands;

/// <summary>
/// Command to process payment for an order after inventory is reserved
/// </summary>
[PinnedId("883170c6-d962-439e-8b93-5bc6fa19c104")]
public record ProcessPaymentCommand : ICommand {
  public required string OrderId { get; init; }
  public required string CustomerId { get; init; }
  public decimal Amount { get; init; }
}
