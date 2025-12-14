namespace Whizbang.Core;

/// <summary>
/// Perspectives listen to events and update read models (projections/views).
/// They are eventually-consistent denormalized views optimized for queries.
/// A perspective can implement multiple IPerspectiveOf interfaces to handle different event types.
/// </summary>
/// <typeparam name="TEvent">The type of event this perspective listens to</typeparam>
/// <docs>core-concepts/perspectives</docs>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveDiscoveryGeneratorTests.cs:PerspectiveDiscoveryGenerator_SinglePerspectiveOneEvent_GeneratesRegistrationAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveDiscoveryGeneratorTests.cs:PerspectiveDiscoveryGenerator_SinglePerspectiveMultipleEvents_GeneratesMultipleRegistrationsAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveDiscoveryGeneratorTests.cs:PerspectiveDiscoveryGenerator_MultiplePerspectives_GeneratesAllRegistrationsAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveSchemaGeneratorTests.cs</tests>
/// <tests>tests/Whizbang.Generators.Tests/MessageRegistryGeneratorTests.cs</tests>
/// <tests>tests/Whizbang.Generators.Tests/EFCoreServiceRegistrationGeneratorTests.cs</tests>
public interface IPerspectiveOf<in TEvent> where TEvent : IEvent {
  /// <summary>
  /// Updates the read model based on the event.
  /// </summary>
  /// <param name="event">The event that occurred</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <tests>tests/Whizbang.Generators.Tests/PerspectiveDiscoveryGeneratorTests.cs:PerspectiveDiscoveryGenerator_SinglePerspectiveOneEvent_GeneratesRegistrationAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/PerspectiveDiscoveryGeneratorTests.cs:PerspectiveDiscoveryGenerator_SinglePerspectiveMultipleEvents_GeneratesMultipleRegistrationsAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/PerspectiveDiscoveryGeneratorTests.cs:PerspectiveDiscoveryGenerator_MultiplePerspectives_GeneratesAllRegistrationsAsync</tests>
  Task Update(TEvent @event, CancellationToken cancellationToken = default);
}
