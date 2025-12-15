namespace Whizbang.Generators;

/// <summary>
/// Value type containing information about a discovered event with stream key.
/// This record uses value equality which is critical for incremental generator performance.
/// </summary>
/// <param name="EventType">Fully qualified event type name</param>
/// <param name="PropertyName">Name of the property or parameter marked with [StreamKey]</param>
/// <param name="PropertyType">Fully qualified type of the stream key property</param>
/// <tests>tests/Whizbang.Generators.Tests/StreamKeyInfoTests.cs:StreamKeyInfo_ValueEquality_ComparesFieldsAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/StreamKeyInfoTests.cs:StreamKeyInfo_Constructor_SetsPropertiesAsync</tests>
internal sealed record StreamKeyInfo(
    string EventType,
    string PropertyName,
    string PropertyType
);
