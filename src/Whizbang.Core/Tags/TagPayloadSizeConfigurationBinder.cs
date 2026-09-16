using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Whizbang.Core.Tags;

/// <summary>
/// Binds the tag payload-size thresholds from configuration onto <see cref="TagOptions"/>.
/// </summary>
/// <remarks>
/// <para>
/// Reads, under <c>Whizbang:Tags</c>: <c>PayloadSizeWarningThresholdBytes</c> and
/// <c>PayloadSizeErrorThresholdBytes</c> for every tag, and per-tag overrides under
/// <c>PayloadSizeWarningThresholdBytesByTag:{tag}</c> and <c>PayloadSizeErrorThresholdBytesByTag:{tag}</c>.
/// An empty value clears the threshold, which disables it, the same as assigning <see langword="null"/>
/// in code. A value that is not a whole number is refused by its full key, because a threshold that
/// silently stayed at its default would keep producing the warnings the operator set out to stop.
/// </para>
/// <para>
/// Read explicitly rather than through an options binder, so it works without reflection under
/// ahead-of-time compilation, and applied where the processor is built and where the policies are
/// validated at startup, whichever runs first; both applications are idempotent.
/// </para>
/// </remarks>
/// <docs>fundamentals/messages/message-tags</docs>
/// <tests>tests/Whizbang.Core.Tests/Tags/TagPayloadSizeConfigurationBinderTests.cs</tests>
public static class TagPayloadSizeConfigurationBinder {
  /// <summary>The section every key lives under.</summary>
  internal const string CONFIGURATION_SECTION = "Whizbang:Tags";
  /// <summary>The global warning threshold key, relative to the section.</summary>
  internal const string WARNING_KEY = "PayloadSizeWarningThresholdBytes";
  /// <summary>The global error threshold key, relative to the section.</summary>
  internal const string ERROR_KEY = "PayloadSizeErrorThresholdBytes";
  /// <summary>The per-tag warning thresholds, a child per tag, relative to the section.</summary>
  internal const string WARNING_BY_TAG_KEY = "PayloadSizeWarningThresholdBytesByTag";
  /// <summary>The per-tag error thresholds, a child per tag, relative to the section.</summary>
  internal const string ERROR_BY_TAG_KEY = "PayloadSizeErrorThresholdBytesByTag";

  /// <summary>Applies every threshold the configuration carries; leaves the rest as they are.</summary>
  /// <param name="tagOptions">The options to bind onto.</param>
  /// <param name="configuration">The configuration, or <see langword="null"/> when the host has none.</param>
  /// <exception cref="InvalidOperationException">A value is present but is not a whole number of bytes.</exception>
  public static void Apply(TagOptions tagOptions, IConfiguration? configuration) {
    ArgumentNullException.ThrowIfNull(tagOptions);
    var section = configuration?.GetSection(CONFIGURATION_SECTION);
    if (section is null || !section.Exists()) {
      return;
    }

    if (_tryRead(section, WARNING_KEY, out var warning)) {
      tagOptions.PayloadSizeWarningThresholdBytes = warning;
    }
    if (_tryRead(section, ERROR_KEY, out var error)) {
      tagOptions.PayloadSizeErrorThresholdBytes = error;
    }
    foreach (var child in section.GetSection(WARNING_BY_TAG_KEY).GetChildren()) {
      if (_tryRead(child, out var perTag)) {
        tagOptions.SetPayloadSizeWarningThreshold(child.Key, perTag);
      }
    }
    foreach (var child in section.GetSection(ERROR_BY_TAG_KEY).GetChildren()) {
      if (_tryRead(child, out var perTag)) {
        tagOptions.SetPayloadSizeErrorThreshold(child.Key, perTag);
      }
    }
  }

  private static bool _tryRead(IConfigurationSection parent, string key, out int? bytes) =>
    _tryRead(parent.GetSection(key), out bytes);

  /// <summary>Absent: not configured. Empty: configured as disabled. Otherwise a whole number of bytes.</summary>
  private static bool _tryRead(IConfigurationSection section, out int? bytes) {
    bytes = null;
    var value = section.Value;
    if (value is null) {
      return false;
    }
    if (string.IsNullOrWhiteSpace(value)) {
      return true;
    }
    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed < 0) {
      throw new InvalidOperationException(
        $"Configuration value '{section.Path}' must be a whole number of bytes, or empty to disable the threshold; found '{value}'.");
    }
    bytes = parsed;
    return true;
  }
}
