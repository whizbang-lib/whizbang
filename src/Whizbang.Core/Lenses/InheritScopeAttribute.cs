// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Lenses;

/// <summary>
/// Declares which <see cref="PerspectiveScope"/> fields a perspective row inherits from the messages
/// applied to it: at creation (<see cref="OnCreate"/>), and on every apply (<see cref="Always"/>).
/// Applied to perspective model classes.
/// </summary>
/// <remarks>
/// <para>
/// Creation records ownership. When the attribute is absent, and when it is present without
/// <see cref="OnCreate"/>, a new row copies every scope field from the message that created it, including
/// the user who sent it: who created a row matters for audit and for security. Whether a read cares is the
/// lens's decision, through how it filters (by tenant, by user, by principal), not the row's.
/// </para>
/// <para>
/// After creation the row's scope stays as it was set unless <see cref="Always"/> names fields that follow
/// the latest writer, or an <see cref="IScopeEvent"/> replaces the scope.
/// </para>
/// <para>
/// Put the attribute on the perspective's <b>model</b> type: that is where the perspective runner reads it.
/// On a perspective class over another model it has no effect, and the analyzer reports WHIZ170.
/// </para>
/// <para>
/// <see cref="Perspectives.IPerspectiveScopeFor{TModel}"/> takes precedence over both this
/// attribute and <see cref="IScopeEvent"/> when present on a perspective.
/// </para>
/// </remarks>
/// <docs>fundamentals/security/scoping#scope-inheritance</docs>
/// <tests>tests/Whizbang.Core.Tests/Scoping/InheritScopeAttributeTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Scoping/ScopeInheritanceTests.cs</tests>
/// <example>
/// <code>
/// // No attribute, or [InheritScope]: every field at creation, set once.
/// public class WorkflowModel { ... }
///
/// // Last-modified-by: everything at creation, and the user follows the latest writer.
/// [InheritScope(Always = ScopeFields.User)]
/// public class CollaborativeDocModel { ... }
///
/// // A shared resource that records only its tenant, never who created it.
/// [InheritScope(OnCreate = ScopeFields.Tenant)]
/// public class CatalogItemModel { ... }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class InheritScopeAttribute : Attribute {
  /// <summary>
  /// Fields copied from the creating message's scope when the row is created. Default: every field
  /// (<see cref="ScopeFields.All"/>), so the row records who created it.
  /// </summary>
  public ScopeFields OnCreate { get; init; } = ScopeFields.All;

  /// <summary>
  /// Fields copied from each applied message's scope on every apply, so they follow the latest writer
  /// (last-writer semantics). They are also set at creation. A message that does not carry a field leaves
  /// the row's value in place. Default: <see cref="ScopeFields.None"/>, so scope is set once, at creation.
  /// </summary>
  public ScopeFields Always { get; init; } = ScopeFields.None;
}
