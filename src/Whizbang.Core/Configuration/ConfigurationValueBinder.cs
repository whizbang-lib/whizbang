using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Whizbang.Core.Configuration;

/// <summary>
/// Reflection-free readers for one configuration value each, shared by the per-instance options
/// binders (transport sections, named databases, coalesce bindings). Each applies the value only
/// when the key is present AND parses with the invariant culture, so a missing or mistyped key
/// leaves whatever the code set in place. The binder source generator cannot reach these
/// shapes — the section is chosen per instance at runtime, or the class lives in an assembly that
/// does not run the generator — so they read the keys by hand, the same way the
/// <c>Whizbang:Transports:AzureServiceBus</c> section always has.
/// </summary>
/// <docs>operations/configuration/configuration-reference#per-instance-sections</docs>
/// <tests>tests/Whizbang.Core.Tests/Configuration/ConfigurationValueBinderTests.cs</tests>
public static class ConfigurationValueBinder {
  /// <summary>Applies <paramref name="key"/> as an <see cref="int"/>.</summary>
  public static void BindInt(IConfiguration section, string key, Action<int> apply) {
    if (int.TryParse(_read(section, key, apply), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) {
      apply(value);
    }
  }

  /// <summary>Applies <paramref name="key"/> as a <see cref="ushort"/>; an out-of-range value is skipped.</summary>
  public static void BindUShort(IConfiguration section, string key, Action<ushort> apply) {
    if (ushort.TryParse(_read(section, key, apply), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) {
      apply(value);
    }
  }

  /// <summary>Applies <paramref name="key"/> as a <see cref="double"/>.</summary>
  public static void BindDouble(IConfiguration section, string key, Action<double> apply) {
    if (double.TryParse(_read(section, key, apply), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) {
      apply(value);
    }
  }

  /// <summary>Applies <paramref name="key"/> as a <see cref="bool"/> (<c>true</c> / <c>false</c>).</summary>
  public static void BindBool(IConfiguration section, string key, Action<bool> apply) {
    if (bool.TryParse(_read(section, key, apply), out var value)) {
      apply(value);
    }
  }

  /// <summary>Applies <paramref name="key"/> as a <see cref="TimeSpan"/> (<c>hh:mm:ss</c>).</summary>
  public static void BindTimeSpan(IConfiguration section, string key, Action<TimeSpan> apply) {
    if (TimeSpan.TryParse(_read(section, key, apply), CultureInfo.InvariantCulture, out var value)) {
      apply(value);
    }
  }

  /// <summary>Applies <paramref name="key"/> as a string; a blank value is skipped.</summary>
  public static void BindString(IConfiguration section, string key, Action<string> apply) {
    var value = _read(section, key, apply);
    if (!string.IsNullOrWhiteSpace(value)) {
      apply(value);
    }
  }

  /// <summary>
  /// Applies <paramref name="key"/> as a member of <typeparamref name="TEnum"/>, by name and
  /// ignoring case; a value that names no defined member is skipped.
  /// </summary>
  public static void BindEnum<TEnum>(IConfiguration section, string key, Action<TEnum> apply)
    where TEnum : struct, Enum {
    if (Enum.TryParse<TEnum>(_read(section, key, apply), ignoreCase: true, out var value) && Enum.IsDefined(value)) {
      apply(value);
    }
  }

  private static string? _read(IConfiguration section, string key, Delegate apply) {
    ArgumentNullException.ThrowIfNull(section);
    ArgumentNullException.ThrowIfNull(apply);
    return section[key];
  }
}
