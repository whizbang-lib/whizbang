using System;
using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Whizbang.Core.Routing;

/// <summary>
/// AOT-safe configuration binding for <see cref="ControlClassOptions"/> (topology arc phase 9)
/// from <c>Whizbang:Routing:ControlClass</c>. Explicit per-key reads only (house idiom — no
/// <c>IConfiguration.Bind</c>, no reflection), applied AFTER code callbacks so an operator can
/// flip the killswitch, retune the TTL, or roll the sessionless / non-durable migration steps
/// forward and back from appsettings or an environment variable
/// (<c>Whizbang__Routing__ControlClass__Enabled</c>) without a redeploy. Absent section = no-op.
/// </summary>
/// <docs>fundamentals/dispatcher/routing#control-class</docs>
/// <tests>tests/Whizbang.Core.Tests/Routing/ControlClassOptionsTests.cs:Options_BindFromConfigurationAsync</tests>
internal sealed class ControlClassOptionsConfigurationBinder(IConfiguration? configuration)
  : IPostConfigureOptions<ControlClassOptions> {

  /// <summary>The control-class configuration section.</summary>
  internal const string CONFIGURATION_SECTION = "Whizbang:Routing:ControlClass";

  public void PostConfigure(string? name, ControlClassOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    var section = configuration?.GetSection(CONFIGURATION_SECTION);
    if (section is null || !section.Exists()) {
      return;
    }

    if (bool.TryParse(section["Enabled"], out var enabled)) {
      options.Enabled = enabled;
    }
    if (bool.TryParse(section["SessionlessSubscriptions"], out var sessionless)) {
      options.SessionlessSubscriptions = sessionless;
    }
    if (bool.TryParse(section["NonDurableReceive"], out var nonDurable)) {
      options.NonDurableReceive = nonDurable;
    }
    if (int.TryParse(section["CadenceMultiplier"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var multiplier)) {
      options.CadenceMultiplier = multiplier;
    }
    if (TimeSpan.TryParse(section["TimeToLiveFloor"], CultureInfo.InvariantCulture, out var floor)) {
      options.TimeToLiveFloor = floor;
    }
    if (TimeSpan.TryParse(section["TimeToLive"], CultureInfo.InvariantCulture, out var timeToLive)) {
      options.TimeToLive = timeToLive;
    }
  }
}
