using Whizbang.Core.Routing;

// These types must be in specific namespaces to test namespace-based detection
// Cannot use file-scoped namespaces since we need multiple namespaces

namespace Whizbang.Core.Tests.Routing.MessageKindDetectorTestTypes.Commands {
  /// <summary>
  /// Type in Commands namespace (no suffix) - should detect as Command from namespace.
  /// </summary>
  internal sealed record CommandsNamespaceMessage;

  /// <summary>
  /// Type ends with "Event" but is in Commands namespace - namespace should take priority.
  /// </summary>
  internal sealed record ConfusingEvent;
}

namespace Whizbang.Core.Tests.Routing.MessageKindDetectorTestTypes.Events {
  /// <summary>
  /// Type in Events namespace (no suffix) - should detect as Event from namespace.
  /// </summary>
  internal sealed record EventsNamespaceMessage;

  /// <summary>
  /// Implements ICommand but is in Events namespace - interface should take priority.
  /// </summary>
  internal sealed record InterfaceOverridesNamespace : ICommand;
}

namespace Whizbang.Core.Tests.Routing.MessageKindDetectorTestTypes.Queries {
  /// <summary>
  /// Type in Queries namespace (no suffix) - should detect as Query from namespace.
  /// </summary>
  internal sealed record QueriesNamespaceMessage;
}
