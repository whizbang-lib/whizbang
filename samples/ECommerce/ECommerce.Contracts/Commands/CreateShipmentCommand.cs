using Whizbang.Core;

namespace ECommerce.Contracts.Commands;

/// <summary>
/// Command to create a shipment after payment is processed
/// </summary>
public record CreateShipmentCommand : ICommand {
  public required string OrderId { get; init; }
  public required string ShippingAddress { get; init; }
}
