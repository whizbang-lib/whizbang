using Whizbang.Core.Lenses;

namespace Whizbang.Core;

/// <summary>
/// Marker interface for events that carry a proposed scope change.
/// When a perspective processes an IScopeEvent, the scope column is updated
/// (unlike normal events where scope is set-once on INSERT and never overwritten).
/// </summary>
/// <remarks>
/// <para>
/// Scope is a security boundary. By default, it is set only on INSERT and preserved
/// across all subsequent UPDATEs. IScopeEvent is the explicit opt-in mechanism for
/// changing scope after initial creation.
/// </para>
/// <para>
/// If the perspective implements <see cref="Perspectives.IPerspectiveScopeFor{TModel}"/>,
/// the perspective's <c>ApplyScope</c> method decides the final scope.
/// Otherwise, <see cref="Scope"/> is used directly.
/// </para>
/// </remarks>
/// <docs>fundamentals/security/scoping#scope-events</docs>
/// <tests>tests/Whizbang.Core.Tests/Scoping/IScopeEventTests.cs</tests>
public interface IScopeEvent : IEvent {
  /// <summary>
  /// The proposed new scope for the perspective row.
  /// </summary>
  PerspectiveScope Scope { get; }
}
