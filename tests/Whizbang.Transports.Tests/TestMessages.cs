// Test message types for TransportAutoDiscoveryTests
// These simulate different namespace patterns for pattern matching tests

// Message with no namespace (for null namespace test)
public record NoNamespaceMessage;

// MyApp.Orders.* pattern
namespace MyApp.Orders {
  public record OrderCreated;
}

// MyApp.Payments.* pattern
namespace MyApp.Payments {
  public record PaymentProcessed;
  public record PaymentReceived;
}

// *.Events pattern
namespace MyApp.Orders.Events {
  public record OrderCreatedEvent;
}
