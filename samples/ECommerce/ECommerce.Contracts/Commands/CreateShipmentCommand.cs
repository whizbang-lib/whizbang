using Whizbang.Core;
using Whizbang.Core.Attributes;

namespace ECommerce.Contracts.Commands;

/// <summary>
/// Command to create a shipment after payment is processed
/// </summary>
[PinnedId("f915e30a-60e7-41bc-bd1c-9ce2067ca02d")]
public record CreateShipmentCommand : ICommand {
  public required string OrderId { get; init; }
  public required string ShippingAddress { get; init; }
}
