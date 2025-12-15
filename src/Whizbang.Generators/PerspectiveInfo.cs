namespace Whizbang.Generators;

/// <summary>
/// Value type containing information about a discovered perspective.
/// This record uses value equality which is critical for incremental generator performance.
/// A single perspective class can implement multiple IPerspectiveOf&lt;TEvent&gt; interfaces,
/// so we store all event types it handles.
/// </summary>
/// <param name="ClassName">Fully qualified class name implementing IPerspectiveOf</param>
/// <param name="EventTypes">Array of fully qualified event type names this perspective listens to</param>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveDiscoveryGeneratorTests.cs</tests>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveSchemaGeneratorTests.cs</tests>
internal sealed record PerspectiveInfo(
    string ClassName,
    string[] EventTypes
);
