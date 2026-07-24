using System;
using System.Collections.Generic;
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
    ArgumentNullException.ThrowIfNull(services);

    var receptorTypes = _snapshotReceptorMessageTypes(services);
    services.AddSingleton(new HandledReceptorTypeSnapshot(receptorTypes));
    services.AddHostedService<OrphanInboxJanitor>();
    return services;
  }

  private static HashSet<Type> _snapshotReceptorMessageTypes(IServiceCollection services) {
    var seen = new HashSet<Type>();
    foreach (var sd in services) {
      var st = sd.ServiceType;
      if (!st.IsGenericType) {
        continue;
      }
      var def = st.GetGenericTypeDefinition();
      if (def != typeof(IReceptor<>) && def != typeof(IReceptor<,>)) {
        continue;
      }
      var args = st.GetGenericArguments();
      if (args.Length > 0) {
        seen.Add(args[0]);
      }
    }
    return seen;
  }
}
