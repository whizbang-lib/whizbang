// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Whizbang.Core.Configuration;
using Whizbang.Core.Offloads;
using Whizbang.Core.Transports;

namespace Whizbang.Core.Minting;

/// <summary>
/// The service-wide default byte limit of a composite batch (#2285): three quarters of the smaller of the body-offload
/// threshold and the transport's largest message, so a composite is neither offloaded nor refused for its size. Applied
/// when <c>Whizbang:CompositeBatches:MaxBufferedBytes</c> is not set, and reported with its inputs.
/// </summary>
/// <param name="offload">The body-offload settings.</param>
/// <param name="transport">The transport, or null when the host has none.</param>
/// <docs>fundamentals/messaging/composite-events#streaming-events-into-composites</docs>
/// <tests>tests/Whizbang.Core.Tests/Minting/CompositeBatchDefaultsTests.cs</tests>
internal sealed class CompositeBatchDefaults(IOptions<MessageBodyOffloadOptions> offload, ITransport? transport)
    : IPostConfigureOptions<CompositeBatchOptions> {
  private const string MAX_BYTES_KEY = CompositeBatchOptions.SECTION + ":MaxBufferedBytes";

  /// <summary>The byte limit this derived, or null when the setting was configured.</summary>
  public DerivedSetting<long>? Derived { get; }

  /// <inheritdoc />
  public void PostConfigure(string? name, CompositeBatchOptions options) => _ = (name, options, offload, transport);

  /// <summary>Three quarters of the smaller of the offload threshold and the transport's largest message (when it has one).</summary>
  internal static DerivedSetting<long> Derive(long offloadThreshold, long? transportMax) =>
    DerivedSetting.Of(MAX_BYTES_KEY, offloadThreshold + (transportMax ?? 0), "stub");
}

/// <summary>Supplies the derived byte limit to the startup settings report, once the options have been built.</summary>
/// <param name="options">The options, built so the default is derived.</param>
/// <param name="defaults">What derived it.</param>
/// <docs>fundamentals/messaging/composite-events#streaming-events-into-composites</docs>
/// <tests>tests/Whizbang.Core.Tests/Minting/CompositeBatchDefaultsTests.cs</tests>
internal sealed class CompositeBatchDerivedSettings(IOptions<CompositeBatchOptions> options, CompositeBatchDefaults defaults)
    : IDerivedSettingSource {
  /// <inheritdoc />
  public IEnumerable<IDerivedSetting> DerivedSettings {
    get {
      _ = options.Value;
      return defaults.Derived is { } derived ? [derived] : [];
    }
  }
}

/// <summary>
/// Binds <see cref="CompositeBatchOptions"/> from <c>Whizbang:CompositeBatches</c>. The section is named literally at the
/// call so the configuration binder source generator compiles the bind to typed assignments.
/// </summary>
/// <docs>fundamentals/messaging/composite-events#streaming-events-into-composites</docs>
/// <tests>tests/Whizbang.Core.Tests/Minting/CompositeBatchDefaultsTests.cs</tests>
internal sealed class ConfigureCompositeBatchOptions(IConfiguration? configuration) : IConfigureOptions<CompositeBatchOptions> {
  public void Configure(CompositeBatchOptions options) {
    if (configuration is null) {
      return;
    }
#pragma warning disable IL2026, IL3050 // intercepted: the binder source generator compiles this call to typed assignments (BindingExtensions.g.cs); format's analyzer pass does not see the generator's suppressor
    configuration.GetSection("Whizbang:CompositeBatches").Bind(options);
#pragma warning restore IL2026, IL3050
  }
}

/// <summary>Registers the service-wide composite batch settings (#2285).</summary>
/// <docs>fundamentals/messaging/composite-events#streaming-events-into-composites</docs>
/// <tests>tests/Whizbang.Core.Tests/Minting/CompositeBatchDefaultsTests.cs</tests>
internal static class CompositeBatchServiceCollectionExtensions {
  /// <summary>
  /// Binds <c>Whizbang:CompositeBatches</c>, derives the byte limit when it is not set, and reports it. Every call after the
  /// first changes nothing.
  /// </summary>
  internal static IServiceCollection AddWhizbangCompositeBatches(this IServiceCollection services) {
    services.AddOptions<CompositeBatchOptions>();
    services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigureOptions<CompositeBatchOptions>, ConfigureCompositeBatchOptions>(
      sp => new ConfigureCompositeBatchOptions(sp.GetService<IConfiguration>())));
    services.TryAddSingleton(sp => new CompositeBatchDefaults(
      sp.GetRequiredService<IOptions<MessageBodyOffloadOptions>>(), sp.GetService<ITransport>()));
    services.TryAddEnumerable(ServiceDescriptor.Singleton<IPostConfigureOptions<CompositeBatchOptions>, CompositeBatchDefaults>(
      sp => sp.GetRequiredService<CompositeBatchDefaults>()));
    services.TryAddEnumerable(ServiceDescriptor.Singleton<IDerivedSettingSource, CompositeBatchDerivedSettings>(
      sp => new CompositeBatchDerivedSettings(sp.GetRequiredService<IOptions<CompositeBatchOptions>>(), sp.GetRequiredService<CompositeBatchDefaults>())));
    return services;
  }
}
