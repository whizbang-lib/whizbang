namespace Whizbang.Core.Signals;

/// <summary>
/// Kind of a <see cref="SignalTarget"/>: broadcast (every instance), streams (resolve owners via
/// <c>notify_instance_owners</c>), or instance (direct route to one instance).
/// </summary>
/// <docs>fundamentals/signal-bus/signal-bus</docs>
public enum SignalTargetKind {
  /// <summary>No target — every instance's broadcast channel. Default value.</summary>
  Broadcast = 0,

  /// <summary>Resolve owners of the given streams via <c>notify_instance_owners</c>.</summary>
  Streams = 1,

  /// <summary>Direct-route to a specific instance's channel (<c>wh_work_i_&lt;id&gt;</c>).</summary>
  Instance = 2,
}

/// <summary>
/// Per-publish target selector for <see cref="ISignalBus.PublishAsync{TSignal}"/>. The signal
/// type's static <see cref="ISignal"/><see cref="SignalTargeting"/> declares whether it is
/// broadcast or targeted; the <see cref="SignalTarget"/> at the call site says <em>which</em>
/// target for a targeted signal (which streams' owners to wake, or which instance directly).
/// Broadcast signals default their target to <see cref="Broadcast"/> — no extra parameters at
/// the call site.
/// </summary>
/// <docs>fundamentals/signal-bus/signal-bus</docs>
public readonly struct SignalTarget {
  private readonly IReadOnlyList<Guid>? _streamIds;
  private readonly Guid _instanceId;
  private readonly SignalTargetKind _kind;

  private SignalTarget(SignalTargetKind kind, IReadOnlyList<Guid>? streamIds, Guid instanceId) {
    _kind = kind;
    _streamIds = streamIds;
    _instanceId = instanceId;
  }

  /// <summary>The kind of target this value carries.</summary>
  public SignalTargetKind Kind => _kind;

  /// <summary>Stream ids for <see cref="SignalTargetKind.Streams"/>. Empty for other kinds.</summary>
  public IReadOnlyList<Guid> StreamIds => _streamIds ?? [];

  /// <summary>Instance id for <see cref="SignalTargetKind.Instance"/>. <see cref="Guid.Empty"/> for other kinds.</summary>
  public Guid InstanceId => _instanceId;

  /// <summary>No target — every instance's broadcast channel. Same as <see langword="default"/>.</summary>
  public static SignalTarget Broadcast => default;

  /// <summary>
  /// Resolve the owning instance(s) via <c>notify_instance_owners(payload, stream_ids)</c> —
  /// one NOTIFY per unique owner, exactly like today's work-wake fan-out. Streams that are not
  /// yet in <c>wh_active_streams</c> are routed to the deterministic partition-modulo owner.
  /// </summary>
  public static SignalTarget Streams(IReadOnlyList<Guid> streamIds) {
    ArgumentNullException.ThrowIfNull(streamIds);
    if (streamIds.Count == 0) {
      throw new ArgumentException("Targeted publish requires at least one stream id.", nameof(streamIds));
    }
    return new SignalTarget(SignalTargetKind.Streams, streamIds, Guid.Empty);
  }

  /// <summary>
  /// Direct-route to a specific instance's channel (<c>wh_work_i_&lt;instanceId&gt;</c>). Used
  /// when the caller already knows the target instance (e.g. instance-lifecycle triggers).
  /// </summary>
  public static SignalTarget Instance(Guid instanceId) {
    if (instanceId == Guid.Empty) {
      throw new ArgumentException("Instance target requires a non-empty instance id.", nameof(instanceId));
    }
    return new SignalTarget(SignalTargetKind.Instance, null, instanceId);
  }
}
