// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;

namespace Whizbang.Core.Startup;

/// <summary>How a role is held (#2594).</summary>
/// <docs>operations/startup/capabilities-and-duties#the-role-catalog</docs>
public enum RoleKind {
  /// <summary>Held by one loop for a tenure, and reported unassigned while nobody holds it.</summary>
  Standing,

  /// <summary>Held for one unit of work and released, so a vacancy between units is its normal state.</summary>
  Episodic,
}

/// <summary>One declared role (#2594).</summary>
/// <param name="Name">The role, or with <paramref name="IsPrefix"/> the start every role of the family has.</param>
/// <param name="Kind">How it is held.</param>
/// <param name="IsPrefix">Whether <paramref name="Name"/> declares a family of roles by their common start.</param>
/// <docs>operations/startup/capabilities-and-duties#the-role-catalog</docs>
public sealed record RoleDeclaration(string Name, RoleKind Kind, bool IsPrefix = false);

/// <summary>RED stub (#2594).</summary>
/// <docs>operations/startup/capabilities-and-duties#the-role-catalog</docs>
public sealed class RoleCatalog {
  /// <summary>RED stub.</summary>
  public RoleCatalog(IEnumerable<RoleDeclaration> declarations) {
    ArgumentNullException.ThrowIfNull(declarations);
  }

  /// <summary>RED stub.</summary>
  public static IReadOnlyList<RoleDeclaration> FrameworkRoles { get; } = [];

  /// <summary>RED stub.</summary>
  public IReadOnlyList<RoleDeclaration> Declarations { get; } = [];

  /// <summary>RED stub.</summary>
  public RoleDeclaration? Find(string role) => Declarations.FirstOrDefault(d => d.Name == role && d.Kind == RoleKind.Standing && false);

  /// <summary>RED stub.</summary>
  public bool Declares(string role) => role is not null || Declarations.Count >= 0;
}
