// Template snippets for PerspectiveFanOutDiscoveryGenerator. Embedded, not compiled: each #region is extracted and its
// placeholders replaced.

namespace Whizbang.Generators.Templates.Snippets;

/// <summary>Snippets for the perspective fan-out registry: one fan-out's entry.</summary>
public class PerspectiveFanOutSnippets {

  /// <summary>One fan-out: the perspective, its model and foreign event, the key and its selector, and the invoker that calls Apply.</summary>
  public void FanOutEntryExample() {
    #region FAN_OUT_ENTRY
    new global::Whizbang.Core.Perspectives.PerspectiveFanOutEntry(
      PerspectiveType: typeof(__PERSPECTIVE__),
      ModelType: typeof(__MODEL__),
      EventType: typeof(__EVENT__),
      KeyProperty: __KEY_PROPERTY__,
      KeyColumn: __KEY_COLUMN__,
      KeySelector: (global::System.Linq.Expressions.Expression<global::System.Func<__MODEL__, object?>>)(row => row.__KEY_MEMBER__),
      TableName: __TABLE__,
      CreateExecutor: static factory => factory.Create<__MODEL__>(__TABLE__),
      Invoker: static (perspective, evt) => ((global::Whizbang.Core.Perspectives.IPerspectiveFanOutFor<__MODEL__, __EVENT__>)perspective).Apply((__EVENT__)evt)
    ),
    #endregion
  }
}
