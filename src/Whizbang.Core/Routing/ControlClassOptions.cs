using System;

namespace Whizbang.Core.Routing;

/// <summary>
/// The control class's delivery semantics (transport traffic classes, topology arc phase 9). The
/// <c>sys-control</c> class changes HOW a message is delivered, not just WHERE: control messages
/// are minted with a short TTL, consumed from sessionless subscriptions, and — under the
/// non-durable receive path — compared and discarded without an inbox row.
/// </summary>
/// <remarks>
/// <para>
/// <b>TTL.</b> A supersedable control message's value expires: the next cadence re-derives it.
/// Minting it with <c>TimeToLive ≈ CadenceMultiplier × cadence</c> means a superseded checkpoint
/// expires ON THE BROKER instead of queueing, so a control backlog is structurally impossible —
/// the transport-level twin of ephemeral events. The multiplier (not 1×) is the slack that keeps
/// a legitimately slow-but-progressing message alive: one whole cadence of headroom past the point
/// where a successor exists. <see cref="TimeToLiveFloor"/> keeps a very fast cadence from minting
/// a lifetime shorter than a healthy broker round-trip.
/// </para>
/// <para>
/// <b>Why two knobs are opt-in.</b> <see cref="SessionlessSubscriptions"/> re-provisions broker
/// topology (Service Bus cannot toggle <c>RequiresSession</c> in place — the subscription is
/// deleted and recreated) and <see cref="NonDurableReceive"/> removes the durable inbox row that
/// today's retry and dead-letter machinery is built on. Both are therefore migration steps a host
/// opts into — unlike the per-namespace inbox topology and its shared-inbox retirement, which are
/// the DEFAULT. TTL minting is on by default because it is the half that cannot lose information:
/// its only members are messages whose successor is already scheduled.
/// </para>
/// <para>
/// Bindable from <c>Whizbang:Routing:ControlClass</c>.
/// </para>
/// </remarks>
/// <docs>fundamentals/dispatcher/routing#control-class</docs>
/// <tests>tests/Whizbang.Core.Tests/Routing/ControlClassOptionsTests.cs:DeriveTimeToLive_IsMaxOfFloorAndCadenceTimesMultiplierAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Routing/ControlClassOptionsTests.cs:Options_BindFromConfigurationAsync</tests>
public sealed class ControlClassOptions {
#pragma warning disable CA1707 // project convention: public const/static readonly use UPPER_CASE with underscores
  /// <summary>
  /// The shipped TTL floor (30 seconds). No derived lifetime is ever shorter: below roughly this,
  /// a normal broker round-trip plus a consumer restart can outrun the message and the class would
  /// start dropping signals that were never superseded.
  /// </summary>
  public static readonly TimeSpan DEFAULT_TIME_TO_LIVE_FLOOR = TimeSpan.FromSeconds(30);

  /// <summary>The shipped cadence multiplier (2) — the spec's <c>TTL ≈ 2× cadence</c> rule.</summary>
  public const int DEFAULT_CADENCE_MULTIPLIER = 2;
#pragma warning restore CA1707

  /// <summary>
  /// Killswitch for control-class TTL minting (default <c>true</c>). When false the mint stamps no
  /// lifetime at all and control messages keep the broker's entity default — the pre-phase-9 wire
  /// shape exactly, not merely a long TTL.
  /// </summary>
  public bool Enabled { get; set; } = true;

  /// <summary>
  /// How many cadences a minted control message may outlive its own emission
  /// (default <see cref="DEFAULT_CADENCE_MULTIPLIER"/>). Values below 1 fall back to
  /// <see cref="TimeToLiveFloor"/>.
  /// </summary>
  public int CadenceMultiplier { get; set; } = DEFAULT_CADENCE_MULTIPLIER;

  /// <summary>
  /// The shortest lifetime the derivation may produce (default
  /// <see cref="DEFAULT_TIME_TO_LIVE_FLOOR"/>).
  /// </summary>
  public TimeSpan TimeToLiveFloor { get; set; } = DEFAULT_TIME_TO_LIVE_FLOOR;

  /// <summary>
  /// Operator override for the minted lifetime. Null (default) derives from the caller's cadence;
  /// an explicit value bypasses BOTH the derivation and the floor.
  /// </summary>
  public TimeSpan? TimeToLive { get; set; }

  /// <summary>
  /// Provision control-class subscriptions WITHOUT sessions (default <c>false</c> — opt-in).
  /// Control consumers need no ordering, so the accept/lock machinery is pure cost for this class;
  /// removing it also restores the broker's own delivery-count dead-letter valve, which on a
  /// session-enabled entity can never fire under connection-death lock loss (topology arc
  /// phase 8.5).
  /// </summary>
  public bool SessionlessSubscriptions { get; set; }

  /// <summary>
  /// Take control-class messages off the durable inbox (default <c>false</c> — opt-in): receive,
  /// compare (the class's receptors run inline at the receive boundary), discard. No inbox row, no
  /// completion bookkeeping, and a failure drops instead of dead-lettering — the receive-boundary
  /// extension of the rule the dead-letter boundary already applies to control-plane traffic.
  /// </summary>
  public bool NonDurableReceive { get; set; }

  /// <summary>
  /// The lifetime to mint for a message emitted on <paramref name="cadence"/>: the override when
  /// one is configured, the derivation otherwise.
  /// </summary>
  /// <param name="cadence">The emitter's cadence.</param>
  /// <returns>The effective time-to-live; always strictly positive.</returns>
  public TimeSpan EffectiveTimeToLive(TimeSpan cadence) =>
    TimeToLive ?? DeriveTimeToLive(cadence, CadenceMultiplier, TimeToLiveFloor);

  /// <summary>
  /// The derivation, as a pure function so it can be reasoned about and property-tested rather
  /// than trusted at one call site: <c>max(floor, cadence × multiplier)</c>, with every degenerate
  /// input collapsing to a usable floor instead of to a zero-length (instantly dead) lifetime.
  /// </summary>
  /// <param name="cadence">The emitter's cadence. Non-positive ⇒ the floor.</param>
  /// <param name="cadenceMultiplier">Cadences of headroom. Below 1 ⇒ the floor.</param>
  /// <param name="floor">The shortest permitted lifetime. Non-positive ⇒
  /// <see cref="DEFAULT_TIME_TO_LIVE_FLOOR"/> when the cadence is also unusable.</param>
  /// <returns>The derived time-to-live; saturates at <see cref="TimeSpan.MaxValue"/>.</returns>
  public static TimeSpan DeriveTimeToLive(TimeSpan cadence, int cadenceMultiplier, TimeSpan floor) {
    var effectiveFloor = floor > TimeSpan.Zero ? floor : TimeSpan.Zero;

    if (cadence <= TimeSpan.Zero || cadenceMultiplier < 1) {
      return effectiveFloor > TimeSpan.Zero ? effectiveFloor : DEFAULT_TIME_TO_LIVE_FLOOR;
    }

    TimeSpan derived;
    try {
      derived = cadence * cadenceMultiplier;
    } catch (OverflowException) {
      // A degenerate configuration must not fault a publish; saturate at the largest lifetime,
      // which is operationally "never expires" — the pre-phase-9 behavior.
      return TimeSpan.MaxValue;
    }

    return derived > effectiveFloor ? derived : effectiveFloor;
  }
}
