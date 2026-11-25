// Template snippets for the version rule a generated WhizbangId carries (#2516, #2517).
// Each #region holds members of the generated struct; the generator extracts a region, replaces its placeholders and
// indents it into the struct.

using System;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Generators.Templates.Snippets;

/// <summary>The members of a generated WhizbangId that carry its version rule.</summary>
internal readonly partial struct __TYPE_NAME__ {
  private __TYPE_NAME__(TrackedGuid tracked) => _ = tracked;

  #region GRANDFATHERED_HOOK
  /// <summary>
  /// Implement to let the checked entry points (From, Parse, the explicit cast, JSON, the GraphQL scalar) accept
  /// a frozen set of values that predate this id's version rule: set <paramref name="grandfathered"/> to true for
  /// them. Implement it without an accessibility modifier, as declared here: with one, C# requires every id to
  /// implement it. Asked only for a value that breaks the rule, and never for Guid.Empty, which is always refused.
  /// Keep it a set lookup: it runs on every refused value.
  /// </summary>
  static partial void _isGrandfathered(Guid value, ref bool grandfathered);

  /// <summary>Whether the id type grandfathers a value that breaks its version rule. Never the empty Guid.</summary>
  private static bool _grandfathers(Guid value) {
    if (value == Guid.Empty)
      return false;
    var grandfathered = false;
    _isGrandfathered(value, ref grandfathered);
    return grandfathered;
  }

  #endregion

  #region IS_ACCEPTED_UUID_V7
  /// <summary>
  /// Whether <c>From</c> accepts <paramref name="value"/>: UUIDv7, or a value the id grandfathers. Never throws for a
  /// value it refuses, so a caller that only asks (a stream-id report, say) needs no exception.
  /// </summary>
  public static bool IsAccepted(Guid value) => value.Version == 7 || _grandfathers(value);

  #endregion

  #region IS_ACCEPTED_ALLOW_LIST
  /// <summary>
  /// Whether <c>From</c> accepts <paramref name="value"/>: __ACCEPTED__ with the RFC 9562 variant, or a value the id
  /// grandfathers. Never throws for a value it refuses, so a caller that only asks (a stream-id report, say) needs no
  /// exception.
  /// </summary>
  public static bool IsAccepted(Guid value) =>
    (value.Version is __VERSION_PATTERN__ && (value.Variant & 0xC) == 0x8) || _grandfathers(value);

  #endregion

  #region IS_ACCEPTED_ANY_VERSION
  /// <summary>Whether <c>From</c> accepts <paramref name="value"/>: any Guid but the empty one. Never throws.</summary>
  public static bool IsAccepted(Guid value) => value != Guid.Empty;

  #endregion

  #region ALLOW_LIST_FACTORIES
  /// <summary>Creates an instance from a TrackedGuid, preserving tracking metadata. Throws if not __ACCEPTED__.</summary>
  public static __TYPE_NAME__ From(TrackedGuid tracked) {
    _requireAcceptedVersion(tracked.Value, nameof(tracked));
    return new(tracked);
  }

  /// <summary>Creates an instance from a Guid value. Throws if not __ACCEPTED__.</summary>
  public static __TYPE_NAME__ From(Guid value) => new(TrackedGuid.FromExternal(_requireAcceptedVersion(value, nameof(value))));

  /// <summary>Returns the Guid, or throws if its version is not __ACCEPTED__.</summary>
  private static Guid _requireAcceptedVersion(Guid value, string parameterName) {
    // The RFC 9562 variant as well as the version: a hash or random bytes carry a version nibble by chance, and the
    // variant, the top two bits of octet 8 (10xx), is what says the bytes are a UUID at all. A grandfathered value is
    // accepted either way (#2517).
    if (!IsAccepted(value))
      throw new ArgumentException($"__TYPE_NAME__ requires __ACCEPTED__ (RFC 9562 variant) but received version {value.Version}, variant {value.Variant:X}", parameterName);
    return value;
  }
  #endregion

  // Placeholders, for the IDE.
  private const int __VERSION_PATTERN__ = 4;
  private static bool __ACCEPTED__ => true;
}

/// <summary>The module initializer lines that register a generated WhizbangId's version rule.</summary>
internal static class WhizbangIdRuleRegistrationSnippets {
  internal static void Example() {
    #region REGISTER_ACCEPTANCE
    global::Whizbang.Core.WhizbangIdProviderRegistry.RegisterAcceptance<global::__FULLY_QUALIFIED_NAME__>(global::__FULLY_QUALIFIED_NAME__.IsAccepted);
    #endregion

    #region REGISTER_ACCEPTS_ANY_VERSION
    global::Whizbang.Core.WhizbangIdProviderRegistry.RegisterAcceptsAnyVersion<global::__FULLY_QUALIFIED_NAME__>();
    #endregion
  }
}
