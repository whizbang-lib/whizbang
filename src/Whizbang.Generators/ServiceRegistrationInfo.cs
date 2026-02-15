namespace Whizbang.Generators;

/// <summary>
/// Value type containing information about a service to be registered for dependency injection.
/// This record uses value equality which is critical for incremental generator performance.
/// </summary>
/// <param name="ConcreteTypeName">Fully qualified concrete class name (e.g., "global::MyApp.OrderLens")</param>
/// <param name="SimpleTypeName">Simple type name without namespace (e.g., "OrderLens")</param>
/// <param name="UserInterfaceName">Fully qualified user interface name (e.g., "global::MyApp.IOrderLens")</param>
/// <param name="Category">Category of service (Lens or Perspective)</param>
/// <param name="IsAbstract">True if the class is abstract (will be skipped for registration)</param>
/// <tests>tests/Whizbang.Generators.Tests/ServiceRegistrationGeneratorTests.cs</tests>
internal sealed record ServiceRegistrationInfo(
    string ConcreteTypeName,
    string SimpleTypeName,
    string UserInterfaceName,
    ServiceCategory Category,
    bool IsAbstract = false
);

/// <summary>
/// Category of service for dependency injection registration.
/// </summary>
internal enum ServiceCategory {
  /// <summary>
  /// Lens service (implements user interface extending ILensQuery).
  /// </summary>
  Lens,

  /// <summary>
  /// Perspective service (implements user interface extending IPerspectiveFor&lt;&gt;).
  /// </summary>
  Perspective
}
