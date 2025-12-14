namespace Whizbang.Core;

/// <summary>
/// Marker interface for commands - messages that represent an intent to change state.
/// Commands are processed by Receptors which validate business rules and emit Events.
/// </summary>
/// <docs>messaging/commands-events</docs>
/// <tests>tests/Whizbang.Generators.Tests/MessageRegistryGeneratorTests.cs:MessageRegistryGenerator_WithCommandAndEventMessages_GeneratesCorrectRegistryAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs:MessageJsonContextGenerator_WithSingleCommand_GeneratesValidJsonContextAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/ReceptorDiscoveryGeneratorTests.cs:ReceptorDiscoveryGenerator_WithSingleReceptorAndCommand_GeneratesValidDispatcherAsync</tests>
public interface ICommand {
}
