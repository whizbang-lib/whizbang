using System.Runtime.CompilerServices;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Internal;

namespace Whizbang.Core.Tests.Internal;

/// <summary>
/// Tests for ResponseExtractor - extracts a typed response from complex receptor return values.
/// Used for RPC-style LocalInvokeAsync calls where the caller requests a specific type
/// from a receptor that returns a tuple or complex result.
/// </summary>
public class ResponseExtractorTests {
  // Test types for extraction scenarios
  public record OrderCreated : IEvent {
    [StreamId]
    public required string OrderId { get; init; }
  }

  public record InventoryReserved : IEvent {
    [StreamId]
    public required string ProductId { get; init; }
  }

  public record PaymentProcessed : IEvent {
    [StreamId]
    public required decimal Amount { get; init; }
  }

  public record CacheInvalidated : IEvent {
    [StreamId]
    public required string Key { get; init; }
  }

  // ============================================================
  // Single Value Extraction
  // ============================================================

  [Test]
  public async Task TryExtractResponse_SingleValue_ExtractsDirectMatchAsync() {
    // Arrange
    var order = new OrderCreated { OrderId = "123" };

    // Act
    var success = ResponseExtractor.TryExtractResponse<OrderCreated>(order, out var response);

    // Assert
    await Assert.That(success).IsTrue();
    await Assert.That(response).IsNotNull();
    await Assert.That(response!.OrderId).IsEqualTo("123");
  }

  [Test]
  public async Task TryExtractResponse_SingleValue_ExtractsViaInterfaceAsync() {
    // Arrange
    var order = new OrderCreated { OrderId = "123" };

    // Act - Extract as IEvent (base interface)
    var success = ResponseExtractor.TryExtractResponse<IEvent>(order, out var response);

    // Assert
    await Assert.That(success).IsTrue();
    await Assert.That(response).IsNotNull();
    await Assert.That(response).IsTypeOf<OrderCreated>();
  }

  [Test]
  public async Task TryExtractResponse_SingleValue_FailsWhenTypeMismatchAsync() {
    // Arrange
    var order = new OrderCreated { OrderId = "123" };

    // Act - Try to extract wrong type
    var success = ResponseExtractor.TryExtractResponse<InventoryReserved>(order, out var response);

    // Assert
    await Assert.That(success).IsFalse();
    await Assert.That(response).IsNull();
  }

  [Test]
  public async Task TryExtractResponse_NullResult_ReturnsFalseAsync() {
    // Act
    var success = ResponseExtractor.TryExtractResponse<OrderCreated>(null, out var response);

    // Assert
    await Assert.That(success).IsFalse();
    await Assert.That(response).IsNull();
  }

  // ============================================================
  // Tuple Extraction (2 elements)
  // ============================================================

  [Test]
  public async Task TryExtractResponse_Tuple2_ExtractsFirstElementAsync() {
    // Arrange
    var tuple = (new OrderCreated { OrderId = "123" }, new InventoryReserved { ProductId = "ABC" });

    // Act
    var success = ResponseExtractor.TryExtractResponse<OrderCreated>(tuple, out var response);

    // Assert
    await Assert.That(success).IsTrue();
    await Assert.That(response).IsNotNull();
    await Assert.That(response!.OrderId).IsEqualTo("123");
  }

  [Test]
  public async Task TryExtractResponse_Tuple2_ExtractsSecondElementAsync() {
    // Arrange
    var tuple = (new OrderCreated { OrderId = "123" }, new InventoryReserved { ProductId = "ABC" });

    // Act
    var success = ResponseExtractor.TryExtractResponse<InventoryReserved>(tuple, out var response);

    // Assert
    await Assert.That(success).IsTrue();
    await Assert.That(response).IsNotNull();
    await Assert.That(response!.ProductId).IsEqualTo("ABC");
  }

  [Test]
  public async Task TryExtractResponse_Tuple2_FailsWhenTypeNotPresentAsync() {
    // Arrange
    var tuple = (new OrderCreated { OrderId = "123" }, new InventoryReserved { ProductId = "ABC" });

    // Act
    var success = ResponseExtractor.TryExtractResponse<PaymentProcessed>(tuple, out var response);

    // Assert
    await Assert.That(success).IsFalse();
    await Assert.That(response).IsNull();
  }

  // ============================================================
  // Tuple Extraction (3+ elements)
  // ============================================================

  [Test]
  public async Task TryExtractResponse_Tuple3_ExtractsMiddleElementAsync() {
    // Arrange
    var tuple = (
      new OrderCreated { OrderId = "123" },
      new InventoryReserved { ProductId = "ABC" },
      new PaymentProcessed { Amount = 99.99m }
    );

    // Act
    var success = ResponseExtractor.TryExtractResponse<InventoryReserved>(tuple, out var response);

    // Assert
    await Assert.That(success).IsTrue();
    await Assert.That(response).IsNotNull();
    await Assert.That(response!.ProductId).IsEqualTo("ABC");
  }

  [Test]
  public async Task TryExtractResponse_Tuple4_ExtractsLastElementAsync() {
    // Arrange
    var tuple = (
      new OrderCreated { OrderId = "123" },
      new InventoryReserved { ProductId = "ABC" },
      new PaymentProcessed { Amount = 99.99m },
      new CacheInvalidated { Key = "order:123" }
    );

    // Act
    var success = ResponseExtractor.TryExtractResponse<CacheInvalidated>(tuple, out var response);

    // Assert
    await Assert.That(success).IsTrue();
    await Assert.That(response).IsNotNull();
    await Assert.That(response!.Key).IsEqualTo("order:123");
  }

  // ============================================================
  // Routed<T> Wrapper Extraction
  // ============================================================

  [Test]
  public async Task TryExtractResponse_RoutedWrapper_ExtractsInnerValueAsync() {
    // Arrange
    var routed = Route.Local(new OrderCreated { OrderId = "123" });

    // Act
    var success = ResponseExtractor.TryExtractResponse<OrderCreated>(routed, out var response);

    // Assert
    await Assert.That(success).IsTrue();
    await Assert.That(response).IsNotNull();
    await Assert.That(response!.OrderId).IsEqualTo("123");
  }

  [Test]
  public async Task TryExtractResponse_TupleWithRoutedWrapper_ExtractsFromTupleAsync() {
    // Arrange - Tuple where one element is wrapped
    var tuple = (
      new OrderCreated { OrderId = "123" },
      Route.Local(new CacheInvalidated { Key = "cache:key" })
    );

    // Act - Extract the non-wrapped OrderCreated
    var success = ResponseExtractor.TryExtractResponse<OrderCreated>(tuple, out var response);

    // Assert
    await Assert.That(success).IsTrue();
    await Assert.That(response).IsNotNull();
    await Assert.That(response!.OrderId).IsEqualTo("123");
  }

  [Test]
  public async Task TryExtractResponse_TupleWithRoutedWrapper_ExtractsFromWrapperAsync() {
    // Arrange - Tuple where one element is wrapped
    var tuple = (
      new OrderCreated { OrderId = "123" },
      Route.Local(new CacheInvalidated { Key = "cache:key" })
    );

    // Act - Extract CacheInvalidated from within the Routed wrapper
    var success = ResponseExtractor.TryExtractResponse<CacheInvalidated>(tuple, out var response);

    // Assert
    await Assert.That(success).IsTrue();
    await Assert.That(response).IsNotNull();
    await Assert.That(response!.Key).IsEqualTo("cache:key");
  }

  // ============================================================
  // RPC Response Extraction Ignores Routing Wrappers
  // ============================================================

  [Test]
  public async Task TryExtractResponse_RouteLocal_ExtractsInnerValueForRpcAsync() {
    // Arrange - Response wrapped in Route.Local() should still be extractable
    var routed = Route.Local(new OrderCreated { OrderId = "123" });

    // Act - RPC extraction should unwrap and return the value
    var success = ResponseExtractor.TryExtractResponse<OrderCreated>(routed, out var response);

    // Assert - Value extracted regardless of routing wrapper
    await Assert.That(success).IsTrue();
    await Assert.That(response).IsNotNull();
    await Assert.That(response!.OrderId).IsEqualTo("123");
  }

  [Test]
  public async Task TryExtractResponse_RouteOutbox_ExtractsInnerValueForRpcAsync() {
    // Arrange - Response wrapped in Route.Outbox() should still be extractable
    var routed = Route.Outbox(new OrderCreated { OrderId = "456" });

    // Act - RPC extraction should unwrap and return the value
    var success = ResponseExtractor.TryExtractResponse<OrderCreated>(routed, out var response);

    // Assert - Value extracted regardless of routing wrapper
    await Assert.That(success).IsTrue();
    await Assert.That(response).IsNotNull();
    await Assert.That(response!.OrderId).IsEqualTo("456");
  }

  [Test]
  public async Task TryExtractResponse_RouteBoth_ExtractsInnerValueForRpcAsync() {
    // Arrange - Response wrapped in Route.Both() should still be extractable
    var routed = Route.Both(new OrderCreated { OrderId = "789" });

    // Act - RPC extraction should unwrap and return the value
    var success = ResponseExtractor.TryExtractResponse<OrderCreated>(routed, out var response);

    // Assert - Value extracted regardless of routing wrapper
    await Assert.That(success).IsTrue();
    await Assert.That(response).IsNotNull();
    await Assert.That(response!.OrderId).IsEqualTo("789");
  }

  [Test]
  public async Task TryExtractResponse_TupleWithAllRouted_ExtractsCorrectTypeAsync() {
    // Arrange - Tuple where ALL elements are wrapped in routing
    var tuple = (
      Route.Local(new OrderCreated { OrderId = "123" }),
      Route.Outbox(new InventoryReserved { ProductId = "ABC" })
    );

    // Act - Extract OrderCreated from Route.Local wrapper
    var success = ResponseExtractor.TryExtractResponse<OrderCreated>(tuple, out var response);

    // Assert
    await Assert.That(success).IsTrue();
    await Assert.That(response).IsNotNull();
    await Assert.That(response!.OrderId).IsEqualTo("123");
  }

  [Test]
  public async Task TryExtractResponse_NestedRoutedWrappers_ExtractsValueAsync() {
    // Arrange - Edge case: nested routed wrappers (shouldn't happen but should handle)
    var innerRouted = Route.Local(new OrderCreated { OrderId = "nested" });
    var tuple = (innerRouted, new InventoryReserved { ProductId = "ABC" });

    // Act
    var success = ResponseExtractor.TryExtractResponse<OrderCreated>(tuple, out var response);

    // Assert
    await Assert.That(success).IsTrue();
    await Assert.That(response).IsNotNull();
    await Assert.That(response!.OrderId).IsEqualTo("nested");
  }

  // ============================================================
  // Duplicate Type Handling
  // ============================================================

  [Test]
  public async Task TryExtractResponse_TupleWithDuplicateTypes_ExtractsFirstMatchAsync() {
    // Arrange - Tuple with two OrderCreated instances
    var first = new OrderCreated { OrderId = "first" };
    var second = new OrderCreated { OrderId = "second" };
    var tuple = (first, second);

    // Act - Should extract the first match
    var success = ResponseExtractor.TryExtractResponse<OrderCreated>(tuple, out var response);

    // Assert
    await Assert.That(success).IsTrue();
    await Assert.That(response).IsNotNull();
    await Assert.That(response!.OrderId).IsEqualTo("first");
  }

  // ============================================================
  // Array and Enumerable Handling
  // ============================================================

  [Test]
  public async Task TryExtractResponse_Array_ExtractsFirstMatchAsync() {
    // Arrange
    var array = new IEvent[] {
      new OrderCreated { OrderId = "123" },
      new InventoryReserved { ProductId = "ABC" }
    };

    // Act
    var success = ResponseExtractor.TryExtractResponse<OrderCreated>(array, out var response);

    // Assert
    await Assert.That(success).IsTrue();
    await Assert.That(response).IsNotNull();
    await Assert.That(response!.OrderId).IsEqualTo("123");
  }

  [Test]
  public async Task TryExtractResponse_List_ExtractsMatchAsync() {
    // Arrange
    var list = new List<IEvent> {
      new InventoryReserved { ProductId = "ABC" },
      new OrderCreated { OrderId = "123" }
    };

    // Act
    var success = ResponseExtractor.TryExtractResponse<OrderCreated>(list, out var response);

    // Assert
    await Assert.That(success).IsTrue();
    await Assert.That(response).IsNotNull();
    await Assert.That(response!.OrderId).IsEqualTo("123");
  }

  // ============================================================
  // Non-IMessage Type Extraction
  // ============================================================

  [Test]
  public async Task TryExtractResponse_TupleWithNonMessage_ExtractsNonMessageTypeAsync() {
    // Arrange - Tuple with a string and an event
    var tuple = ("result-string", new OrderCreated { OrderId = "123" });

    // Act - Extract the string
    var success = ResponseExtractor.TryExtractResponse<string>(tuple, out var response);

    // Assert
    await Assert.That(success).IsTrue();
    await Assert.That(response).IsEqualTo("result-string");
  }

  [Test]
  public async Task TryExtractResponse_TupleWithPrimitives_ExtractsIntAsync() {
    // Arrange
    var tuple = (42, "hello", new OrderCreated { OrderId = "123" });

    // Act
    var success = ResponseExtractor.TryExtractResponse<int>(tuple, out var response);

    // Assert
    await Assert.That(success).IsTrue();
    await Assert.That(response).IsEqualTo(42);
  }

  // ============================================================
  // Reference Extraction (for cascade exclusion)
  // ============================================================

  [Test]
  public async Task TryExtractResponse_ReturnsExactInstance_ForReferenceEqualityAsync() {
    // Arrange
    var order = new OrderCreated { OrderId = "123" };
    var inventory = new InventoryReserved { ProductId = "ABC" };
    var tuple = (order, inventory);

    // Act
    var success = ResponseExtractor.TryExtractResponse<OrderCreated>(tuple, out var response);

    // Assert - Should return the same instance (for ReferenceEquals in cascade exclusion)
    await Assert.That(success).IsTrue();
    await Assert.That(ReferenceEquals(response, order)).IsTrue();
  }

  // ============================================================
  // Edge Cases
  // ============================================================

  [Test]
  public async Task TryExtractResponse_EmptyTuple_ReturnsFalseAsync() {
    // Arrange
    var tuple = ValueTuple.Create();

    // Act
    var success = ResponseExtractor.TryExtractResponse<OrderCreated>(tuple, out var response);

    // Assert
    await Assert.That(success).IsFalse();
    await Assert.That(response).IsNull();
  }

  [Test]
  public async Task TryExtractResponse_TupleWithNulls_SkipsNullsAsync() {
    // Arrange
    OrderCreated? nullOrder = null;
    var inventory = new InventoryReserved { ProductId = "ABC" };
    var tuple = (nullOrder, inventory);

    // Act
    var success = ResponseExtractor.TryExtractResponse<InventoryReserved>(tuple, out var response);

    // Assert
    await Assert.That(success).IsTrue();
    await Assert.That(response).IsNotNull();
    await Assert.That(response!.ProductId).IsEqualTo("ABC");
  }

  [Test]
  public async Task TryExtractResponse_TupleWithAllNulls_ReturnsFalseAsync() {
    // Arrange
    OrderCreated? nullOrder = null;
    InventoryReserved? nullInventory = null;
    var tuple = (nullOrder, nullInventory);

    // Act
    var success = ResponseExtractor.TryExtractResponse<OrderCreated>(tuple, out var response);

    // Assert
    await Assert.That(success).IsFalse();
    await Assert.That(response).IsNull();
  }

  // ============================================================
  // Discriminated Union Tuple Patterns
  // ============================================================

  [Test]
  public async Task TryExtractResponse_DiscriminatedUnion_ExtractsSuccessPathAsync() {
    // Arrange - Discriminated union: (success, failure) - success path
    var success = new OrderCreated { OrderId = "123" };
    InventoryReserved? failure = null;
    var tuple = (success, failure);

    // Act
    var extracted = ResponseExtractor.TryExtractResponse<OrderCreated>(tuple, out var response);

    // Assert
    await Assert.That(extracted).IsTrue();
    await Assert.That(response).IsNotNull();
    await Assert.That(response!.OrderId).IsEqualTo("123");
  }

  [Test]
  public async Task TryExtractResponse_DiscriminatedUnion_ExtractsFailurePathAsync() {
    // Arrange - Discriminated union: (success, failure) - failure path
    OrderCreated? success = null;
    var failure = new InventoryReserved { ProductId = "ABC" };
    var tuple = (success, failure);

    // Act
    var extracted = ResponseExtractor.TryExtractResponse<InventoryReserved>(tuple, out var response);

    // Assert
    await Assert.That(extracted).IsTrue();
    await Assert.That(response).IsNotNull();
    await Assert.That(response!.ProductId).IsEqualTo("ABC");
  }

  [Test]
  public async Task TryExtractResponse_DiscriminatedUnion_SuccessTypeNotPresentReturnsFalseAsync() {
    // Arrange - Discriminated union where we request the null path
    OrderCreated? success = null;
    var failure = new InventoryReserved { ProductId = "ABC" };
    var tuple = (success, failure);

    // Act - Request OrderCreated which is null
    var extracted = ResponseExtractor.TryExtractResponse<OrderCreated>(tuple, out var response);

    // Assert
    await Assert.That(extracted).IsFalse();
    await Assert.That(response).IsNull();
  }

  [Test]
  public async Task TryExtractResponse_DiscriminatedUnionWithRouteNone_ExtractsSuccessPathAsync() {
    // Arrange - Using Route.None() instead of null for explicit "no value"
    var success = new OrderCreated { OrderId = "456" };
    var tuple = (success: (object)success, failure: Route.None());

    // Act
    var extracted = ResponseExtractor.TryExtractResponse<OrderCreated>(tuple, out var response);

    // Assert
    await Assert.That(extracted).IsTrue();
    await Assert.That(response).IsNotNull();
    await Assert.That(response!.OrderId).IsEqualTo("456");
  }

  [Test]
  public async Task TryExtractResponse_DiscriminatedUnionWithRouteNone_SkipsNoneValueAsync() {
    // Arrange - Route.None() indicates "no value here"
    var failure = new InventoryReserved { ProductId = "DEF" };
    var tuple = (success: Route.None(), failure: (object)failure);

    // Act - Extract from the failure path
    var extracted = ResponseExtractor.TryExtractResponse<InventoryReserved>(tuple, out var response);

    // Assert
    await Assert.That(extracted).IsTrue();
    await Assert.That(response).IsNotNull();
    await Assert.That(response!.ProductId).IsEqualTo("DEF");
  }

  [Test]
  public async Task TryExtractResponse_RouteNone_CannotBeExtractedAsync() {
    // Arrange - Route.None() should never be extracted as a value
    var tuple = (Route.None(), new OrderCreated { OrderId = "789" });

    // Act - Try to extract RoutedNone (should fail - it's not a value)
    var extracted = ResponseExtractor.TryExtractResponse<RoutedNone>(tuple, out var response);

    // Assert - RoutedNone should NOT be extractable
    await Assert.That(extracted).IsFalse();
    await Assert.That(response).IsEqualTo(default(RoutedNone));
  }

  [Test]
  public async Task TryExtractResponse_AllRouteNone_ReturnsFalseAsync() {
    // Arrange - Tuple with only Route.None() values
    var tuple = (Route.None(), Route.None());

    // Act
    var extracted = ResponseExtractor.TryExtractResponse<OrderCreated>(tuple, out var response);

    // Assert
    await Assert.That(extracted).IsFalse();
    await Assert.That(response).IsNull();
  }

  [Test]
  public async Task TryExtractResponse_ThreeWayDiscriminatedUnion_ExtractsCorrectPathAsync() {
    // Arrange - Three-way union: (success, validation_error, system_error)
    var validationError = new PaymentProcessed { Amount = 0 };  // Using as validation error
    var tuple = (
      success: (OrderCreated?)null,
      validationError: validationError,
      systemError: (CacheInvalidated?)null
    );

    // Act
    var extracted = ResponseExtractor.TryExtractResponse<PaymentProcessed>(tuple, out var response);

    // Assert
    await Assert.That(extracted).IsTrue();
    await Assert.That(response).IsNotNull();
    await Assert.That(response!.Amount).IsEqualTo(0);
  }

  [Test]
  public async Task TryExtractResponse_NamedTupleElements_ExtractsCorrectlyAsync() {
    // Arrange - Named tuple elements for clarity
    var tuple = (
      created: new OrderCreated { OrderId = "order-1" },
      reserved: (InventoryReserved?)null,
      processed: (PaymentProcessed?)null
    );

    // Act
    var extracted = ResponseExtractor.TryExtractResponse<OrderCreated>(tuple, out var response);

    // Assert
    await Assert.That(extracted).IsTrue();
    await Assert.That(response!.OrderId).IsEqualTo("order-1");
  }
}
