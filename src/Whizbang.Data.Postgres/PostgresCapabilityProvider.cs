// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;
using Whizbang.Core.Capabilities;

namespace Whizbang.Data.Postgres;

/// <summary>A database the Postgres provider probes, and how to open a connection to it.</summary>
/// <param name="Name">The scope it is reported under (the driver's name for the database).</param>
/// <param name="OpenConnectionAsync">Opens a connection the probe disposes.</param>
internal sealed record PostgresCapabilityDatabase(
  string Name,
  Func<CancellationToken, ValueTask<NpgsqlConnection>> OpenConnectionAsync);

/// <summary>
/// Postgres extensions as capabilities: one descriptor per catalog entry, one scope per registered
/// database, and a probe that reads each database with one query and maps each extension's Postgres state
/// onto the generic state, keeping the Postgres state as the detail and the installed version.
/// </summary>
/// <docs>operations/infrastructure/postgres-extensions</docs>
/// <tests>tests/Whizbang.Core.Tests/PostgresExtensions/PostgresCapabilityProviderTests.cs</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/PostgresCapabilityProbeIntegrationTests.cs</tests>
internal sealed class PostgresCapabilityProvider : ICapabilityProvider {
  private readonly Dictionary<string, PostgresCapabilityDatabase> _databases;
  private readonly IReadOnlyList<PostgresExtensionInfo> _catalog;

  /// <summary>Creates the provider over the registered databases; a name registered twice is probed once.</summary>
  public PostgresCapabilityProvider(IEnumerable<PostgresCapabilityDatabase> databases) {
    ArgumentNullException.ThrowIfNull(databases);
    var distinct = databases.DistinctBy(d => d.Name, StringComparer.Ordinal).ToArray();
    _databases = distinct.ToDictionary(d => d.Name, StringComparer.Ordinal);
    Scopes = [.. distinct.Select(d => d.Name)];
    _catalog = PostgresExtensionCatalog.BuiltIn;
    Descriptors = [.. _catalog.Select(PostgresExtensionCatalog.Describe)];
  }

  /// <inheritdoc />
  public string Name => PostgresExtensionCatalog.PROVIDER;

  /// <inheritdoc />
  public IReadOnlyList<CapabilityDescriptor> Descriptors { get; }

  /// <inheritdoc />
  public IReadOnlyList<string> Scopes { get; }

  /// <inheritdoc />
  public async Task<IReadOnlyList<CapabilityStatus>> ProbeAsync(string scope, CancellationToken cancellationToken) {
    var connection = await _databases[scope].OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
    await using (connection.ConfigureAwait(false)) {
      var rows = await PostgresCapabilityProbe.ReadAsync(connection, _catalog, cancellationToken).ConfigureAwait(false);
      return [.. _catalog.Select((extension, i) => Status(Descriptors[i], scope, extension, rows[extension.Name]))];
    }
  }

  /// <summary>
  /// One extension's status from what the probe read: the generic state, the installed version, the version an
  /// install or update reaches, and the Postgres state as the detail.
  /// </summary>
  internal static CapabilityStatus Status(
      CapabilityDescriptor descriptor, string scope, PostgresExtensionInfo extension, PostgresExtensionProbeRow row) {
    var state = PostgresCapabilityProbe.DeriveState(extension, row);
    return new CapabilityStatus(descriptor, scope, PostgresCapabilityProbe.ToCapabilityState(state),
      row.InstalledVersion, state.ToString(), Reason: null) {
      AvailableVersion = row.DefaultVersion,
    };
  }
}

/// <summary>
/// Registration of Postgres extensions as capabilities. Both Postgres drivers call it for their database,
/// so a host gets the probe, the verdict and the gauge with no wiring of its own.
/// </summary>
/// <docs>operations/infrastructure/postgres-extensions</docs>
/// <tests>tests/Whizbang.Core.Tests/PostgresExtensions/PostgresCapabilityProviderTests.cs</tests>
public static class PostgresCapabilityServiceCollectionExtensions {
  /// <summary>
  /// Registers <paramref name="database"/> as a scope of the Postgres capability provider, and once per
  /// container the provider, its provisioner (<c>CREATE EXTENSION</c> and <c>ALTER EXTENSION ... UPDATE</c>) and
  /// capability detection (<c>AddWhizbangCapabilities</c>). Safe to call more
  /// than once; a database registered twice is probed once.
  /// </summary>
  /// <param name="services">The service collection.</param>
  /// <param name="database">The name the database is reported under.</param>
  /// <param name="openConnection">
  /// Opens a connection to it, from the container, when the probe runs (not at registration), so a data
  /// source is only resolved once the schema is ready.
  /// </param>
  /// <returns>The same collection.</returns>
  public static IServiceCollection AddWhizbangPostgresCapabilities(
      this IServiceCollection services,
      string database,
      Func<IServiceProvider, CancellationToken, ValueTask<NpgsqlConnection>> openConnection) {
    ArgumentNullException.ThrowIfNull(services);
    ArgumentException.ThrowIfNullOrWhiteSpace(database);
    ArgumentNullException.ThrowIfNull(openConnection);

    services.AddSingleton(sp => new PostgresCapabilityDatabase(database, ct => openConnection(sp, ct)));
    services.TryAddEnumerable(ServiceDescriptor.Singleton<ICapabilityProvider, PostgresCapabilityProvider>(
      sp => new PostgresCapabilityProvider(sp.GetServices<PostgresCapabilityDatabase>())));
    services.TryAddEnumerable(ServiceDescriptor.Singleton<ICapabilityProvisioner, PostgresCapabilityProvisioner>(
      sp => new PostgresCapabilityProvisioner(
        sp.GetServices<PostgresCapabilityDatabase>(), sp.GetRequiredService<ILogger<PostgresCapabilityProvisioner>>())));
    return services.AddWhizbangCapabilities();
  }

  /// <summary>
  /// Puts a context's schema requirements in force on its own database: the requirement source the generated code
  /// keyed by <paramref name="contextKey"/>, scoped to <paramref name="database"/>. A context with no keyed source
  /// requires nothing.
  /// </summary>
  /// <param name="services">The service collection.</param>
  /// <param name="contextKey">The key the generated code registered the context's requirements under (its type).</param>
  /// <param name="database">The name the database is probed under, as given to <see cref="AddWhizbangPostgresCapabilities"/>.</param>
  /// <returns>The same collection.</returns>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Capabilities/TwoDatabaseHostRequirementTests.cs</tests>
  public static IServiceCollection AddWhizbangPostgresSchemaRequirements(
      this IServiceCollection services, object contextKey, string database) {
    ArgumentNullException.ThrowIfNull(services);
    ArgumentNullException.ThrowIfNull(contextKey);
    ArgumentException.ThrowIfNullOrWhiteSpace(database);
    services.AddSingleton<ICapabilityRequirementSource>(sp => new ScopedCapabilityRequirementSource(
      sp.GetKeyedService<ICapabilityRequirementSource>(contextKey) ?? new FixedCapabilityRequirementSource([]), database));
    return services;
  }
}
