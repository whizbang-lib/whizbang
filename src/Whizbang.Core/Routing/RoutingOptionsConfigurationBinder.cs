using Microsoft.Extensions.Configuration;

namespace Whizbang.Core.Routing;

/// <summary>
/// AOT-safe configuration binding for the routing flip set and the shared-inbox retirement
/// switch — the two knobs that move a host between the DEFAULT per-namespace command topology
/// and the legacy/mid-migration ones, without a redeploy:
/// <list type="bullet">
///   <item><c>Whizbang:Routing:RouteAllCommandNamespacesToInbox</c> (bool) — the whole
///   publisher flip. <c>false</c> rolls it fully back; this is the key that rolls back the
///   DEFAULT.</item>
///   <item><c>Whizbang:Routing:CommandNamespacesToInbox</c> (string array) — the
///   namespace-at-a-time flip set (the <c>"*"</c> entry flips ALL). Naming namespaces is the
///   same statement in configuration as in code, so the presence of this list steps the
///   all-namespaces DEFAULT aside.</item>
///   <item><c>Whizbang:Routing:RetireSharedInbox</c> (bool) — retirement of the legacy
///   catch-all. <c>false</c> keeps the transitional shared subscription (and the entity's
///   provisioning) while publishers stay flipped: the dual-delivery rollback rung.</item>
/// </list>
/// Explicit per-key reads only (house idiom — no <c>IConfiguration.Bind</c>, no reflection),
/// applied ON TOP of the code-callback values. Absent section = no-op (defaults locked).
/// </summary>
/// <docs>fundamentals/dispatcher/routing#namespace-outbox</docs>
/// <tests>tests/Whizbang.Core.Tests/Routing/NamespaceOutboxStrategyTests.cs:WithRouting_ConfigurationFlipSet_AppliedOnOptionsResolutionAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Routing/SharedInboxRetirementTests.cs:WithRouting_ConfigurationRetireSharedInbox_WithWildcardFlip_BindsAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Routing/DefaultNamespaceTopologyTests.cs:Configuration_RouteAllFalse_RollsBackTheDefaultFlipAsync</tests>
internal static class RoutingOptionsConfigurationBinder {
  /// <summary>The routing configuration section.</summary>
  internal const string CONFIGURATION_SECTION = "Whizbang:Routing";

  /// <summary>The flip-set key under the routing section (a string array).</summary>
  internal const string COMMAND_NAMESPACES_TO_INBOX_KEY = "CommandNamespacesToInbox";

  /// <summary>The all-namespaces flip key under the routing section (a boolean).</summary>
  internal const string ROUTE_ALL_COMMAND_NAMESPACES_KEY = "RouteAllCommandNamespacesToInbox";

  /// <summary>The shared-inbox retirement key under the routing section (a boolean).</summary>
  internal const string RETIRE_SHARED_INBOX_KEY = "RetireSharedInbox";

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

    // The whole-flip switch. Both values are meaningful now that the flip is the DEFAULT:
    // "true" asserts it, "false" is the full publisher rollback. Absent or unparseable leaves
    // whatever the code callback (or the default rule) decided.
    if (bool.TryParse(section[ROUTE_ALL_COMMAND_NAMESPACES_KEY], out var routeAll)) {
      _ = routeAll
        ? options.RouteAllCommandNamespacesToInbox()
        : options.RouteNoCommandNamespacesToInbox();
    }

    // The retirement switch. "true" retires; "false" KEEPS the transitional shared subscription
    // (and therefore the entity's provisioning) — the rollback rung, meaningful now that
    // retirement is the default. Validity (full flip required) is enforced AFTER binding, by
    // the WithRouting options factory.
    if (bool.TryParse(section[RETIRE_SHARED_INBOX_KEY], out var retire)) {
      _ = retire ? options.RetireSharedInbox() : options.KeepSharedInbox();
    }

    var flipSection = section.GetSection(COMMAND_NAMESPACES_TO_INBOX_KEY);
    if (!flipSection.Exists()) {
      return;
    }

    // JSON arrays surface as children keyed "0", "1", … with the entry in child.Value —
    // the same GetChildren() loop TracingOptionsPostConfigure uses (house idiom).
    // RouteCommandNamespaceToInbox steps the all-namespaces DEFAULT aside for us: naming a
    // namespace is the migrate-one-at-a-time statement wherever it is written.
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
