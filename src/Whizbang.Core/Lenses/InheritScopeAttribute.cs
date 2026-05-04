namespace Whizbang.Core.Lenses;

/// <summary>
/// Declares which <see cref="PerspectiveScope"/> fields a perspective row inherits from
/// the creating message's scope, and on what temporal cadence (only at INSERT, or on every
/// projection apply). Applied to perspective model classes.
/// </summary>
/// <remarks>
/// <para>
/// When the attribute is absent, the framework default is the same as <c>[InheritScope]</c>
/// with no overrides: <c>OnCreate = ScopeFields.Tenant</c>, <c>Always = ScopeFields.None</c>.
/// This is a deliberate change from the legacy "copy every scope field on INSERT" behavior —
/// auto-copying actor identity into a row's resource scope conflates audit and access control.
/// </para>
/// <para>
/// <see cref="Perspectives.IPerspectiveScopeFor{TModel}"/> takes precedence over both this
/// attribute and <see cref="IScopeEvent"/> when present on a perspective.
/// </para>
/// </remarks>
/// <docs>fundamentals/security/scoping#scope-inheritance</docs>
/// <tests>tests/Whizbang.Core.Tests/Scoping/InheritScopeAttributeTests.cs</tests>
/// <example>
/// <code>
/// // Owner-style record: tenant + creator pinned at create, never mutated thereafter.
/// [InheritScope(OnCreate = ScopeFields.Tenant | ScopeFields.User)]
/// public class MyDashboardModel { ... }
///
/// // Last-modified-by semantics: tenant pinned at create, user tracks the latest writer.
/// [InheritScope(OnCreate = ScopeFields.Tenant, Always = ScopeFields.User)]
/// public class CollaborativeDocModel { ... }
///
/// // Pure resource record: only tenant inherits; everything else is event-supplied.
/// [InheritScope]   // defaults: OnCreate = Tenant, Always = None
/// public class WorkflowModel { ... }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class InheritScopeAttribute : Attribute {
  /// <summary>
  /// Fields copied from the creating message's scope on INSERT only. Default: <c>Tenant</c>.
  /// </summary>
  public ScopeFields OnCreate { get; init; } = ScopeFields.Tenant;

  /// <summary>
  /// Fields copied from the message's scope on every Apply (last-writer semantics).
  /// Default: <c>None</c>.
  /// </summary>
  public ScopeFields Always { get; init; } = ScopeFields.None;
}
