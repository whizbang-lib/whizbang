namespace Whizbang.Core;

/// <summary>
/// Factory for creating WhizbangId instances through dependency injection.
/// </summary>
/// <typeparam name="TId">The WhizbangId type to create.</typeparam>
/// <remarks>
/// <para>
/// Use this interface when you need to inject ID generation into services for better
/// testability and explicit dependencies.
/// </para>
/// <para>
/// <strong>Example - Service with Injected Factory:</strong>
/// <code>
/// public class OrderService {
///     private readonly IWhizbangIdFactory&lt;OrderId&gt; _orderIdFactory;
///
///     public OrderService(IWhizbangIdFactory&lt;OrderId&gt; orderIdFactory) {
///         _orderIdFactory = orderIdFactory;
///     }
///
///     public void CreateOrder() {
///         var orderId = _orderIdFactory.Create();
///         // ... use orderId
///     }
/// }
/// </code>
/// </para>
/// <para>
/// <strong>Example - Testing with Mock Factory:</strong>
/// <code>
/// var mockFactory = new Mock&lt;IWhizbangIdFactory&lt;OrderId&gt;&gt;();
/// mockFactory.Setup(f => f.Create()).Returns(OrderId.From(knownGuid));
/// var service = new OrderService(mockFactory.Object);
/// </code>
/// </para>
/// </remarks>
/// <tests>tests/Whizbang.Generators.Tests/WhizbangIdGeneratorTests.cs:Generator_WithExplicitTypeDeclaration_GeneratesValueObjectAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/WhizbangIdGeneratorTests.cs:Generator_WithMultipleIdTypes_GeneratesAllAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/WhizbangIdGeneratorTests.cs:Generator_WithPropertyBasedDiscovery_GeneratesValueObjectAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/WhizbangIdGeneratorTests.cs:Generator_WithParameterBasedDiscovery_GeneratesValueObjectAsync</tests>
public interface IWhizbangIdFactory<out TId> {
  /// <summary>
  /// Creates a new WhizbangId instance using the configured provider.
  /// </summary>
  /// <returns>A new WhizbangId instance.</returns>
  /// <tests>tests/Whizbang.Generators.Tests/WhizbangIdGeneratorTests.cs:Generator_WithExplicitTypeDeclaration_GeneratesValueObjectAsync</tests>
  TId Create();
}
