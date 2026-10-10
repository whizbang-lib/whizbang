// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Workers;

namespace Whizbang.Data.Postgres;

/// <summary>
/// Registers the one schema initializer every driver shares.
/// </summary>
/// <docs>data/turnkey-initialization</docs>
/// <tests>tests/Whizbang.Core.Tests/DependencyInjection/SchemaInitializationRegistrationTests.cs</tests>
/// <tests>tests/Whizbang.Core.Component.Tests/Schema/SchemaInitializationRunnersTests.cs</tests>
public static class SchemaInitializationRegistration {
  /// <summary>
  /// Ensures the schema initializer and the schema-ready gate it opens are registered. Idempotent, so every
  /// driver calls it and a host that composes two still starts one initializer.
  /// </summary>
  /// <param name="services">The service collection.</param>
  /// <returns>The same collection, for chaining.</returns>
  /// <remarks>
  /// The initializer is constructed by a factory naming its constructor, not activated by type, so registering it
  /// involves no reflection. It runs every <see cref="ISchemaInitializationRunner"/> registered when it starts.
  /// </remarks>
  public static IServiceCollection AddWhizbangSchemaInitialization(this IServiceCollection services) {
    ArgumentNullException.ThrowIfNull(services);

    services.AddWhizbangSchemaReadyGate();
    services.AddLogging();
    services.AddOptions();
    services.TryAddSingleton(TimeProvider.System);
    services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, WhizbangDatabaseInitializerService>(sp =>
      new WhizbangDatabaseInitializerService(
        sp,
        sp.GetServices<ISchemaInitializationRunner>(),
        sp.GetRequiredService<ISchemaReadyGate>(),
        sp.GetRequiredService<IOptions<ClaimWorkerOptions>>(),
        sp.GetRequiredService<IOptions<SchemaInitializationOptions>>(),
        sp.GetRequiredService<TimeProvider>(),
        sp.GetRequiredService<ILogger<WhizbangDatabaseInitializerService>>())));
    return services;
  }
}
