// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Observability;

namespace Whizbang.Data.Postgres.Schema;

/// <summary>
/// What a host adds to a managed-object reconcile, the same on both drivers: the objects its registered
/// contributors create, this instance's id for the fleet gate, and the window a maintenance step claims to re-run
/// the reconcile once per fleet.
/// </summary>
/// <docs>fundamentals/perspectives/managed-schema-objects#every-later-start</docs>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperManagedObjectsStartupTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/ManagedSchemaReconcileStepTests.cs</tests>
public static class ManagedSchemaHostPass {
  /// <summary>How long one instance's claim to run the reconcile step lasts: the fleet reconciles once per window.</summary>
  public static readonly TimeSpan ClaimWindow = TimeSpan.FromMinutes(15);

  /// <summary>The claim key of the window <paramref name="now"/> falls in, for one schema.</summary>
  /// <param name="schema">The schema, unquoted.</param>
  /// <param name="now">When the step runs.</param>
  /// <returns>The key every driver's reconcile step claims.</returns>
  public static string ClaimKey(string schema, DateTimeOffset now) {
    var window = now.UtcTicks - (now.UtcTicks % ClaimWindow.Ticks);
    return string.Create(CultureInfo.InvariantCulture, $"whizbang:managed-schema-objects:{schema}:{window}");
  }

  /// <summary>Adds every registered <see cref="IManagedSchemaObjectContributor"/>'s objects to <paramref name="declared"/>.</summary>
  /// <param name="declared">A fresh declaration set; it is changed and returned.</param>
  /// <param name="services">Where contributors are resolved from; null adds none.</param>
  /// <returns><paramref name="declared"/>.</returns>
  public static ManagedSchemaObjectSet Declared(ManagedSchemaObjectSet declared, IServiceProvider? services) {
    ArgumentNullException.ThrowIfNull(declared);
    foreach (var contributor in services?.GetServices<IManagedSchemaObjectContributor>() ?? []) {
      contributor.Contribute(declared);
    }
    return declared;
  }

  /// <summary>This instance's id, which the fleet gate records declarations under; null when none is registered.</summary>
  /// <param name="services">The host's services; may be null.</param>
  public static Guid? InstanceId(IServiceProvider? services) =>
    services?.GetService<IServiceInstanceProvider>()?.InstanceId;
}
