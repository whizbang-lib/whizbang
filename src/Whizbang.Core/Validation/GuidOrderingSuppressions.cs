// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Validation;

/// <summary>
/// The message types that carry <see cref="SuppressGuidOrderingValidationAttribute"/>, as the generated module
/// initializer of each assembly records them (#2528). <see cref="GuidOrderingValidator"/> asks it, by type or by the
/// stored type name the outbox and inbox writes carry, whether a non-v7 event or message id is a deliberate opt-out.
/// No attribute is read by reflection.
/// </summary>
/// <remarks>
/// <para>
/// Types are registered by their stored name (<c>Namespace.Type, Assembly</c>, <see cref="TypeNameFormatter.Format"/>),
/// so a type generated code cannot name (a private nested type) is honored too. A generic type registers its definition
/// (<c>Namespace.Wrapper`1, Assembly</c>), and a lookup of any construction strips the type arguments to match it.
/// </para>
/// <para>
/// Only the non-v7 path reads this: a UUIDv7 passes the check before it is consulted. Answers by type are cached, and
/// the cache is cleared whenever a registration arrives, because an assembly loaded later registers its types later.
/// </para>
/// </remarks>
/// <docs>fundamentals/identity/whizbang-ids#time-ordering-checks</docs>
/// <tests>tests/Whizbang.Core.Tests/Validation/GuidOrderingSuppressionTests.cs</tests>
public static class GuidOrderingSuppressions {
  private static readonly ConcurrentDictionary<string, byte> _names = new(StringComparer.Ordinal);
  private static readonly ConcurrentDictionary<Type, bool> _byType = new();

  /// <summary>
  /// Records that the type with this stored name carries <see cref="SuppressGuidOrderingValidationAttribute"/>.
  /// Called by the generated module initializer; consumers do not call it.
  /// </summary>
  /// <param name="typeName">The stored type name; a versioned assembly-qualified name is normalized first.</param>
  /// <exception cref="ArgumentException">The name is null, empty or white space.</exception>
  /// <tests>tests/Whizbang.Core.Tests/Validation/GuidOrderingSuppressionTests.cs:Register_AVersionedName_IsStoredNormalizedAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Validation/GuidOrderingSuppressionTests.cs:Register_ABlankName_ThrowsAsync</tests>
  public static void Register(string typeName) {
    ArgumentException.ThrowIfNullOrWhiteSpace(typeName);
    _names.TryAdd(EventTypeMatchingHelper.NormalizeTypeName(typeName), 0);
    _byType.Clear();
  }

  /// <summary>Whether <paramref name="messageType"/> carries <see cref="SuppressGuidOrderingValidationAttribute"/>.</summary>
  /// <param name="messageType">The message type, or null.</param>
  /// <returns>True only for a registered type, or a construction of a registered generic type.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Validation/GuidOrderingSuppressionTests.cs:AnAttributedType_IsRegisteredByTheGeneratedModuleInitializerAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Validation/GuidOrderingSuppressionTests.cs:IsSuppressed_ATypeAnsweredBeforeItsRegistration_IsAnsweredAgainAfterItAsync</tests>
  public static bool IsSuppressed(Type? messageType) =>
    messageType is not null && _byType.GetOrAdd(messageType, static type => IsSuppressed(TypeNameFormatter.FormatOrDisplay(type)));

  /// <summary>Whether the type with this stored name carries <see cref="SuppressGuidOrderingValidationAttribute"/>.</summary>
  /// <param name="typeName">The stored or assembly-qualified type name, or null.</param>
  /// <returns>True only for a registered name, or a construction of a registered generic type.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Validation/GuidOrderingSuppressionTests.cs:IsSuppressed_ANameNotRegistered_IsFalseAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Validation/GuidOrderingSuppressionTests.cs:IsSuppressed_AClosedGenericName_MatchesItsRegisteredDefinitionAsync</tests>
  public static bool IsSuppressed(string? typeName) {
    if (string.IsNullOrEmpty(typeName)) {
      return false;
    }
    var normalized = EventTypeMatchingHelper.NormalizeTypeName(typeName);
    return _names.ContainsKey(normalized) || (_genericDefinitionName(normalized) is { } definition && _names.ContainsKey(definition));
  }

  // "Ns.Wrapper`1[[Arg, ArgAssembly]], Assembly" -> "Ns.Wrapper`1, Assembly"; null for a name with no type arguments.
  private static string? _genericDefinitionName(string name) {
    var tick = name.IndexOf('`', StringComparison.Ordinal);
    var open = tick < 0 ? -1 : name.IndexOf('[', tick);
    if (open < 0) {
      return null;
    }
    var depth = 0;
    for (var i = open; i < name.Length; i++) {
      if (name[i] == '[') {
        depth++;
      } else if (name[i] == ']' && --depth == 0) {
        return string.Concat(name.AsSpan(0, open), name.AsSpan(i + 1));
      }
    }
    return null;
  }
}
