namespace Whizbang.Generators.Utilities;

/// <summary>
/// Standard interface names used across all generators.
/// ALL constants use global:: prefix for FullyQualifiedFormat compatibility.
/// </summary>
/// <remarks>
/// <para>
/// These constants MUST be used instead of inline string literals to ensure
/// consistent type matching across all generators.
/// </para>
/// <para>
/// All names include the "global::" prefix because they are designed to match
/// output from <see cref="TypeNameHelper.GetFullyQualifiedName"/>.
/// </para>
/// </remarks>
internal static class StandardInterfaceNames {
  // Core message interfaces
  public const string I_COMMAND = "global::Whizbang.Core.ICommand";
  public const string I_EVENT = "global::Whizbang.Core.IEvent";
  public const string I_MESSAGE = "global::Whizbang.Core.IMessage";

  /// <summary>
  /// Composite event marker — IMessage-not-IEvent. Carries [StreamId] (via CompositeEventBase) so its
  /// fanned-out inner events inherit the composite's stream. Resolved through the object-typed
  /// TryResolveAsGuid path (TRY_RESOLVE_OTHER_DISPATCH), since it is not an IEvent/ICommand.
  /// </summary>
  public const string I_COMPOSITE_EVENT = "global::Whizbang.Core.Messaging.ICompositeEvent";

  // Dispatcher interface — used by MessageRegistryGenerator to filter SendAsync /
  // PublishAsync invocations to genuine dispatcher calls. Without this filter the
  // generator picks up identically-named methods on unrelated types (e.g. Rocks-
  // generated `IDispatcherCreateExpectations.SetupsExpectations.PublishAsync<T>`)
  // and crashes on the symbol-info dereference (CS8785).
  public const string I_DISPATCHER = "global::Whizbang.Core.IDispatcher";

  // Receptor interfaces
  public const string I_RECEPTOR = "global::Whizbang.Core.IReceptor";
  public const string I_SYNC_RECEPTOR = "global::Whizbang.Core.ISyncReceptor";

  /// <summary>Slice 5 opt-in raw JSON receptor — handles unparsed JsonElement payloads.</summary>
  public const string I_RAW_RECEPTOR = "global::Whizbang.Core.Messaging.IRawReceptor";

  // Generic receptor interface original definitions
  public const string I_RECEPTOR_GENERIC_DEFINITION = "global::Whizbang.Core.IReceptor<TMessage>";
  public const string I_RECEPTOR_WITH_RESPONSE_GENERIC_DEFINITION = "global::Whizbang.Core.IReceptor<TMessage, TResponse>";
  public const string I_SYNC_RECEPTOR_GENERIC_DEFINITION = "global::Whizbang.Core.ISyncReceptor<TMessage>";
  public const string I_SYNC_RECEPTOR_WITH_RESPONSE_GENERIC_DEFINITION = "global::Whizbang.Core.ISyncReceptor<TMessage, TResponse>";

  // Perspective interfaces
  public const string I_PERSPECTIVE_BASE = "global::Whizbang.Core.Perspectives.IPerspectiveBase";
  public const string I_PERSPECTIVE_FOR = "global::Whizbang.Core.Perspectives.IPerspectiveFor";
  public const string I_PERSPECTIVE_WITH_ACTIONS_FOR = "global::Whizbang.Core.Perspectives.IPerspectiveWithActionsFor";

  // Generic perspective interface original definitions
  public const string I_PERSPECTIVE_FOR_GENERIC_DEFINITION = "global::Whizbang.Core.Perspectives.IPerspectiveFor<TModel, TEvent>";
  public const string I_PERSPECTIVE_WITH_ACTIONS_FOR_GENERIC_DEFINITION = "global::Whizbang.Core.Perspectives.IPerspectiveWithActionsFor<TModel, TEvent, TAction>";

  // Attributes
  public const string PINNED_ID_ATTRIBUTE = "global::Whizbang.Core.Attributes.PinnedIdAttribute";
  public const string STREAM_ID_ATTRIBUTE = "global::Whizbang.Core.StreamIdAttribute";
  public const string TOPIC_ATTRIBUTE = "global::Whizbang.Core.Attributes.TopicAttribute";
  public const string TOPIC_FILTER_ATTRIBUTE = "global::Whizbang.Core.TopicFilterAttribute";
  public const string WHIZBANG_ID_ATTRIBUTE = "global::Whizbang.Core.WhizbangIdAttribute";
  public const string WHIZBANG_SERIALIZABLE = "global::Whizbang.Core.WhizbangSerializableAttribute";
  public const string GENERATE_STREAM_ID_ATTRIBUTE = "global::Whizbang.Core.GenerateStreamIdAttribute";

  // Other core types
  public const string I_WHIZBANG_ID = "global::Whizbang.Core.IWhizbangId";
  public const string WHIZBANG_ID = "global::Whizbang.Core.WhizbangId";
}
