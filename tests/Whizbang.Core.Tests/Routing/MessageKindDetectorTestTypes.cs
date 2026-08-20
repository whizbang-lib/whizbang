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

// Test-only types declared INSIDE the framework system namespace (namespaces merge across
// assemblies) — exercises the framework-system-namespace detection tier without depending
// on which real system commands exist.
namespace Whizbang.Core.Commands.System {
  /// <summary>
  /// Implements ICommand but lives in the framework system namespace — the
  /// framework-system tier outranks interface detection, so this detects as System.
  /// </summary>
  internal sealed record DetectorTestSystemCommand : Whizbang.Core.ICommand;

  /// <summary>
  /// [MessageKind] attribute must still outrank the framework-system-namespace tier
  /// (explicit override wins over every convention).
  /// </summary>
  [Whizbang.Core.Routing.MessageKind(Whizbang.Core.Routing.MessageKind.Command)]
  internal sealed record DetectorTestAttributeOverridesSystem;
}

namespace Whizbang.Core.Commands.System.SubArea {
  /// <summary>
  /// Sub-namespace of the framework system namespace — still detects as System.
  /// </summary>
  internal sealed record DetectorTestNestedSystemCommand;
}

namespace MyAppTest.Commands.System {
  /// <summary>
  /// A CONSUMER namespace that merely ends in ".Commands.System" is NOT framework system
  /// traffic — the generic Commands-segment convention applies, detecting as Command.
  /// </summary>
  internal sealed record DetectorTestConsumerSystemLookalike;
}
