// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Whizbang.Core.Configuration;
using Whizbang.Core.Offloads;

namespace Whizbang.Core.Minting;

/// <summary>
/// The service-wide default byte limit of a composite batch (#2285): three quarters of the body-offload threshold, so a
/// composite is not offloaded for its size and, since the threshold is set below the transport's largest message, not
/// refused for it either. Applied when <c>Whizbang:CompositeBatches:MaxBufferedBytes</c> is not set, and reported with
/// its input.
/// </summary>
/// <remarks>
/// The transport's own limit is not read here: resolving the transport initializes it (an Azure Service Bus transport
/// connects), which options must never do.
/// </remarks>
/// <param name="offload">The body-offload settings.</param>
/// <docs>fundamentals/messaging/composite-events#streaming-events-into-composites</docs>
/// <tests>tests/Whizbang.Core.Tests/Minting/CompositeBatchDefaultsTests.cs</tests>
internal sealed class CompositeBatchDefaults(IOptions<MessageBodyOffloadOptions> offload)
    : IPostConfigureOptions<CompositeBatchOptions> {
  private const string MAX_BYTES_KEY = CompositeBatchOptions.SECTION + ":MaxBufferedBytes";

  /// <summary>The byte limit this derived, or null when the setting was configured.</summary>
  public DerivedSetting<long>? Derived { get; private set; }

  /// <inheritdoc />
  public void PostConfigure(string? name, CompositeBatchOptions options) {
    if (options.MaxBufferedBytes is not null) {
      return;
    }
    Derived = Derive(offload.Value.SizeThresholdBytes);
    options.MaxBufferedBytes = Derived.Value;
  }

  /// <summary>Three quarters of the body-offload threshold, leaving room for the composite's own envelope.</summary>
  internal static DerivedSetting<long> Derive(long offloadThreshold) =>
    DerivedSetting.Of(MAX_BYTES_KEY, offloadThreshold * 3 / 4, "0.75 x SizeThresholdBytes",
      SettingInput.Of("SizeThresholdBytes", offloadThreshold));
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
    services.TryAddSingleton(sp => new CompositeBatchDefaults(sp.GetRequiredService<IOptions<MessageBodyOffloadOptions>>()));
    services.TryAddEnumerable(ServiceDescriptor.Singleton<IPostConfigureOptions<CompositeBatchOptions>, CompositeBatchDefaults>(
      sp => sp.GetRequiredService<CompositeBatchDefaults>()));
    services.TryAddEnumerable(ServiceDescriptor.Singleton<IDerivedSettingSource, CompositeBatchDerivedSettings>(
      sp => new CompositeBatchDerivedSettings(sp.GetRequiredService<IOptions<CompositeBatchOptions>>(), sp.GetRequiredService<CompositeBatchDefaults>())));
    return services;
  }
}
