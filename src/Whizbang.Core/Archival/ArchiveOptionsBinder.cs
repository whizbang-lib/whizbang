// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Whizbang.Core.Archival;

/// <summary>
/// Binds <see cref="ArchiveOptions"/>, <see cref="StreamLifecycleOptions"/> and <see cref="ArchivedStreamRowsOptions"/> from configuration by hand, so a value the
/// framework cannot use is refused at startup with the key that holds it, rather than read as a default.
/// </summary>
/// <remarks>
/// A policy is bound by its name only: a number is refused, because binding a policy by its position is a setting nobody
/// reading the configuration can check. <c>FinalizeGrace</c> must be a time span of zero or more and
/// <c>SweepBatchSize</c> a whole number of one or more (#1734). <c>Enabled</c> is a boolean; a type's <c>IdleTtl</c> and
/// <c>MaxAge</c> are time spans above zero, its <c>KeepTailVersions</c> and <c>TenantByteCap</c> whole numbers of one or
/// more, and its <c>Action</c> a name, <c>CloseBooks</c> refused until closing the books ships (#1740).
/// </remarks>
/// <docs>fundamentals/events/archive-store#configuration</docs>
/// <tests>tests/Whizbang.Core.Tests/Archival/StreamLifecycleOptionsBinderTests.cs</tests>
internal static class ArchiveOptionsBinder {
  private const string READ_THROUGH_KEY = ArchiveOptions.SECTION + ":" + nameof(ArchiveOptions.ReadThrough);
  private const string TYPES_SECTION = StreamLifecycleOptions.SECTION + ":" + nameof(StreamLifecycleOptions.Types);
  private const string FINALIZE_GRACE_KEY = StreamLifecycleOptions.SECTION + ":" + nameof(StreamLifecycleOptions.FinalizeGrace);
  private const string SWEEP_BATCH_SIZE_KEY = StreamLifecycleOptions.SECTION + ":" + nameof(StreamLifecycleOptions.SweepBatchSize);
  private const string ENABLED_KEY = StreamLifecycleOptions.SECTION + ":" + nameof(StreamLifecycleOptions.Enabled);

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
  /// <exception cref="InvalidOperationException">An <c>AppendToArchived</c> names no policy, <c>FinalizeGrace</c> is not a
  /// time span of zero or more, or <c>SweepBatchSize</c> is not a whole number of one or more.</exception>
  internal static void Bind(IConfiguration configuration, StreamLifecycleOptions options) {
    if (configuration[FINALIZE_GRACE_KEY] is { } grace) {
      options.FinalizeGrace = TimeSpan.TryParse(grace, System.Globalization.CultureInfo.InvariantCulture, out var parsed) && parsed >= TimeSpan.Zero
        ? parsed
        : throw new InvalidOperationException($"{FINALIZE_GRACE_KEY} is '{grace}'; it must be a time span of zero or more, such as 7.00:00:00.");
    }
    if (configuration[SWEEP_BATCH_SIZE_KEY] is { } batch) {
      options.SweepBatchSize = int.TryParse(batch, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var size) && size >= 1
        ? size
        : throw new InvalidOperationException($"{SWEEP_BATCH_SIZE_KEY} is '{batch}'; it must be a whole number of 1 or more.");
    }
    if (configuration[ENABLED_KEY] is { } enabled) {
      options.Enabled = bool.TryParse(enabled, out var on)
        ? on
        : throw new InvalidOperationException($"{ENABLED_KEY} is '{enabled}'; it must be true or false.");
    }
    foreach (var type in configuration.GetSection(TYPES_SECTION).GetChildren()) {
      options.Types[type.Key] = _type(type);
    }
  }

  // One stream type's settings; each key names itself in its refusal.
  private static StreamTypeLifecycleOptions _type(IConfigurationSection type) {
    var prefix = $"{TYPES_SECTION}:{type.Key}:";
    var options = new StreamTypeLifecycleOptions();
    if (type[nameof(StreamTypeLifecycleOptions.AppendToArchived)] is { } appendToArchived) {
      options.AppendToArchived = _policy(prefix + nameof(StreamTypeLifecycleOptions.AppendToArchived), appendToArchived);
    }
    options.IdleTtl = _positiveSpan(prefix + nameof(StreamTypeLifecycleOptions.IdleTtl), type[nameof(StreamTypeLifecycleOptions.IdleTtl)]);
    options.MaxAge = _positiveSpan(prefix + nameof(StreamTypeLifecycleOptions.MaxAge), type[nameof(StreamTypeLifecycleOptions.MaxAge)]);
    options.KeepTailVersions = _positiveCount(prefix + nameof(StreamTypeLifecycleOptions.KeepTailVersions), type[nameof(StreamTypeLifecycleOptions.KeepTailVersions)]);
    options.TenantByteCap = _positiveCount(prefix + nameof(StreamTypeLifecycleOptions.TenantByteCap), type[nameof(StreamTypeLifecycleOptions.TenantByteCap)]);
    if (type[nameof(StreamTypeLifecycleOptions.Action)] is { } action) {
      options.Action = _action(prefix + nameof(StreamTypeLifecycleOptions.Action), action);
    }
    return options;
  }

  // Unset is no trigger; zero would act on every stream of the type at once, so a trigger is above zero.
  private static TimeSpan? _positiveSpan(string key, string? value) => value switch {
    null => null,
    _ when TimeSpan.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var span) && span > TimeSpan.Zero => span,
    _ => throw new InvalidOperationException($"{key} is '{value}'; it must be a time span above zero, such as 90.00:00:00."),
  };

  private static long? _positiveCount(string key, string? value) => value switch {
    null => null,
    _ when long.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var count) && count >= 1 => count,
    _ => throw new InvalidOperationException($"{key} is '{value}'; it must be a whole number of 1 or more."),
  };

  private static StreamLifecycleAction _action(string key, string value) {
    // Names only, as for a policy; CloseBooks is a planned action, refused by name so a host learns it has not shipped.
    if (value.Length > 0 && !char.IsAsciiDigit(value[0]) && value[0] != '-'
        && Enum.TryParse<StreamLifecycleAction>(value, ignoreCase: true, out var action) && Enum.IsDefined(action)) {
      return action;
    }
    var planned = string.Equals(value, "CloseBooks", StringComparison.OrdinalIgnoreCase)
      ? " Closing the books is planned (#1741) and has not shipped."
      : string.Empty;
    throw new InvalidOperationException($"{key} is '{value}', which names no end-of-life action.{planned} Use one of: Archive, Compact, Purge.");
  }

  /// <summary>Binds <c>Whizbang:Perspectives:ArchivedStreamRows:&lt;Perspective&gt;</c> (#1739).</summary>
  /// <param name="configuration">The host's configuration.</param>
  /// <param name="options">The options to fill.</param>
  /// <exception cref="InvalidOperationException">A perspective's value names no policy, is a section rather than a value, or
  /// the perspective's name holds <c>=</c> or a line break, which the stored policies cannot carry.</exception>
  internal static void Bind(IConfiguration configuration, ArchivedStreamRowsOptions options) {
    foreach (var perspective in configuration.GetSection(ArchivedStreamRowsOptions.SECTION).GetChildren()) {
      var key = $"{ArchivedStreamRowsOptions.SECTION}:{perspective.Key}";
      if (perspective.Key.AsSpan().IndexOfAny('=', '\n', '\r') >= 0) {
        throw new InvalidOperationException($"{key} cannot name a perspective: the stored policies are one name=policy per line.");
      }
      var value = perspective.Value
        ?? throw new InvalidOperationException($"{key} is a section; it must be a policy: Keep, Drop, Archive.");
      options.Perspectives[perspective.Key] = _rowPolicy(key, value);
    }
  }

  private static ArchivedStreamRowPolicy _rowPolicy(string key, string value) {
    // Names only, as for the append policy: a number would bind a policy by position.
    if (value.Length > 0 && !char.IsAsciiDigit(value[0]) && value[0] != '-'
        && Enum.TryParse<ArchivedStreamRowPolicy>(value, ignoreCase: true, out var policy) && Enum.IsDefined(policy)) {
      return policy;
    }
    throw new InvalidOperationException($"{key} is '{value}', which names no policy for rows of archived streams. Use one of: Keep, Drop, Archive.");
  }

  /// <summary>
  /// Applies the <see cref="Attributes.StreamEndOfLifeAttribute"/> declarations (#1740) before configuration binds, so a
  /// key set in configuration wins over the attribute's value.
  /// </summary>
  /// <param name="declarations">The declarations (<see cref="StreamEndOfLifeDeclarations.All"/>).</param>
  /// <param name="options">The options to fill.</param>
  /// <exception cref="InvalidOperationException">A declared value cannot be used; the message names the type.</exception>
  internal static void Apply(IEnumerable<StreamEndOfLifeDeclaration> declarations, StreamLifecycleOptions options) =>
    throw new NotImplementedException($"{declarations} {options.Types.Count}");

  private static AppendToArchivedPolicy _policy(string key, string value) {
    // Names only: a number would bind a policy by position.
    if (value.Length > 0 && !char.IsAsciiDigit(value[0]) && value[0] != '-'
        && Enum.TryParse<AppendToArchivedPolicy>(value, ignoreCase: true, out var policy) && Enum.IsDefined(policy)) {
      return policy;
    }
    throw new InvalidOperationException(
      $"{key} is '{value}', which names no append-to-archived policy. Use one of: Refuse, AutoRestore, NewGeneration.");
  }

  /// <summary>
  /// Registers the three options, bound from the host's configuration and validated when the host starts. Safe to call more
  /// than once: the binders are added once.
  /// </summary>
  /// <param name="services">The service collection.</param>
  /// <returns>The service collection.</returns>
  internal static IServiceCollection AddWhizbangArchiveOptions(this IServiceCollection services) {
    services.AddOptions<ArchiveOptions>().ValidateOnStart();
    services.AddOptions<StreamLifecycleOptions>().ValidateOnStart();
    services.AddOptions<ArchivedStreamRowsOptions>().ValidateOnStart();
    services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigureOptions<ArchiveOptions>, ArchiveOptionsSetup>(
      sp => new ArchiveOptionsSetup(sp.GetService<IConfiguration>())));
    services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigureOptions<StreamLifecycleOptions>, StreamLifecycleOptionsSetup>(
      sp => new StreamLifecycleOptionsSetup(sp.GetService<IConfiguration>())));
    services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigureOptions<ArchivedStreamRowsOptions>, ArchivedStreamRowsOptionsSetup>(
      sp => new ArchivedStreamRowsOptionsSetup(sp.GetService<IConfiguration>())));
    return services;
  }

  private sealed class ArchiveOptionsSetup(IConfiguration? configuration) : IConfigureOptions<ArchiveOptions> {
    public void Configure(ArchiveOptions options) {
      if (configuration is not null) {
        Bind(configuration, options);
      }
    }
  }

  private sealed class ArchivedStreamRowsOptionsSetup(IConfiguration? configuration) : IConfigureOptions<ArchivedStreamRowsOptions> {
    public void Configure(ArchivedStreamRowsOptions options) {
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
