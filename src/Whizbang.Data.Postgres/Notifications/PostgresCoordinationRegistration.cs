// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Whizbang.Core.Notifications;
using Whizbang.Core.Signals;
using Whizbang.Core.Workers;

namespace Whizbang.Data.Postgres.Notifications;

/// <summary>
/// The coordination every Postgres driver registers, through this one method so no driver can drift from another
/// (#2559): role assignment (the elector, the duty holder loop and its owed-work store) and the elected partition
/// assigner (its store, the claimers' cached assignment, its subscription and its worker). The published partition
/// assignment is the one source of stream ownership, so every driver runs the assigner.
/// </summary>
/// <docs>fundamentals/work-coordinator/partition-assignment</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/DriverCoordinationParityTests.cs:EveryPieceOfTheSharedCoordination_IsRegisteredAlikeByBothDriversAsync</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperCoordinationTests.cs:AddWhizbangPostgres_RegistersRoleAssignmentAndTheElectedAssignerAsync</tests>
internal static class PostgresCoordinationRegistration {
  /// <summary>
  /// Registers role assignment and the elected partition assigner, over whatever connection the notification options
  /// resolve. Safe to call more than once.
  /// </summary>
  /// <param name="services">The service collection.</param>
  /// <returns>The same collection.</returns>
  internal static IServiceCollection AddWhizbangPostgresCoordination(this IServiceCollection services) {
    services.AddOptions<WhizbangNotificationOptions>();
    // The defaults the core registers, for a driver registered without it; each gives way to the real one, in either
    // order: a signal bus, and this instance's connection mode (a driver with an alive-lock registers its own).
    services.TryAddSingleton<ISignalBus>(NullSignalBus.Instance);
    services.TryAddSingleton<IInstanceConnectionModeSource>(NullInstanceConnectionModeSource.Instance);
    services.AddWhizbangRoleAssignmentByDefault();
    services.AddWhizbangPartitionAssigner();
    return services;
  }

  /// <summary>
  /// The coordination over a driver's own connection string, for a driver without a notification connection of its
  /// own (Dapper): the string answers when no notification configuration does, as the EF Core driver's context does.
  /// </summary>
  /// <param name="services">The service collection.</param>
  /// <param name="connectionString">The driver's connection string.</param>
  /// <returns>The same collection.</returns>
  internal static IServiceCollection AddWhizbangPostgresCoordination(this IServiceCollection services, string connectionString) {
    ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
    services.TryAddSingleton<INotificationConnectionStringFallback>(new DriverConnectionStringFallback(connectionString));
    return services.AddWhizbangPostgresCoordination();
  }
}

/// <summary>A driver's own connection string, as the last resort for the coordination's connections.</summary>
/// <param name="connectionString">The driver's connection string.</param>
/// <docs>fundamentals/work-coordinator/partition-assignment</docs>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperCoordinationTests.cs:AddWhizbangPostgres_RegistersRoleAssignmentAndTheElectedAssignerAsync</tests>
internal sealed class DriverConnectionStringFallback(string connectionString) : INotificationConnectionStringFallback {
  /// <inheritdoc />
  public string? GetConnectionString() => connectionString;
}
