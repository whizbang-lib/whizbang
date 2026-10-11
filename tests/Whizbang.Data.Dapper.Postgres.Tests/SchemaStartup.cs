// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// Runs what a host runs at start for the schema: the shared initializer, inline, so the call returns once the
/// gate is open (or throws what initialization threw).
/// </summary>
internal static class SchemaStartup {
  /// <summary>Builds the provider and starts its schema initializer inline.</summary>
  /// <param name="services">A collection the Dapper driver was registered in.</param>
  /// <returns>The started provider; the caller disposes it.</returns>
  public static async Task<ServiceProvider> StartAsync(IServiceCollection services) {
    services.AddLogging();
    services.Configure<SchemaInitializationOptions>(o => o.NonBlockingSchemaInit = false);
    var provider = services.BuildServiceProvider();
    await provider.GetServices<IHostedService>().OfType<WhizbangDatabaseInitializerService>().Single()
      .StartAsync(CancellationToken.None);
    return provider;
  }
}
