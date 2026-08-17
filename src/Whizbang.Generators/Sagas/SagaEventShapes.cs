namespace Whizbang.Generators.Sagas;

/// <summary>
/// One property on a generator-emitted saga event class, described in the
/// exact vocabulary <see cref="PropertyInfo"/> expects.
/// </summary>
/// <param name="Name">Property name as emitted by <c>SagaGenerator</c>.</param>
/// <param name="Type">Fully qualified type as Roslyn's fully-qualified-with-nullability display
/// format would render it (special types stay as keywords: <c>string</c>, <c>int</c>).</param>
/// <param name="IsValueType">True for structs, enums and primitives — drives the <c>typeof()</c>
/// expression the JSON context emits for nullable properties.</param>
internal sealed record SagaEventPropertyShape(string Name, string Type, bool IsValueType);

/// <summary>
/// One generator-emitted saga event class: its nested class name and the properties
/// <c>SagaGenerator</c> declares on it (excluding those inherited from the event base).
/// </summary>
/// <param name="ClassName">Nested class name, e.g. <c>InitiatedEvent</c>.</param>
/// <param name="MarkerInterface">Metadata name of the <c>Whizbang.Sagas.Contracts</c> interface the
/// emitted class implements. It is a polymorphic base like any consumer interface, so the synthesized
/// inheritance has to name it for the emitted context to match a hand-written equivalent.</param>
/// <param name="Properties">Properties declared directly on the emitted class.</param>
/// <param name="IsHookEvent">True for the two hook events, which <c>[Saga(IncludeHooks = false)]</c> omits.</param>
internal sealed record SagaEventShape(string ClassName, string MarkerInterface, SagaEventPropertyShape[] Properties, bool IsHookEvent);

/// <summary>
/// The compile-time shape of every event class <c>Whizbang.Sagas.Generators.SagaGenerator</c>
/// emits into a <c>[Saga]</c>-marked partial class.
///
/// <para><b>Why this exists.</b> Source generators do not observe each other's output: the saga
/// generator emits nine nested event classes into the consumer's assembly, and
/// <see cref="MessageJsonContextGenerator"/> — running over the same, pre-generation compilation —
/// cannot see them. Without this descriptor the consumer's <c>MessageJsonContext</c> carries no
/// <c>JsonTypeInfo</c> for its own saga events, and the first <c>InitiateSagaAsync</c> fails with
/// <c>NotSupportedException: JsonTypeInfo metadata for type '…+InitiatedEvent' was not provided</c>.
/// Declaring the shapes here lets the JSON generator synthesize metadata for types that do not
/// exist yet, from the <c>[Saga]</c> attribute alone.</para>
///
/// <para><b>Keeping it honest.</b> Nothing in the compiler couples this table to
/// <c>SagaGenerator</c>'s <c>_emit*</c> methods, so a drift guard does the coupling at test time:
/// <c>SagaEventSerializationTests.SynthesizedMetadata_CoversExactlyTheEmittedPropertiesAsync</c>
/// (in Whizbang.Sagas.Tests, which loads BOTH generators) reflects over the classes that were
/// actually emitted and fails if this table describes a different property set. Add a property to a
/// saga event and that test — not a consumer's production wire — is what breaks.</para>
/// </summary>
/// <docs>fundamentals/sagas/saga-events</docs>
internal static class SagaEventShapes {
  private const string STRING = "string";
  private const string NULLABLE_STRING = "string?";
  private const string INT = "int";
  private const string GUID = "global::System.Guid";
  private const string STRING_LIST = "global::System.Collections.Generic.IReadOnlyList<string>";
  private const string NULLABLE_STRING_LIST = "global::System.Collections.Generic.IReadOnlyList<string>?";
  private const string SAGA_STATUS = "global::Whizbang.Sagas.SagaStatus";
  private const string SAGA_ITEM_STATE = "global::Whizbang.Sagas.SagaItemState";

  /// <summary>Metadata name of the non-generic saga attribute.</summary>
  public const string SAGA_ATTRIBUTE = "Whizbang.Sagas.SagaAttribute";

  /// <summary>Metadata name of the generic <c>[Saga&lt;TEventBase&gt;]</c> attribute.</summary>
  public const string SAGA_ATTRIBUTE_GENERIC = "Whizbang.Sagas.SagaAttribute`1";

  /// <summary>Default event base used when the saga is declared without an explicit <c>TEventBase</c>.</summary>
  public const string DEFAULT_EVENT_BASE = "Whizbang.Sagas.SagaEventBase";

  /// <summary>Named argument on <c>[Saga]</c> that suppresses the two hook events.</summary>
  public const string INCLUDE_HOOKS_ARGUMENT = "IncludeHooks";

  private static SagaEventPropertyShape _sagaName() => new("SagaName", STRING, IsValueType: false);
  private static SagaEventPropertyShape _entityId() => new("EntityId", GUID, IsValueType: true);
  private static SagaEventPropertyShape _sagaId() => new("SagaId", GUID, IsValueType: true);
  private static SagaEventPropertyShape _itemIdentifier() => new("ItemIdentifier", STRING, IsValueType: false);
  private static SagaEventPropertyShape _displayName() => new("DisplayName", NULLABLE_STRING, IsValueType: false);
  private static SagaEventPropertyShape _hookName() => new("HookName", STRING, IsValueType: false);
  private static SagaEventPropertyShape _totalItems() => new("TotalItems", INT, IsValueType: true);

  /// <summary>
  /// Every event class the generator emits, in emission order. Property lists mirror the
  /// <c>_emit*</c> methods of <c>SagaGenerator</c> one-for-one.
  /// </summary>
  public static readonly SagaEventShape[] All = [
    new("InitiatedEvent", "Whizbang.Sagas.ISagaInitiatedEvent", [
      _sagaName(),
      _entityId(),
      new("ItemIdentifiers", STRING_LIST, IsValueType: false),
      _totalItems(),
      new("HookNames", NULLABLE_STRING_LIST, IsValueType: false),
    ], IsHookEvent: false),

    new("ItemsDispatchedEvent", "Whizbang.Sagas.ISagaItemsDispatchedEvent", [
      _sagaName(),
      _entityId(),
      _totalItems(),
      new("SuccessfullyDispatched", INT, IsValueType: true),
      new("FailedToDispatch", INT, IsValueType: true),
    ], IsHookEvent: false),

    new("ItemStartedEvent", "Whizbang.Sagas.ISagaItemStartedEvent", [
      _sagaName(),
      _entityId(),
      _sagaId(),
      _itemIdentifier(),
      _displayName(),
    ], IsHookEvent: false),

    new("ItemCompletedEvent", "Whizbang.Sagas.ISagaItemCompletedEvent", [
      _sagaName(),
      _entityId(),
      _sagaId(),
      _itemIdentifier(),
      _displayName(),
    ], IsHookEvent: false),

    new("ItemFailedEvent", "Whizbang.Sagas.ISagaItemFailedEvent", [
      _sagaName(),
      _entityId(),
      _sagaId(),
      _itemIdentifier(),
      _displayName(),
      new("ErrorMessage", STRING, IsValueType: false),
      new("ErrorDetails", NULLABLE_STRING, IsValueType: false),
    ], IsHookEvent: false),

    new("CompletedEvent", "Whizbang.Sagas.ISagaCompletedEvent", [
      _sagaName(),
      _entityId(),
      new("FinalStatus", SAGA_STATUS, IsValueType: true),
      new("CompletedByItemIdentifier", NULLABLE_STRING, IsValueType: false),
      new("CompletedItems", INT, IsValueType: true),
      new("FailedItems", INT, IsValueType: true),
      _totalItems(),
    ], IsHookEvent: false),

    new("ResetEvent", "Whizbang.Sagas.ISagaResetEvent", [
      _sagaName(),
      _entityId(),
      _itemIdentifier(),
      new("PreviousStatus", SAGA_ITEM_STATE, IsValueType: true),
    ], IsHookEvent: false),

    new("HookStartedEvent", "Whizbang.Sagas.ISagaHookStartedEvent", [
      _sagaName(),
      _entityId(),
      _hookName(),
      _displayName(),
    ], IsHookEvent: true),

    new("HookCompletedEvent", "Whizbang.Sagas.ISagaHookCompletedEvent", [
      _sagaName(),
      _entityId(),
      _hookName(),
      _displayName(),
      new("Status", SAGA_ITEM_STATE, IsValueType: true),
      new("ErrorMessage", NULLABLE_STRING, IsValueType: false),
      new("ErrorDetails", NULLABLE_STRING, IsValueType: false),
    ], IsHookEvent: true),
  ];
}
