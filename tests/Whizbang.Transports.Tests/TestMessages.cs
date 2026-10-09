// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

// Test message types shared by the unit and component projects (one source, compiled into both).
// The namespaced records simulate different namespace patterns for TransportAutoDiscoveryTests.

namespace Whizbang.Transports.Tests {
  // Message with no namespace test (in Whizbang.Transports.Tests namespace)
  public record NoNamespaceMessage;

  /// <summary>
  /// Test message for the serializer and transport-manager publishing tests.
  /// </summary>
  public record TestMessage : Whizbang.Core.ICommand {
    public required string Content { get; init; }
    public required int Value { get; init; }
  }
}

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
