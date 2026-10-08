// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Whizbang.Core.Offloads;

/// <summary>
/// Binds <see cref="MessageBodyOffloadOptions"/> from <c>Whizbang:BodyOffload</c>, applied after any
/// code callback so an operator can set every documented knob from appsettings or an environment
/// variable (<c>Whizbang__BodyOffload__PassiveExpiry</c>), whatever provider stores the bodies.
/// </summary>
/// <remarks>
/// Read by hand rather than through the reflection binder, so it stays trimming- and AOT-safe. A key
/// that is absent or does not parse leaves the value it already had. A host that registers no
/// configuration gets an empty one, and keeps the code values.
/// </remarks>
/// <docs>operations/configuration/configuration-reference#whizbangbodyoffload--messagebodyoffloadoptions</docs>
/// <tests>tests/Whizbang.Core.Tests/Offloads/MessageBodyOffloadOptionsBindingTests.cs</tests>
internal sealed class MessageBodyOffloadOptionsPostConfigure(IConfiguration configuration)
  : IPostConfigureOptions<MessageBodyOffloadOptions> {

  /// <summary>The section every body-offload knob binds from.</summary>
  internal const string CONFIGURATION_SECTION = "Whizbang:BodyOffload";

  public void PostConfigure(string? name, MessageBodyOffloadOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    var section = configuration.GetSection(CONFIGURATION_SECTION);
    if (!section.Exists()) {
      return;
    }

    _text(section, nameof(MessageBodyOffloadOptions.ProviderName), v => options.ProviderName = v);
    _text(section, nameof(MessageBodyOffloadOptions.CipherName), v => options.CipherName = v);
    if (long.TryParse(section[nameof(MessageBodyOffloadOptions.SizeThresholdBytes)], NumberStyles.Integer, CultureInfo.InvariantCulture, out var threshold)) {
      options.SizeThresholdBytes = threshold;
    }

    if (bool.TryParse(section[nameof(MessageBodyOffloadOptions.ActiveCleanup)], out var activeCleanup)) {
      options.ActiveCleanup = activeCleanup;
    }

    _timeSpan(section, nameof(MessageBodyOffloadOptions.PassiveExpiry), v => options.PassiveExpiry = v);
    _timeSpan(section, nameof(MessageBodyOffloadOptions.PassiveSweepClaimWindow), v => options.PassiveSweepClaimWindow = v);
    _int(section, nameof(MessageBodyOffloadOptions.PassiveSweepBatchSize), v => options.PassiveSweepBatchSize = v);
    _int(section, nameof(MessageBodyOffloadOptions.PassiveSweepMaxBatchesPerCycle), v => options.PassiveSweepMaxBatchesPerCycle = v);
    _timeSpan(section, nameof(MessageBodyOffloadOptions.DownloadTimeout), v => options.DownloadTimeout = v);
  }

  private static void _text(IConfiguration section, string key, Action<string> apply) {
    var value = section[key];
    if (!string.IsNullOrWhiteSpace(value)) {
      apply(value);
    }
  }

  private static void _int(IConfiguration section, string key, Action<int> apply) {
    if (int.TryParse(section[key], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) {
      apply(value);
    }
  }

  private static void _timeSpan(IConfiguration section, string key, Action<TimeSpan> apply) {
    if (TimeSpan.TryParse(section[key], CultureInfo.InvariantCulture, out var value)) {
      apply(value);
    }
  }
}
