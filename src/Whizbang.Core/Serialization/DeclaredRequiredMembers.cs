// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Collections.Frozen;

namespace Whizbang.Core.Serialization;

/// <summary>
/// The members a type declares required (<see langword="required"/> or <c>[JsonRequired]</c>) whose generated reader
/// does not refuse their absence, by declared JSON name. The generated message JSON context marks a required member
/// required only on a perspective model and the types only perspective models carry; a type a perspective model
/// shares with an event, a command or another message is read tolerantly, because that one type's metadata also
/// reads history, and history must stay readable after a later change adds <see langword="required"/> (#1874). The
/// generated module initializer registers such a type's required members here, so the startup check of perspective
/// rows still reports a row missing one, at Error.
/// </summary>
/// <remarks>
/// Registration is by <see langword="typeof"/> from generated code, so no reflection is involved. Every context that
/// registers a type derives the same names from the same declaration, so a later registration replaces an earlier one.
/// </remarks>
/// <docs>fundamentals/perspectives/stored-form-migrations#declared-names</docs>
/// <tests>tests/Whizbang.Core.Tests/Serialization/DeclaredRequiredMembersTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Serialization/DeclaredNameReadTests.cs:ATypeARowSharesWithAnEvent_ReadsTolerantly_AndIsDeclaredToTheStartupCheckAsync</tests>
public static class DeclaredRequiredMembers {
  private static readonly ConcurrentDictionary<Type, FrozenSet<string>> _byType = new();

  /// <summary>
  /// Registers the declared JSON names of <paramref name="type"/>'s required members that its reader tolerates,
  /// replacing any registered before. Called from generated module initializers.
  /// </summary>
  /// <param name="type">The type that declares the members.</param>
  /// <param name="jsonNames">The members' declared JSON names.</param>
  /// <tests>tests/Whizbang.Core.Tests/Serialization/DeclaredRequiredMembersTests.cs:ARegisteredMember_IsDeclared_ByItsJsonNameAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Serialization/DeclaredRequiredMembersTests.cs:ALaterRegistration_ReplacesTheEarlierOneAsync</tests>
  public static void Register(Type type, params string[] jsonNames) {
    ArgumentNullException.ThrowIfNull(type);
    ArgumentNullException.ThrowIfNull(jsonNames);
    _byType[type] = jsonNames.ToFrozenSet(StringComparer.Ordinal);
  }

  /// <summary>Whether <paramref name="type"/> declares the member stored under <paramref name="jsonName"/> required.</summary>
  /// <param name="type">The type the member belongs to.</param>
  /// <param name="jsonName">The member's declared JSON name, matched exactly.</param>
  /// <tests>tests/Whizbang.Core.Tests/Serialization/DeclaredRequiredMembersTests.cs:ARegisteredMember_IsDeclared_ByItsJsonNameAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Serialization/DeclaredRequiredMembersTests.cs:ATypeNeverRegistered_DeclaresNothingAsync</tests>
  public static bool IsDeclared(Type type, string jsonName) {
    ArgumentNullException.ThrowIfNull(type);
    ArgumentNullException.ThrowIfNull(jsonName);
    return _byType.TryGetValue(type, out var names) && names.Contains(jsonName);
  }
}
