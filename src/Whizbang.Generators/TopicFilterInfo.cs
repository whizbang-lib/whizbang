namespace Whizbang.Generators;

/// <summary>
/// Value type containing information about a discovered topic filter on a command.
/// This record uses value equality which is critical for incremental generator performance.
/// Sealed for performance optimization - allows better caching by the incremental generator.
/// </summary>
/// <param name="CommandType">Fully qualified command type name (e.g., "global::MyApp.Commands.CreateOrder")</param>
/// <param name="Filter">The topic filter string extracted from the attribute</param>
/// <tests>tests/Whizbang.Generators.Tests/TopicFilterGeneratorTests.cs</tests>
internal sealed record TopicFilterInfo(
    string CommandType,
    string Filter
);
