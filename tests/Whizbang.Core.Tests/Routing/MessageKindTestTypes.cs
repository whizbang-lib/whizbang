using Whizbang.Core;
using Whizbang.Core.Routing;

#pragma warning disable CA1707 // Identifiers should not contain underscores
#pragma warning disable WHIZ009 // Event without StreamKey - test types don't need StreamKey

namespace Whizbang.Core.Tests.Routing {
  #region Test Types - Root Namespace (for suffix detection)

  // Types in test namespace for suffix-based detection
  public sealed record CreateOrderCommand;
  public sealed record OrderCreatedEvent;
  public sealed record GetOrderByIdQuery;
  public sealed record OrderCreated;
  public sealed record OrderUpdated;
  public sealed record OrderDeleted;
  public sealed record UnclassifiedMessage;

  // Types with attribute markers
  [MessageKind(MessageKind.Command)]
  public sealed record AttributeMarkedCommand;

  [MessageKind(MessageKind.Event)]
  public sealed record AttributeMarkedEvent;

  [MessageKind(MessageKind.Query)]
  public sealed record AttributeMarkedQuery;

  // Type that implements ICommand but is marked as Event via attribute
  [MessageKind(MessageKind.Event)]
  public sealed record AttributeOverridesInterface : ICommand;

  // Types that implement interfaces
  public sealed record InterfaceCommand : ICommand;

  public sealed record InterfaceEvent([property: StreamKey] Guid StreamKey) : IEvent;

  public sealed record InterfaceQuery : IQuery;

  #endregion
}

namespace Whizbang.Core.Tests.Routing.MessageKindTestTypes.Commands {
  public sealed record PlainCommand;
  public sealed record CreateOrderEvent;  // Wrong suffix, right namespace
}

namespace Whizbang.Core.Tests.Routing.MessageKindTestTypes.Events {
  public sealed record PlainEvent;
  public sealed record InterfaceInWrongNamespace : ICommand;  // ICommand in Events namespace
}

namespace Whizbang.Core.Tests.Routing.MessageKindTestTypes.Queries {
  public sealed record PlainQuery;
}
