// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Whizbang.Core.Archival;

/// <summary>
/// Binds <see cref="ArchiveOptions"/> and <see cref="StreamLifecycleOptions"/> from configuration by hand, so a value the
/// framework cannot use is refused at startup with the key that holds it, rather than read as a default.
/// </summary>
/// <remarks>
/// <c>NewGeneration</c> is refused with its own message: it is the third append-to-archived policy the design names, and it
/// ships with the final snapshot finalization takes (#1734). A number is refused too, because binding a policy by its
/// position is a setting nobody reading the configuration can check.
/// </remarks>
/// <docs>fundamentals/events/archive-store#configuration</docs>
/// <tests>tests/Whizbang.Core.Tests/Archival/StreamLifecycleOptionsBinderTests.cs</tests>
internal static class ArchiveOptionsBinder {
  private const string READ_THROUGH_KEY = ArchiveOptions.SECTION + ":" + nameof(ArchiveOptions.ReadThrough);
  private const string TYPES_SECTION = StreamLifecycleOptions.SECTION + ":" + nameof(StreamLifecycleOptions.Types);
  private const string NEW_GENERATION = "NewGeneration";

  /// <summary>Binds <c>Whizbang:Archive</c>.</summary>
  /// <param name="configuration">The host's configuration.</param>
  /// <param name="options">The options to fill.</param>
  /// <exception cref="InvalidOperationException"><c>ReadThrough</c> is set to something that is not a boolean.</exception>
  internal static void Bind(IConfiguration configuration, ArchiveOptions options) {
    var value = configuration[READ_THROUGH_KEY];
    if (value is null) {
      return;
    }
    options.ReadThrough = bool.TryParse(value, out var readThrough)
      ? readThrough
      : throw new InvalidOperationException($"{READ_THROUGH_KEY} is '{value}'; it must be true or false.");
  }

  /// <summary>Binds <c>Whizbang:StreamLifecycle</c>.</summary>
  /// <param name="configuration">The host's configuration.</param>
  /// <param name="options">The options to fill.</param>
  /// <exception cref="InvalidOperationException">An <c>AppendToArchived</c> names no policy that ships.</exception>
  internal static void Bind(IConfiguration configuration, StreamLifecycleOptions options) {
    foreach (var type in configuration.GetSection(TYPES_SECTION).GetChildren()) {
      var typeOptions = new StreamTypeLifecycleOptions();
      var value = type[nameof(StreamTypeLifecycleOptions.AppendToArchived)];
      if (value is not null) {
        typeOptions.AppendToArchived = _policy($"{TYPES_SECTION}:{type.Key}:{nameof(StreamTypeLifecycleOptions.AppendToArchived)}", value);
      }
      options.Types[type.Key] = typeOptions;
    }
  }

  private static AppendToArchivedPolicy _policy(string key, string value) {
    if (string.Equals(value, NEW_GENERATION, StringComparison.OrdinalIgnoreCase)) {
      throw new InvalidOperationException(
        $"{key} is NewGeneration, which is not available yet: starting a new generation of an archived stream needs the "
        + "final snapshot that finalization takes, and ships with it (#1734). Use Refuse or AutoRestore until then.");
    }
    // Names only: a number would bind a policy by position.
    if (value.Length > 0 && !char.IsAsciiDigit(value[0]) && value[0] != '-'
        && Enum.TryParse<AppendToArchivedPolicy>(value, ignoreCase: true, out var policy) && Enum.IsDefined(policy)) {
      return policy;
    }
    throw new InvalidOperationException(
      $"{key} is '{value}', which names no append-to-archived policy. Use one of: Refuse, AutoRestore.");
  }

  /// <summary>
  /// Registers both options, bound from the host's configuration and validated when the host starts. Safe to call more
  /// than once: the binders are added once.
  /// </summary>
  /// <param name="services">The service collection.</param>
  /// <returns>The service collection.</returns>
  internal static IServiceCollection AddWhizbangArchiveOptions(this IServiceCollection services) {
    services.AddOptions<ArchiveOptions>().ValidateOnStart();
    services.AddOptions<StreamLifecycleOptions>().ValidateOnStart();
    services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigureOptions<ArchiveOptions>, ArchiveOptionsSetup>(
      sp => new ArchiveOptionsSetup(sp.GetService<IConfiguration>())));
    services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigureOptions<StreamLifecycleOptions>, StreamLifecycleOptionsSetup>(
      sp => new StreamLifecycleOptionsSetup(sp.GetService<IConfiguration>())));
    return services;
  }

  private sealed class ArchiveOptionsSetup(IConfiguration? configuration) : IConfigureOptions<ArchiveOptions> {
    public void Configure(ArchiveOptions options) {
      if (configuration is not null) {
        Bind(configuration, options);
      }
    }
  }

  private sealed class StreamLifecycleOptionsSetup(IConfiguration? configuration) : IConfigureOptions<StreamLifecycleOptions> {
    public void Configure(StreamLifecycleOptions options) {
      if (configuration is not null) {
        Bind(configuration, options);
      }
    }
  }
}
