using Whizbang.Core;

// Test namespaces for NamespaceRoutingStrategy tests
// These are in separate namespaces to test namespace-based routing
namespace NamespaceRoutingTestTypes {
  // Used in generic tests (falls back to type name extraction)
  internal sealed record OrderCreated : IEvent;
}

namespace TestNamespaces.MyApp.Orders.Events {
  internal sealed record OrderCreated : IEvent;
  internal sealed record OrderUpdated : IEvent;
}

namespace TestNamespaces.MyApp.Contracts.Commands {
  internal sealed record CreateOrder : ICommand;
}

namespace TestNamespaces.MyApp.Contracts.Events {
  internal sealed record OrderCreated : IEvent;
}

namespace TestNamespaces.MyApp.Contracts.Queries {
  internal sealed record GetOrderById;
}

namespace TestNamespaces.MyApp.Contracts.Messages {
  internal sealed record CreateOrderCommand : ICommand;
  internal sealed record OrderCreatedEvent : IEvent;
}
