// Test types in nested namespaces for namespace-based routing tests

namespace OutboxTestTypes.Orders.Events {
  public sealed record OrderCreated;
  public sealed record OrderUpdated;
}

namespace OutboxTestTypes.Orders.Commands {
  public sealed record CreateOrder;
  public sealed record UpdateOrder;
}

namespace OutboxTestTypes.Contracts.Events {
  public sealed record ProductCreatedEvent;  // Flat namespace, domain from type name
}

namespace OutboxTestTypes.Users.Commands {
  public sealed record CreateUser;
}

namespace OutboxTestTypes.Users.Events {
  public sealed record UserCreated;
}

// Type without namespace for edge case testing
#pragma warning disable CA1050 // Declare types in namespaces
public sealed record TypeWithoutNamespace;
#pragma warning restore CA1050
