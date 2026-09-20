using Whizbang.Core;

namespace Whizbang.Sagas;

/// <summary>
/// Marks a partial class as a saga. The
/// <c>Whizbang.Sagas.Generators</c> source generator picks up the
/// marked type and emits, into a sibling <c>.g.cs</c> file, nine
/// nested event classes, a typed <c>Service : BaseSagaService&lt;...&gt;</c>
/// with factory overrides, and an <c>Add{ClassName}()</c> DI extension.
/// </summary>
/// <remarks>
/// Use this form when no project-wide event base is needed —
/// generated event classes inherit
/// <see cref="SagaEventBase"/> (Whizbang's minimal <see cref="IEvent"/>
/// implementation). Consumers with their own event hierarchy supply
/// it via <see cref="SagaAttribute{TEventBase}"/>.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class SagaAttribute : Attribute {

  /// <summary>Constructs the attribute with the saga's name.</summary>
  public SagaAttribute(string sagaName) {
    SagaName = sagaName;
  }

  /// <summary>Saga name (matches the value emitted as the <c>SagaName</c> const on the marked class).</summary>
  public string SagaName { get; }

  /// <summary>When false, the generator omits <c>HookStartedEvent</c> / <c>HookCompletedEvent</c> and stubs the hook factory overrides to throw.</summary>
  public bool IncludeHooks { get; init; } = true;

  /// <summary>When false, the generator omits the <c>Service</c> class and the DI extension — consumers write a custom <c>Service</c> subclass.</summary>
  public bool GenerateService { get; init; } = true;
}

/// <summary>
/// Generic variant of <see cref="SagaAttribute"/>. Generated event
/// classes inherit <typeparamref name="TEventBase"/> instead of the
/// default <c>SagaEventBase</c> — letting consumers preserve their
/// own event hierarchy (audit metadata, tenant context, notification
/// tags, …) without re-implementing every event class by hand.
/// </summary>
/// <typeparam name="TEventBase">
/// Consumer's event base type. Must be a reference type implementing
/// <see cref="IEvent"/>; the emitted nested event classes derive from
/// it and add the <see cref="ISagaEvent"/>-family interface properties
/// they need.
/// </typeparam>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class SagaAttribute<TEventBase> : Attribute where TEventBase : class, IEvent {

  /// <summary>Constructs the attribute with the saga's name.</summary>
  public SagaAttribute(string sagaName) {
    SagaName = sagaName;
  }

  /// <summary>Saga name (matches the value emitted as the <c>SagaName</c> const on the marked class).</summary>
  public string SagaName { get; }

  /// <summary>When false, the generator omits <c>HookStartedEvent</c> / <c>HookCompletedEvent</c> and stubs the hook factory overrides to throw.</summary>
  public bool IncludeHooks { get; init; } = true;

  /// <summary>When false, the generator omits the <c>Service</c> class and the DI extension — consumers write a custom <c>Service</c> subclass.</summary>
  public bool GenerateService { get; init; } = true;
}
