using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Registration for <see cref="IEventUpcaster"/>s and the <see cref="EventUpcasterPipeline"/>.
/// Registration is explicit (no assembly scanning) so it is AOT-safe and the apply order is
/// the call order — register upcasters oldest-shape → newest-shape.
/// </summary>
/// <docs>fundamentals/events/event-upcasting</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/EventUpcasterRegistrationTests.cs</tests>
public static class EventUpcasterServiceCollectionExtensions {
  /// <summary>
  /// Registers <typeparamref name="TUpcaster"/> as an <see cref="IEventUpcaster"/> (singleton —
  /// upcasters are pure and stateless) and ensures the <see cref="EventUpcasterPipeline"/> is
  /// resolvable. Upcasters apply in the order this is called.
  /// </summary>
  /// <typeparam name="TUpcaster">The upcaster implementation to register.</typeparam>
  /// <param name="services">The service collection.</param>
  /// <returns>The same <see cref="IServiceCollection"/> for chaining.</returns>
  public static IServiceCollection AddEventUpcaster<
      [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TUpcaster>(
      this IServiceCollection services)
      where TUpcaster : class, IEventUpcaster {
    ArgumentNullException.ThrowIfNull(services);

    // Append (not TryAdd) so multiple upcasters register and DI preserves call order in
    // IEnumerable<IEventUpcaster> — that order is the upcast-pass order.
    services.AddSingleton<IEventUpcaster, TUpcaster>();

    // One pipeline, built from whatever upcasters are registered (in order).
    services.TryAddSingleton(sp => new EventUpcasterPipeline(sp.GetServices<IEventUpcaster>()));

    return services;
  }
}
