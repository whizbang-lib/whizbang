namespace Whizbang.Core;

/// <summary>
/// Marker interface for events that carry a proposed scope change which should be applied
/// to <em>every</em> perspective registered for the same stream id, not just the perspective
/// that happens to process the specific event type. Inherits from <see cref="IScopeEvent"/>
/// — every <see cref="IStreamScopeEvent"/> is also a scope event, with stronger fan-out
/// semantics.
/// </summary>
/// <remarks>
/// <para>
/// Use <see cref="IScopeEvent"/> when the scope change applies only to the projection that
/// directly processes the event. Use <see cref="IStreamScopeEvent"/> when the change applies
/// to the underlying stream as a whole — so any other perspectives projecting that stream
/// should pick it up too. Common case: an admin "grant access to record X to group Y"
/// command should re-scope every read model that displays X.
/// </para>
/// <para>
/// Same precedence rules as <see cref="IScopeEvent"/>: <see cref="Perspectives.IPerspectiveScopeFor{TModel}"/>
/// on a perspective wins over the event's proposed scope.
/// </para>
/// <para>
/// <strong>Current implementation:</strong> the marker is honored by every perspective that
/// applies an event of this type — so today the fan-out happens organically when each
/// affected perspective lists the event in its <see cref="Perspectives.IPerspectiveFor{TModel, TEvent}"/>
/// signature. A registry-driven fan-out (where the dispatcher automatically routes the event
/// to every perspective for the stream id, regardless of whether they Apply that exact type)
/// is a follow-on slice — see plan G.3 for the full design.
/// </para>
/// </remarks>
/// <docs>fundamentals/security/scoping#stream-scope-events</docs>
/// <tests>tests/Whizbang.Core.Tests/Scoping/IStreamScopeEventTests.cs</tests>
public interface IStreamScopeEvent : IScopeEvent {
}
