// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using Whizbang.Core.Notifications;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Workers;

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
/// <docs>operations/startup/capabilities-and-duties#the-role-catalog</docs>
/// <tests>tests/Whizbang.Core.Unit.Workers.Tests/Startup/RoleCatalogTests.cs:ADeclaration_RefusesAnEmptyName_AndTheCatalogRefusesNullsAsync</tests>
public sealed record RoleDeclaration {
  /// <summary>Declares a role.</summary>
  /// <param name="name">The role, or with <paramref name="isPrefix"/> the start every role of the family has.</param>
  /// <param name="kind">How it is held.</param>
  /// <param name="isPrefix">Whether <paramref name="name"/> declares a family of roles by their common start.</param>
  /// <exception cref="ArgumentException">The name is null, empty or white space.</exception>
  public RoleDeclaration(string name, RoleKind kind, bool isPrefix = false) {
    ArgumentException.ThrowIfNullOrWhiteSpace(name);
    Name = name;
    Kind = kind;
    IsPrefix = isPrefix;
  }

  /// <summary>The role, or the start every role of a family has.</summary>
  public string Name { get; }

  /// <summary>How it is held.</summary>
  public RoleKind Kind { get; }

  /// <summary>Whether <see cref="Name"/> declares a family of roles by their common start.</summary>
  public bool IsPrefix { get; }
}

/// <summary>
/// Every role an instance may vote for, declared once (#2594). The framework's own roles are declared here; a host
/// declares its own with <see cref="RoleCatalogServiceCollectionExtensions.AddWhizbangRole"/>, which also puts the role
/// in the roster the holder loop and the <c>roles</c> health component read. The role elector refuses a role this
/// catalog does not declare before any statement runs, so an undeclared name never reaches the vote.
/// </summary>
/// <docs>operations/startup/capabilities-and-duties#the-role-catalog</docs>
/// <tests>tests/Whizbang.Core.Unit.Workers.Tests/Startup/RoleCatalogTests.cs</tests>
/// <tests>tests/Whizbang.Core.Component.Tests/Startup/RoleDeclarationGuardTests.cs</tests>
public sealed class RoleCatalog {
  private readonly Dictionary<string, RoleDeclaration> _exact = new(StringComparer.Ordinal);
  private readonly List<RoleDeclaration> _prefixes = [];

  /// <summary>
  /// The framework's own roles: the migrator and the perspective-rebuild family are held for one unit of work; the
  /// maintainer, the commit-order stamper and the partition assigner are held for a tenure by one loop each.
  /// </summary>
  public static IReadOnlyList<RoleDeclaration> FrameworkRoles { get; } = [
    new(StartupDuties.MIGRATOR, RoleKind.Episodic),
    new(StartupDuties.MAINTAINER, RoleKind.Standing),
    new(CommitOrderStamperOptions.ROLE, RoleKind.Standing),
    new(PartitionAssignerOptions.ROLE, RoleKind.Standing),
    new(PerspectiveRebuildDuty.DUTY_PREFIX, RoleKind.Episodic, isPrefix: true),
  ];

  /// <summary>The catalog of the framework's roles alone.</summary>
  public static RoleCatalog Framework { get; } = new([]);

  /// <summary>Creates the catalog of the framework's roles and <paramref name="declarations"/>.</summary>
  /// <param name="declarations">The host's declarations; one declared twice with one kind is one declaration.</param>
  /// <exception cref="ArgumentNullException"><paramref name="declarations"/> is null.</exception>
  /// <exception cref="InvalidOperationException">One name is declared with two kinds, or as a role and as a family.</exception>
  public RoleCatalog(IEnumerable<RoleDeclaration> declarations) {
    ArgumentNullException.ThrowIfNull(declarations);
    var all = new List<RoleDeclaration>();
    foreach (var declaration in FrameworkRoles.Concat(declarations)) {
      var existing = all.Find(d => string.Equals(d.Name, declaration.Name, StringComparison.Ordinal));
      if (existing is null) {
        all.Add(declaration);
        if (declaration.IsPrefix) {
          _prefixes.Add(declaration);
        } else {
          _exact[declaration.Name] = declaration;
        }
      } else if (existing != declaration) {
        throw new InvalidOperationException(
          $"The role '{declaration.Name}' is declared twice, as {_describe(existing)} and as {_describe(declaration)}; one role is held one way.");
      }
    }
    Declarations = all;
  }

  /// <summary>Every declaration, the framework's first, each once.</summary>
  public IReadOnlyList<RoleDeclaration> Declarations { get; }

  /// <summary>The declaration of <paramref name="role"/>: its own, else the longest family it starts with; null when none.</summary>
  /// <param name="role">The role.</param>
  /// <returns>The declaration, or null when the role is undeclared.</returns>
  /// <exception cref="ArgumentNullException"><paramref name="role"/> is null.</exception>
  public RoleDeclaration? Find(string role) {
    ArgumentNullException.ThrowIfNull(role);
    if (_exact.TryGetValue(role, out var exact)) {
      return exact;
    }
    return _prefixes
      .Where(p => role.Length > p.Name.Length && role.StartsWith(p.Name, StringComparison.Ordinal))
      .MaxBy(p => p.Name.Length);
  }

  /// <summary>Whether <paramref name="role"/> is declared, by name or by family.</summary>
  /// <param name="role">The role.</param>
  /// <returns>True when declared.</returns>
  public bool Declares(string role) => Find(role) is not null;

  private static string _describe(RoleDeclaration d) => $"{(d.IsPrefix ? "a family" : "a role")} held {d.Kind}";
}
