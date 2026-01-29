// Test namespaces for routing tests (must be in separate namespace structure)
namespace TestNamespaces.MyApp.Orders.Events {
  public sealed record OrderCreated;
}

namespace TestNamespaces.MyApp.Contracts.Commands {
  public sealed record CreateOrder;
}

namespace TestNamespaces.MyApp.Contracts.Events {
  public sealed record OrderCreated;
}

namespace TestNamespaces.MyApp.Contracts.Queries {
  public sealed record GetOrderById;
}

namespace TestNamespaces.MyApp.Contracts.Messages {
  public sealed record CreateOrderCommand;
}
