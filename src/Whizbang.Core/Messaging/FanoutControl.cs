using System;
using System.Collections.Generic;
using System.Threading;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Declarative fan-out trigger for a composite event. Read off the composite type (default
/// <see cref="Auto"/>); overridable per composite via <c>CompositeEventBase.FanoutMode</c>.
/// </summary>
/// <docs>fundamentals/messaging/composite-events#fanout-control</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/InboxDispatchWorkerTests.cs:CompositeFanoutMode_Manual_NoReceptorDirective_FansOutNothingAsync</tests>
public enum FanoutMode {
  /// <summary>Zero-config: the dispatch seam automatically fans out <c>InnerEvents</c>.</summary>
  Auto,

  /// <summary>
  /// A pre-fanout receptor drives fan-out by setting a <see cref="FanoutDirective"/> via
  /// <see cref="DispatchFanoutControl"/>. Without an explicit directive, nothing is fanned out.
  /// </summary>
  Manual,
}

/// <summary>
/// Per-child failure policy during composite fan-out. Read off the composite type (default
/// <see cref="Independent"/>); overridable via <c>CompositeEventBase.Atomicity</c>.
/// </summary>
/// <docs>fundamentals/messaging/composite-events#fanout-control</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/CompositeInboxFanoutTests.cs:TryExpand_NullInner_Atomic_ReturnsFailedAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/CompositeInboxFanoutTests.cs:TryExpand_NullInner_Independent_DropsBadChildAndKeepsRestAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/CompositeInboxFanoutTests.cs:TryExpand_NullInner_Independent_LogsTheDroppedChildAsync</tests>
public enum FanoutAtomicity {
  /// <summary>
  /// One bad child doesn't sink the batch — a child that fails to serialize is dropped (logged) and the
  /// remaining children still fan out. Use when inner events are independent (the common bulk case).
  /// </summary>
  Independent,

  /// <summary>
  /// All-or-nothing — any child failure dead-letters the whole composite (no partial fan-out). Use when
  /// the inner events are one logical unit (e.g. one job's field events on a shared stream).
  /// </summary>
  Atomic,
}

/// <summary>
/// The disposition a pre-fanout receptor can impose on fan-out, set imperatively via
/// <see cref="DispatchFanoutControl"/>. Takes precedence over the composite's declarative
/// <see cref="FanoutMode"/>.
/// </summary>
/// <docs>fundamentals/messaging/composite-events#fanout-control</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/DispatchFanoutControlTests.cs:ReplaceWith_CarriesReplacementChildrenAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/DispatchFanoutControlTests.cs:Begin_CapturesDirectiveSetDuringWindowAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/InboxDispatchWorkerTests.cs:CompositeDirective_ReplaceWith_FansOutReplacementSetAsync</tests>
public sealed class FanoutDirective {
  private FanoutDirective(FanoutDirectiveKind kind, IReadOnlyList<IMessage>? replacement) {
    Kind = kind;
    Replacement = replacement;
  }

  /// <summary>Which disposition this directive carries.</summary>
  public FanoutDirectiveKind Kind { get; }

  /// <summary>The replacement inner events when <see cref="Kind"/> is <see cref="FanoutDirectiveKind.ReplaceWith"/>; null otherwise.</summary>
  public IReadOnlyList<IMessage>? Replacement { get; }

  /// <summary>Fan out the composite's own <c>InnerEvents</c> (the default disposition).</summary>
  public static FanoutDirective Proceed { get; } = new(FanoutDirectiveKind.Proceed, null);

  /// <summary>Do not fan out — the pre-fanout receptor has fully handled the composite.</summary>
  public static FanoutDirective Skip { get; } = new(FanoutDirectiveKind.Skip, null);

  /// <summary>Fan out <paramref name="children"/> instead of the composite's own inner events (filter / transform / re-key).</summary>
  public static FanoutDirective ReplaceWith(IReadOnlyList<IMessage> children) {
    ArgumentNullException.ThrowIfNull(children);
    return new FanoutDirective(FanoutDirectiveKind.ReplaceWith, children);
  }
}

/// <summary>The three pre-fanout dispositions. See <see cref="FanoutDirective"/>.</summary>
public enum FanoutDirectiveKind {
  /// <summary>Fan out the composite's own inner events.</summary>
  Proceed,
  /// <summary>Suppress fan-out entirely.</summary>
  Skip,
  /// <summary>Fan out a receptor-supplied replacement set.</summary>
  ReplaceWith,
}

/// <summary>
/// Ambient (per-async-flow) control a pre-fanout receptor uses to override composite fan-out. The
/// dispatch worker opens a control before invoking the composite's receptor and reads the directive
/// after; a receptor sets it via <see cref="Set"/>. AsyncLocal-backed so it flows across DI scopes,
/// mirroring <see cref="DispatchOutboxCollector"/>.
/// </summary>
/// <remarks>
/// Receptors inject nothing — they call the static <see cref="Set"/> while handling the composite. When
/// no control is open (no composite in flight) <see cref="Set"/> is a no-op, so a misfired call outside
/// the pre-fanout window can't corrupt unrelated dispatch.
/// </remarks>
/// <docs>fundamentals/messaging/composite-events#fanout-control</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/DispatchFanoutControlTests.cs:Set_OutsideControlWindow_IsNoOp_DoesNotLeakIntoNextWindowAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/DispatchFanoutControlTests.cs:Begin_NestsAndRestoresOuterControlAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/DispatchFanoutControlTests.cs:Begin_CapturesDirectiveSetDuringWindowAsync</tests>
public sealed class DispatchFanoutControl {
  private static readonly AsyncLocal<DispatchFanoutControl?> _current = new();

  private DispatchFanoutControl() { }

  /// <summary>The directive a receptor imposed, or null if none was set.</summary>
  public FanoutDirective? Directive { get; private set; }

  /// <summary>
  /// Sets the fan-out directive for the composite currently being dispatched. No-op when no control is
  /// open on the async flow (i.e. not inside the pre-fanout window).
  /// </summary>
  public static void Set(FanoutDirective directive) {
    ArgumentNullException.ThrowIfNull(directive);
    if (_current.Value is { } control) {
      control.Directive = directive;
    }
  }

  /// <summary>Opens a control on the current async flow. Disposing restores the previous control (supports nesting).</summary>
  public static ControlScope Begin() {
    var previous = _current.Value;
    var control = new DispatchFanoutControl();
    _current.Value = control;
    return new ControlScope(control, previous);
  }

  /// <summary>Disposable handle for an open control. Disposing restores the previously-active control.</summary>
  public readonly struct ControlScope : IDisposable {
    private readonly DispatchFanoutControl _control;
    private readonly DispatchFanoutControl? _previous;

    internal ControlScope(DispatchFanoutControl control, DispatchFanoutControl? previous) {
      _control = control;
      _previous = previous;
    }

    /// <summary>The control this scope opened.</summary>
    public DispatchFanoutControl Control => _control;

    /// <inheritdoc />
    public void Dispose() => _current.Value = _previous;
  }
}
