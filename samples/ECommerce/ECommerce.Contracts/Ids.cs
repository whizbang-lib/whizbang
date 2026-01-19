using Whizbang.Core;

namespace ECommerce.Contracts.Commands;

/// <summary>Strongly-typed ID for products using UUIDv7.</summary>
[WhizbangId]
public readonly partial struct ProductId;

/// <summary>Strongly-typed ID for orders using UUIDv7.</summary>
[WhizbangId]
public readonly partial struct OrderId;

/// <summary>Strongly-typed ID for customers using UUIDv7.</summary>
[WhizbangId]
public readonly partial struct CustomerId;
