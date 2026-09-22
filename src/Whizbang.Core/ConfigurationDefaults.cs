using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Whizbang.Core;

/// <summary>
/// A generic host registers <see cref="IConfiguration"/>; a bare service collection does not. The
/// framework types that read <c>Whizbang:*</c> keys take the configuration as a required dependency,
/// so every entry point that can stand alone registers an empty root first: every key then reads as
/// absent, which is what an unconfigured host meant before.
/// </summary>
internal static class ConfigurationDefaults {
  /// <summary>Registers an empty configuration root unless the host already registered one.</summary>
  public static IServiceCollection TryAddEmptyConfiguration(this IServiceCollection services) {
    services.TryAddSingleton<IConfiguration>(_ => new ConfigurationBuilder().Build());
    return services;
  }
}
