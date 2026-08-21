using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Whizbang.Core.Routing;

/// <summary>
/// AOT-safe configuration binding for <see cref="PoisonMessageOptions"/> (topology arc phase 8.5)
/// from <c>Whizbang:Routing:PoisonMessages</c>. Explicit per-key reads only (house idiom — no
/// <c>IConfiguration.Bind</c>, no reflection), applied AFTER code callbacks so an operator can
/// flip the killswitch or move either bound from appsettings or an environment variable
/// (<c>Whizbang__Routing__PoisonMessages__Enabled</c>) without a redeploy. Absent section = no-op
/// (defaults locked).
/// </summary>
/// <docs>fundamentals/dispatcher/routing#poison-messages</docs>
/// <tests>tests/Whizbang.Core.Tests/Routing/PoisonMessageWiringTests.cs</tests>
internal sealed class PoisonMessageOptionsConfigurationBinder(IConfiguration? configuration)
  : IPostConfigureOptions<PoisonMessageOptions> {

  /// <summary>The poison-detection configuration section.</summary>
  internal const string CONFIGURATION_SECTION = "Whizbang:Routing:PoisonMessages";

  public void PostConfigure(string? name, PoisonMessageOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    var section = configuration?.GetSection(CONFIGURATION_SECTION);
    if (section is null || !section.Exists()) {
      return;
    }

    if (bool.TryParse(section["Enabled"], out var enabled)) {
      options.Enabled = enabled;
    }
    _bindTimeSpan(section, "AgeThreshold", v => options.AgeThreshold = v);
    _bindTimeSpan(section, "AgeThresholdFloor", v => options.AgeThresholdFloor = v);
    _bindTimeSpan(section, "LockRenewalDuration", v => options.LockRenewalDuration = v);
    _bindInt(section, "MaxDeliveryAttempts", v => options.MaxDeliveryAttempts = v);
    _bindInt(section, "MaxDurableObservations", v => options.MaxDurableObservations = v);
  }

  private static void _bindTimeSpan(IConfigurationSection section, string key, Action<TimeSpan> apply) {
    if (TimeSpan.TryParse(section[key], CultureInfo.InvariantCulture, out var value)) {
      apply(value);
    }
  }

  private static void _bindInt(IConfigurationSection section, string key, Action<int> apply) {
    if (int.TryParse(section[key], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) {
      apply(value);
    }
  }
}
