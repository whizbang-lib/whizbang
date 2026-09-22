using Whizbang.Core.Lenses;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// Optional interface for perspectives that need control over scope transitions.
/// When a perspective implements this AND processes an <see cref="IScopeEvent"/>,
/// <see cref="ApplyScope"/> decides the final scope value.
/// </summary>
/// <typeparam name="TModel">The read model type this perspective maintains</typeparam>
/// <remarks>
/// <para>
/// Without this interface, an <see cref="IScopeEvent"/> sets the scope directly.
/// With this interface, the perspective can merge, override, or reject proposed scope changes.
/// </para>
/// <para>
/// Execution order when both <see cref="IPerspectiveFor{TModel}"/> and <see cref="IPerspectiveScopeFor{TModel}"/>
/// are implemented: Apply (model) fires first, then ApplyScope (scope).
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/perspectives#scope-handling</docs>
/// <tests>tests/Whizbang.Core.Tests/Scoping/IPerspectiveScopeForTests.cs</tests>
[global::System.Diagnostics.CodeAnalysis.SuppressMessage("Sonar", "S2326:Unused type parameters should be removed", Justification = "The type parameter is the key the source generator and the container resolve on; the interface is a marker by design.")]
public interface IPerspectiveScopeFor<TModel> where TModel : class {
  /// <summary>
  /// Determines the final scope when processing an <see cref="IScopeEvent"/>.
  /// </summary>
  /// <param name="currentScope">The current scope of the perspective row (empty on first insert)</param>
  /// <param name="proposedScope">The scope carried by the <see cref="IScopeEvent"/></param>
  /// <returns>The final scope to persist</returns>
  PerspectiveScope ApplyScope(PerspectiveScope currentScope, PerspectiveScope proposedScope);
}
