using Microsoft.Extensions.Configuration;

namespace Whizbang.Core.Tags;

/// <summary>
/// AOT-safe configuration binder for <see cref="TagOptions.RouteNamespace"/> bindings: reads
/// the <c>Whizbang:Tags:RouteNamespace</c> section manually (no reflection-based options
/// binder) — each child key is a TAG, each value the TransportNamespace KEY — and applies the
/// bindings OVER the code callback (last-wins, the transport-options post-configure idiom), so
/// an operator can re-class traffic from configuration — appsettings.json or environment
/// variables (<c>Whizbang__Tags__RouteNamespace__bulk-import=bulk</c>) — without a redeploy.
/// Idempotent: both consumption seams (the <see cref="TransportNamespaceResolver"/> factory
/// and <see cref="TagPolicyStartupValidator"/>) apply it, whichever runs first.
/// </summary>
/// <docs>fundamentals/messages/message-tags#transport-namespace-routing</docs>
/// <tests>tests/Whizbang.Core.Tests/Tags/TransportNamespaceRoutingRegistrationTests.cs:Resolver_ConfigurationBinding_AddsRouteBindingAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Tags/TransportNamespaceRoutingRegistrationTests.cs:Resolver_ConfigurationOverridesCodeBindingAsync</tests>
internal static class TagRouteNamespaceConfigurationBinder {
  /// <summary>Configuration section the routing bindings bind from.</summary>
  internal const string CONFIGURATION_SECTION = "Whizbang:Tags:RouteNamespace";

  /// <summary>
  /// Applies every configured routing binding onto <paramref name="tagOptions"/>. No-ops when
  /// <paramref name="configuration"/> is null or the section is absent.
  /// </summary>
  /// <param name="tagOptions">The tag options to bind onto.</param>
  /// <param name="configuration">The host configuration (may be null).</param>
  internal static void Apply(TagOptions tagOptions, IConfiguration? configuration) {
    ArgumentNullException.ThrowIfNull(tagOptions);

    var section = configuration?.GetSection(CONFIGURATION_SECTION);
    if (section is null || !section.Exists()) {
      return;
    }

    foreach (var child in section.GetChildren()) {
      if (!string.IsNullOrWhiteSpace(child.Key) && !string.IsNullOrWhiteSpace(child.Value)) {
        tagOptions.UseRouteNamespaceBinding(child.Key, child.Value);
      }
    }
  }
}
