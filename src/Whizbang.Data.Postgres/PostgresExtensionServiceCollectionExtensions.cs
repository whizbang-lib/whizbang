// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Whizbang.Core.Observability;
using Whizbang.Core.Startup;

namespace Whizbang.Data.Postgres;

/// <summary>
/// Registration of the Postgres extension probe. Both Postgres drivers call it for their database, so a
/// host gets the probe, the startup report and the gauge with no wiring of its own.
/// </summary>
/// <docs>operations/infrastructure/postgres-extensions</docs>
/// <tests>tests/Whizbang.Core.Tests/PostgresExtensions/PostgresExtensionServiceCollectionExtensionsTests.cs</tests>
public static class PostgresExtensionServiceCollectionExtensions {
  /// <summary>
  /// Registers <paramref name="database"/> for the extension probe, and once per container the
  /// <see cref="IPostgresCapabilities"/> singleton (with its gauge), the <c>PostgresExtensions</c>
  /// startup step and the <c>Whizbang:PostgresExtensions</c> binding. Safe to call more than once; a
  /// database registered twice is probed once.
  /// </summary>
  /// <param name="services">The service collection.</param>
  /// <param name="database">The name the database is reported under.</param>
  /// <param name="openConnection">
  /// Opens a connection to it, from the container, when the probe runs (not at registration), so a data
  /// source is only resolved once the schema is ready.
  /// </param>
  /// <returns>The same collection.</returns>
  public static IServiceCollection AddWhizbangPostgresExtensionProbe(
      this IServiceCollection services,
      string database,
      Func<IServiceProvider, CancellationToken, ValueTask<NpgsqlConnection>> openConnection) {
    ArgumentNullException.ThrowIfNull(services);
    ArgumentException.ThrowIfNullOrWhiteSpace(database);
    ArgumentNullException.ThrowIfNull(openConnection);

    services.AddSingleton(sp => new PostgresExtensionProbeTarget(database, ct => openConnection(sp, ct)));

    services.AddOptions<PostgresExtensionsOptions>();
    services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigureOptions<PostgresExtensionsOptions>, ConfigurePostgresExtensionsOptions>(
      sp => new ConfigurePostgresExtensionsOptions(sp.GetService<IConfiguration>())));

    services.AddMetrics();
    services.TryAddSingleton<WhizbangMetrics>();
    services.TryAddSingleton<IPostgresCapabilities>(sp => new PostgresCapabilities(
      sp.GetServices<PostgresExtensionProbeTarget>(),
      sp.GetRequiredService<ILogger<PostgresCapabilities>>(),
      sp.GetRequiredService<WhizbangMetrics>()));
    services.TryAddEnumerable(ServiceDescriptor.Singleton<IStartupStep, PostgresExtensionsStartupStep>(sp =>
      new PostgresExtensionsStartupStep(
        sp.GetRequiredService<IPostgresCapabilities>(),
        sp.GetRequiredService<IOptions<PostgresExtensionsOptions>>(),
        sp.GetRequiredService<ILogger<PostgresExtensionsStartupStep>>())));
    return services;
  }
}

/// <summary>
/// Binds <see cref="PostgresExtensionsOptions"/> from <c>Whizbang:PostgresExtensions</c>. The section
/// is named literally at the call so the configuration binder source generator compiles the bind to
/// typed assignments: no reflection reaches the AOT path. A host without configuration keeps the code
/// values.
/// </summary>
/// <docs>operations/infrastructure/postgres-extensions#configuration</docs>
/// <tests>tests/Whizbang.Core.Tests/PostgresExtensions/PostgresExtensionServiceCollectionExtensionsTests.cs</tests>
internal sealed class ConfigurePostgresExtensionsOptions(IConfiguration? configuration) : IConfigureOptions<PostgresExtensionsOptions> {
  public void Configure(PostgresExtensionsOptions options) {
    if (configuration is null) {
      return;
    }
#pragma warning disable IL2026, IL3050 // intercepted: the binder source generator compiles this call to typed assignments (BindingExtensions.g.cs); format's analyzer pass does not see the generator's suppressor
    ConfigurationBinder.Bind(configuration.GetSection("Whizbang:PostgresExtensions"), options);
#pragma warning restore IL2026, IL3050
  }
}
