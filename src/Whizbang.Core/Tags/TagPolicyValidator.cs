namespace Whizbang.Core.Tags;

/// <summary>
/// Validates tag-policy invariants over the discovered tag registrations at host start:
/// the <c>sys-</c> prefix is reserved for framework tags (for tag attributes AND for
/// RouteNamespace bindings), a message type may match at most ONE coalesce binding, and a
/// message type's route-bound tags may map to at most ONE TransportNamespace key. Pure over
/// its inputs — the hosted seam is <see cref="TagPolicyStartupValidator"/>.
/// </summary>
/// <docs>fundamentals/messages/message-tags#validation</docs>
/// <tests>tests/Whizbang.Core.Tests/Tags/TagPolicyValidatorTests.cs:Validate_UserTagWithSysPrefix_ThrowsAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Tags/TagPolicyValidatorTests.cs:Validate_FrameworkSysAuditOnFrameworkType_DoesNotThrowAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Tags/TagPolicyValidatorTests.cs:Validate_TypeMatchingTwoCoalesceBindings_ThrowsNamingTypeAndBothTagsAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Tags/TagPolicyValidatorTests.cs:Validate_AmbiguityIgnoresDisabledBindingsAsync</tests>
public static class TagPolicyValidator {
  /// <summary>
  /// Validates <paramref name="registrations"/> against the reserved-prefix rule,
  /// <paramref name="coalesceBindings"/> against the one-binding-per-type rule, and — when
  /// supplied — <paramref name="routeNamespaceBindings"/> against the reserved prefix (a
  /// consumer cannot bind sys-* tags the framework does not ship) and the
  /// one-TransportNamespace-per-type rule.
  /// </summary>
  /// <param name="registrations">All discovered tag registrations (typically <see cref="MessageTagRegistry.GetAllTags"/>).</param>
  /// <param name="coalesceBindings">The effective coalesce bindings, keyed by tag.</param>
  /// <param name="routeNamespaceBindings">The effective TransportNamespace routing bindings,
  /// keyed by tag (<see cref="TagOptions.RouteNamespaceBindings"/>); null keeps the pre-phase-8
  /// two-argument behavior.</param>
  /// <exception cref="TagPolicyConfigurationException">A policy invariant is violated.</exception>
  public static void Validate(
      IEnumerable<MessageTagRegistration> registrations,
      IReadOnlyDictionary<string, CoalescePolicyOptions> coalesceBindings,
      IReadOnlyDictionary<string, string>? routeNamespaceBindings = null) {
    ArgumentNullException.ThrowIfNull(registrations);
    ArgumentNullException.ThrowIfNull(coalesceBindings);

    var all = registrations as IReadOnlyList<MessageTagRegistration> ?? [.. registrations];

    _validateReservedPrefix(all);
    _validateCoalesceAmbiguity(all, coalesceBindings);
    if (routeNamespaceBindings is not null) {
      _validateRouteNamespaceReservedPrefix(routeNamespaceBindings);
      _validateRouteNamespaceAmbiguity(all, routeNamespaceBindings);
    }
  }

  private static void _validateReservedPrefix(IReadOnlyList<MessageTagRegistration> all) {
    foreach (var registration in all) {
      if (registration.Tag.StartsWith(SystemTags.RESERVED_PREFIX, StringComparison.Ordinal)
          && !SystemTags.IsFrameworkTag(registration.Tag)) {
        throw new TagPolicyConfigurationException(
          $"Message type '{registration.MessageType.FullName}' declares tag '{registration.Tag}', which mints a new "
          + $"tag under the reserved '{SystemTags.RESERVED_PREFIX}' prefix. That namespace belongs to framework tags "
          + "(see SystemTags) so framework and application vocabularies can never collide — rename the tag (for "
          + "example, drop the prefix), or use an existing SystemTags value to opt into that framework policy.");
      }
    }
  }

  private static void _validateRouteNamespaceReservedPrefix(
      IReadOnlyDictionary<string, string> routeNamespaceBindings) {
    foreach (var binding in routeNamespaceBindings) {
      if (binding.Key.StartsWith(SystemTags.RESERVED_PREFIX, StringComparison.Ordinal)
          && !SystemTags.IsFrameworkTag(binding.Key)) {
        throw new TagPolicyConfigurationException(
          $"RouteNamespace binding declares tag '{binding.Key}', which mints a new tag under the reserved "
          + $"'{SystemTags.RESERVED_PREFIX}' prefix. That namespace belongs to framework tags (see SystemTags) so "
          + "framework and application vocabularies can never collide — rename the tag (for example, drop the "
          + "prefix), or bind an existing SystemTags value to route that framework traffic class.");
      }
    }
  }

  private static void _validateRouteNamespaceAmbiguity(
      IReadOnlyList<MessageTagRegistration> all,
      IReadOnlyDictionary<string, string> routeNamespaceBindings) {
    if (routeNamespaceBindings.Count == 0) {
      return;
    }

    foreach (var group in all.GroupBy(r => r.MessageType)) {
      var boundKeys = group
        .Select(r => routeNamespaceBindings.TryGetValue(r.Tag, out var key) ? key : null)
        .Where(k => k is not null)
        .Distinct(StringComparer.Ordinal)
        .OrderBy(k => k, StringComparer.Ordinal)
        .ToList();

      if (boundKeys.Count > 1) {
        throw new TagPolicyConfigurationException(
          $"Message type '{group.Key.FullName}' carries tags routed to more than one TransportNamespace "
          + $"({string.Join(", ", boundKeys.Select(k => $"'{k}'"))}). A message rides exactly one broker namespace — "
          + "silently picking the first match is how traffic-class drift hides, so this fails instead. Remove one of "
          + "the tags from the type, or rebind (or unbind) one of the RouteNamespace policies.");
      }
    }
  }

  private static void _validateCoalesceAmbiguity(
      IReadOnlyList<MessageTagRegistration> all,
      IReadOnlyDictionary<string, CoalescePolicyOptions> coalesceBindings) {
    // SlideSeconds = 0 disables a group (no stamp, no floor) — a disabled binding has no
    // operational effect, so it cannot make a type ambiguous. This is also the operator's
    // escape hatch: disable one of two colliding bindings without touching message types.
    var enabledTags = new HashSet<string>(StringComparer.Ordinal);
    foreach (var binding in coalesceBindings) {
      if (binding.Value.SlideSeconds > 0) {
        enabledTags.Add(binding.Key);
      }
    }

    if (enabledTags.Count < 2) {
      return;  // ambiguity needs at least two enabled bindings
    }

    foreach (var group in all.GroupBy(r => r.MessageType)) {
      var boundTags = group
        .Select(r => r.Tag)
        .Where(enabledTags.Contains)
        .Distinct(StringComparer.Ordinal)
        .OrderBy(t => t, StringComparer.Ordinal)
        .ToList();

      if (boundTags.Count > 1) {
        throw new TagPolicyConfigurationException(
          $"Message type '{group.Key.FullName}' carries tags bound to more than one coalesce policy "
          + $"({string.Join(", ", boundTags.Select(t => $"'{t}'"))}). A message belongs to at most one coalesce "
          + "group — silently picking the first match is how policy drift hides, so this fails instead. Remove "
          + "one of the tags from the type, or unbind (or disable via SlideSeconds = 0) one of the policies.");
      }
    }
  }
}
