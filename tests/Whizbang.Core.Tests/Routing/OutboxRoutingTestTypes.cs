// Test types in nested namespaces for namespace-based routing tests

namespace OutboxTestTypes.Orders.Events {
  public sealed record OrderCreated;
  public sealed record OrderUpdated;
}

namespace OutboxTestTypes.Contracts.Events {
  public sealed record ProductCreatedEvent;  // Flat namespace, domain from type name
}
