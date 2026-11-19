// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core;
using Whizbang.Core.Startup;

namespace Whizbang.Core.Workers;

/// <summary>
/// DI extension that wires <see cref="OrphanInboxJanitor"/> into the host. Snapshots all
/// <see cref="IReceptor{TMessage}"/> / <see cref="IReceptor{TMessage,TResponse}"/>
/// registrations from the service collection so the janitor can union them with the
/// consumer registry's consumed types at run time.
/// </summary>
/// <docs>operations/workers/orphan-inbox-janitor</docs>
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
    // Self-contained: the janitor starts at Accept commands, and a host composed without the worker
    // pipeline (no stage gate) falls back to schema readiness, so this extension guarantees that
    // gate rather than assuming a fuller composition registered it first (#1636).
    services.AddWhizbangSchemaReadyGate();
    ArgumentNullException.ThrowIfNull(services);

    var receptorTypes = _snapshotReceptorMessageTypes(services);
    services.AddSingleton(new HandledReceptorTypeSnapshot(receptorTypes));
    services.TryAddWhizbangDefaults();
    services.AddWhizbangWorker<OrphanInboxJanitor>();
    return services;
  }

  private static HashSet<Type> _snapshotReceptorMessageTypes(IServiceCollection services) {
    return [.. services
      .Select(sd => sd.ServiceType)
      .Where(st => st.IsGenericType && st.GetGenericTypeDefinition() is var def && (def == typeof(IReceptor<>) || def == typeof(IReceptor<,>)))
      .Select(st => st.GetGenericArguments())
      .Where(args => args.Length > 0)
      .Select(args => args[0])];
  }
}
