using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core;

namespace Whizbang.Core.Workers;

/// <summary>
/// DI extension that wires <see cref="OrphanInboxJanitor"/> into the host. Snapshots all
/// <see cref="IReceptor{TMessage}"/> / <see cref="IReceptor{TMessage,TResponse}"/>
/// registrations from the service collection so the janitor can union them with the
/// perspective registry's event types at run time.
/// </summary>
public static class OrphanInboxJanitorExtensions {
  /// <summary>
  /// Registers the orphan-inbox janitor as a hosted service. Call AFTER all receptor
  /// registrations so the snapshot is complete.
  /// </summary>
  /// <remarks>
  /// Snapshots receptor message types from the current state of <paramref name="services"/>.
  /// The snapshot is registered as a singleton; the janitor reads it at startup. Idempotent
  /// — calling twice replaces the snapshot with the latest state.
  /// </remarks>
  public static IServiceCollection AddOrphanInboxJanitor(this IServiceCollection services) {
    // Self-contained: the janitor waits on schema readiness, so this extension guarantees the
    // gate rather than assuming a fuller composition registered it first.
    services.AddWhizbangSchemaReadyGate();
    ArgumentNullException.ThrowIfNull(services);

    var receptorTypes = _snapshotReceptorMessageTypes(services);
    services.AddSingleton(new HandledReceptorTypeSnapshot(receptorTypes));
    services.TryAddWhizbangDefaults();
    services.AddHostedService<OrphanInboxJanitor>();
    return services;
  }

  private static HashSet<Type> _snapshotReceptorMessageTypes(IServiceCollection services) {
    return services
      .Select(sd => sd.ServiceType)
      .Where(st => st.IsGenericType && st.GetGenericTypeDefinition() is var def && (def == typeof(IReceptor<>) || def == typeof(IReceptor<,>)))
      .Select(st => st.GetGenericArguments())
      .Where(args => args.Length > 0)
      .Select(args => args[0])
      .ToHashSet();
  }
}
