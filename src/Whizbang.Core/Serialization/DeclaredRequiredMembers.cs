// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Serialization;

/// <summary>Not yet implemented (RED, #1874).</summary>
public static class DeclaredRequiredMembers {
  /// <summary>Not yet implemented.</summary>
  /// <param name="type">The type.</param>
  /// <param name="jsonNames">The names.</param>
  public static void Register(Type type, params string[] jsonNames) {
    ArgumentNullException.ThrowIfNull(type);
    ArgumentNullException.ThrowIfNull(jsonNames);
    throw new NotSupportedException("RED (#1874)");
  }

  /// <summary>Not yet implemented.</summary>
  /// <param name="type">The type.</param>
  /// <param name="jsonName">The name.</param>
  public static bool IsDeclared(Type type, string jsonName) {
    ArgumentNullException.ThrowIfNull(type);
    ArgumentNullException.ThrowIfNull(jsonName);
    throw new NotSupportedException("RED (#1874)");
  }
}
