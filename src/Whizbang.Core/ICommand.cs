namespace Whizbang.Core;

/// <summary>
/// Marker interface for commands - messages that represent an intent to change state.
/// Commands are processed by Receptors which validate business rules and emit Events.
/// </summary>
public interface ICommand {
}
