// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;

namespace Whizbang.Core.Configuration;

/// <summary>Where the value a setting runs with came from.</summary>
/// <docs>operations/configuration/startup-settings-report#what-it-writes</docs>
/// <tests>tests/Whizbang.Core.Tests/Configuration/StartupSettingsReportTests.cs</tests>
public enum SettingSource {
  /// <summary>Nothing set it: the code default applies.</summary>
  Default = 0,

  /// <summary>A configuration provider set it.</summary>
  Configured = 1,

  /// <summary>Computed from inputs that vary from host to host; the entry carries the inputs.</summary>
  Derived = 2,
}

/// <summary>One setting in the startup settings report.</summary>
/// <param name="ManifestKey">The key as the configuration key manifest lists it; <c>*</c> is a name the consumer chooses.</param>
/// <param name="Key">The key itself: the manifest key, or a configured key the manifest key's <c>*</c> matched.</param>
/// <param name="Source">Where the value came from.</param>
/// <param name="Value">The configured value as it may be shown (a sensitive value is redacted); null unless configured.</param>
/// <param name="Provider">The configuration provider that set it; null unless configured.</param>
/// <param name="Derivation">How the value was derived; null unless derived.</param>
/// <docs>operations/configuration/startup-settings-report</docs>
/// <tests>tests/Whizbang.Core.Tests/Configuration/StartupSettingsReportTests.cs</tests>
public sealed record SettingEntry(
    string ManifestKey,
    string Key,
    SettingSource Source,
    string? Value,
    string? Provider,
    IDerivedSetting? Derivation) {
  /// <summary>The entry as the report writes it, named by the last segment of its key.</summary>
  /// <returns>For example <c>MaxBufferedMessages=10000 (configured: JsonConfigurationProvider ...)</c>.</returns>
  public override string ToString() {
    var name = Key[(Key.LastIndexOf(':') + 1)..];
    return Source switch {
      SettingSource.Configured => name + "=" + Value + " (configured: " + Provider + ")",
      SettingSource.Derived => name + "=" + Derivation!.DisplayValue + " (derived: " + DerivedSetting.Describe(Derivation) + ")",
      _ => name + " (default)",
    };
  }
}

/// <summary>The settings of one section of the report, written as one log entry.</summary>
/// <param name="Name">The section: the key without its last segment.</param>
/// <param name="Settings">Its settings, in manifest order.</param>
/// <docs>operations/configuration/startup-settings-report</docs>
/// <tests>tests/Whizbang.Core.Tests/Configuration/StartupSettingsReportTests.cs</tests>
public sealed record SettingsSection(string Name, IReadOnlyList<SettingEntry> Settings) {
  /// <summary>The section's settings as the report writes them, separated by semicolons.</summary>
  /// <returns>For example <c>One=42 (configured: ...); Two (default)</c>.</returns>
  public override string ToString() => string.Join("; ", Settings);
}

/// <summary>
/// Supplies derived settings to the startup settings report. Register one for each component that computes a value
/// from something that varies from host to host; the report shows each value with the inputs it came from.
/// </summary>
/// <docs>operations/configuration/startup-settings-report#derived-settings</docs>
/// <tests>tests/Whizbang.Core.Tests/Configuration/StartupSettingsReportTests.cs</tests>
public interface IDerivedSettingSource {
  /// <summary>The derived settings, as the component runs with them.</summary>
  IEnumerable<IDerivedSetting> DerivedSettings { get; }
}

/// <summary>
/// Builds the startup settings report: one entry for every key in the configuration key manifest, with the value it
/// runs with and where that came from, grouped into one section per settings section.
/// </summary>
/// <remarks>
/// <para>
/// Complete by construction: the report walks the embedded manifest, the same file the configuration key manifest
/// test records from the binders, so a key a binder reads is a key the report shows.
/// </para>
/// <para>
/// Redaction is by type: a key bound to a <see cref="SensitiveString"/> (the embedded sensitive key list, recorded from
/// the options types by the same test), and every connection string, is shown as the sensitive value's redaction. A key
/// whose last segment names a credential is redacted the same way, as a second line of defense.
/// </para>
/// </remarks>
/// <docs>operations/configuration/startup-settings-report</docs>
/// <tests>tests/Whizbang.Core.Tests/Configuration/StartupSettingsReportTests.cs</tests>
internal static class StartupSettingsReport {
  private static readonly string[] _credentialWords =
    ["ConnectionString", "Password", "Secret", "Token", "Key", "Credential", "Credentials"];

  /// <summary>Builds the report.</summary>
  /// <param name="configuration">The host's configuration, or null when it has none (or is not a root, so its providers cannot be named).</param>
  /// <param name="manifest">The keys to report.</param>
  /// <param name="sensitive">The keys bound to a <see cref="SensitiveString"/>.</param>
  /// <param name="derived">Every derived setting.</param>
  /// <param name="defaults">Each key's value when nothing sets it; null shows no default values.</param>
  /// <returns>One section per settings section, ordered by name.</returns>
  public static IReadOnlyList<SettingsSection> Build(
      IConfigurationRoot? configuration,
      KnownConfigurationKeys manifest,
      KnownConfigurationKeys sensitive,
      IEnumerable<IDerivedSetting> derived,
      IReadOnlyDictionary<string, string>? defaults = null) {
    _ = defaults;
    ArgumentNullException.ThrowIfNull(manifest);
    ArgumentNullException.ThrowIfNull(sensitive);
    ArgumentNullException.ThrowIfNull(derived);
    var derivations = new Dictionary<string, IDerivedSetting>(StringComparer.OrdinalIgnoreCase);
    foreach (var setting in derived) {
      derivations[setting.Name] = setting;
    }
    var configured = configuration?.AsEnumerable()
      .Where(static pair => pair.Value is not null)
      .Select(static pair => pair.Key)
      .ToList() ?? [];
    // The last provider that has a key is the one whose value wins, so the search runs from the last.
    var providers = configuration?.Providers.Reverse().ToList() ?? [];

    var entries = new List<SettingEntry>();
    foreach (var manifestKey in manifest.Keys) {
      var keys = KnownConfigurationKeys.IsPattern(manifestKey)
        ? configured.Where(key => KnownConfigurationKeys.Matches(manifestKey, key)).Order(StringComparer.OrdinalIgnoreCase).ToList()
        : [];
      if (keys.Count == 0) {
        keys.Add(manifestKey);
      }
      foreach (var key in keys) {
        entries.Add(_entry(manifestKey, key, configuration, providers, sensitive, derivations));
        derivations.Remove(key);
      }
    }
    // A derived value no key sets (the thread-pool floor) is reported under its own name.
    entries.AddRange(derivations.Values.Select(static d => new SettingEntry(d.Name, d.Name, SettingSource.Derived, null, null, d)));

    return [.. entries
      .GroupBy(static e => _section(e.Key), StringComparer.OrdinalIgnoreCase)
      .OrderBy(static g => g.Key, StringComparer.OrdinalIgnoreCase)
      .Select(static g => new SettingsSection(g.Key, [.. g]))];
  }

  /// <summary>Whether a key's value is shown redacted: bound to a sensitive value, a connection string, or credential-named.</summary>
  internal static bool IsSensitive(string key, KnownConfigurationKeys sensitive) {
    if (sensitive.IsKnown(key) || key.StartsWith("ConnectionStrings:", StringComparison.OrdinalIgnoreCase)) {
      return true;
    }
    var name = key[(key.LastIndexOf(':') + 1)..];
    return _credentialWords.Any(word => name.EndsWith(word, StringComparison.OrdinalIgnoreCase));
  }

  private static SettingEntry _entry(
      string manifestKey,
      string key,
      IConfigurationRoot? configuration,
      List<IConfigurationProvider> providers,
      KnownConfigurationKeys sensitive,
      Dictionary<string, IDerivedSetting> derivations) {
    if (derivations.TryGetValue(key, out var derivation)) {
      return new SettingEntry(manifestKey, key, SettingSource.Derived, null, null, derivation);
    }
    var raw = configuration?[key];
    if (raw is null) {
      return new SettingEntry(manifestKey, key, SettingSource.Default, null, null, null);
    }
    var value = IsSensitive(key, sensitive) ? new SensitiveString(raw).ToString() : raw;
    // A value came from a provider, so one of them has the key.
    var provider = providers.First(p => p.TryGet(key, out _)).ToString();
    return new SettingEntry(manifestKey, key, SettingSource.Configured, value, provider, null);
  }

  private static string _section(string key) {
    var last = key.LastIndexOf(':');
    return last < 0 ? "(root)" : key[..last];
  }
}
