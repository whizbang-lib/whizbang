using System.Text.Json.Serialization;

namespace ECommerce.BFF.API.Perspectives;

/// <summary>
/// JSON source generation context for BFF perspective detail models.
/// These models are serialized into PostgreSQL JSONB columns in the order_status_history table.
/// </summary>
[JsonSourceGenerationOptions(
  PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
  WriteIndented = false,
  DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
)]
[JsonSerializable(typeof(OrderCreatedDetails))]
[JsonSerializable(typeof(InventoryReservedDetails))]
[JsonSerializable(typeof(PaymentProcessedDetails))]
[JsonSerializable(typeof(PaymentFailedDetails))]
[JsonSerializable(typeof(OrderShippedDetails))]
public partial class PerspectiveJsonContext : JsonSerializerContext {
}
