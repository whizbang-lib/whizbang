using Microsoft.Extensions.Configuration;

namespace Whizbang.Core.Routing;

/// <summary>
/// AOT-safe configuration binding for the routing flip set (topology arc phase 6):
/// <c>Whizbang:Routing:CommandNamespacesToInbox</c> is a string array of command contract
/// namespaces to flip to their per-namespace inbox; the <c>"*"</c> entry flips ALL
/// namespaces. Explicit per-key reads only (house idiom — no <c>IConfiguration.Bind</c>, no
/// reflection), applied ON TOP of the code-callback flips, so operators can flip or roll
/// back a namespace from configuration without a redeploy. Absent section = no-op (defaults
/// locked).
/// </summary>
/// <docs>fundamentals/dispatcher/routing#namespace-outbox</docs>
/// <tests>tests/Whizbang.Core.Tests/Routing/NamespaceOutboxStrategyTests.cs:WithRouting_ConfigurationFlipSet_AppliedOnOptionsResolutionAsync</tests>
internal static class RoutingOptionsConfigurationBinder {
  /// <summary>The routing configuration section.</summary>
  internal const string CONFIGURATION_SECTION = "Whizbang:Routing";

  /// <summary>The flip-set key under the routing section (a string array).</summary>
  internal const string COMMAND_NAMESPACES_TO_INBOX_KEY = "CommandNamespacesToInbox";

  /// <summary>The array entry that flips ALL command namespaces.</summary>
  internal const string ROUTE_ALL_WILDCARD = "*";

  /// <summary>
  /// Applies the configuration flip set to <paramref name="options"/>. Idempotent by
  /// construction (the flip set is a set-union) and a no-op when configuration or the
  /// section is absent.
  /// </summary>
  /// <param name="configuration">The host configuration; null when none is registered.</param>
  /// <param name="options">The routing options to apply the flip set to.</param>
  internal static void Apply(IConfiguration? configuration, RoutingOptions options) {
    ArgumentNullException.ThrowIfNull(options);

    var section = configuration?.GetSection(CONFIGURATION_SECTION);
    if (section is null || !section.Exists()) {
      return;
    }

    var flipSection = section.GetSection(COMMAND_NAMESPACES_TO_INBOX_KEY);
    if (!flipSection.Exists()) {
      return;
    }

    // JSON arrays surface as children keyed "0", "1", … with the entry in child.Value —
    // the same GetChildren() loop TracingOptionsPostConfigure uses (house idiom).
    foreach (var child in flipSection.GetChildren()) {
      if (string.IsNullOrWhiteSpace(child.Value)) {
        continue;
      }

      if (child.Value == ROUTE_ALL_WILDCARD) {
        options.RouteAllCommandNamespacesToInbox();
      } else {
        options.RouteCommandNamespaceToInbox(child.Value);
      }
    }
  }
}
