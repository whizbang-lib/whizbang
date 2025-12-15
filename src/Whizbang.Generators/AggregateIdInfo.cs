namespace Whizbang.Generators;

/// <summary>
/// Value type containing information about a discovered aggregate ID property.
/// This record uses value equality which is critical for incremental generator performance.
/// </summary>
/// <param name="MessageType">Fully qualified message type name (e.g., "global::MyApp.Commands.CreateOrder")</param>
/// <param name="PropertyName">Name of the property marked with [AggregateId] (e.g., "OrderId")</param>
/// <param name="IsNullable">True if the property is Guid?, false if Guid</param>
/// <param name="HasMultipleAttributes">True if the type has multiple [AggregateId] attributes</param>
/// <param name="HasInvalidType">True if the property type is not Guid or Guid?</param>
/// <tests>tests/Whizbang.Generators.Tests/AggregateIdInfoTests.cs:AggregateIdInfo_ValueEquality_ComparesFieldsAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/AggregateIdInfoTests.cs:AggregateIdInfo_Constructor_SetsPropertiesAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/AggregateIdInfoTests.cs:AggregateIdInfo_ErrorFlags_TrackValidationStatesAsync</tests>
internal sealed record AggregateIdInfo(
    string MessageType,
    string PropertyName,
    bool IsNullable,
    bool HasMultipleAttributes = false,
    bool HasInvalidType = false
);
