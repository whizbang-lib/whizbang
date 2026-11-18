// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Messaging;

/// <summary>
/// Sealing: closing settled digest epochs so integrity answers read immutable folds instead of re-aggregating history
/// (<c>Whizbang:StreamIntegrity:Sealing</c>, #2353). On by default.
/// </summary>
/// <remarks>
/// Sealing and re-checking sealed history are different jobs with different costs. Sealing costs one fold per epoch,
/// once, and is what keeps every later audit bounded by the open window; a re-check costs a fold per epoch on every run
/// and grows with sealed history. Each therefore has its own switch, and turning a re-check off never stops sealing.
/// </remarks>
/// <docs>resilience/stream-integrity#sealing</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/IntegrityCheckOptionsTests.cs</tests>
public sealed class SealingOptions {
  /// <summary>
  /// Close and seal settled digest epochs on the maintenance cadence (<c>Whizbang:StreamIntegrity:Sealing:Enabled</c>,
  /// default true). Without closure the frontier never moves and every audit keeps paying the full-history cost the
  /// epochs exist to end. Turn it off only on engines that serve no integrity manifests. Replaces
  /// <c>EpochClosureEnabled</c>, which stays an alias for one release.
  /// </summary>
  /// <docs>resilience/stream-integrity#sealing</docs>
  public bool Enabled { get; set; } = true;

  /// <summary>
  /// The epoch width in sequence positions (<c>Whizbang:StreamIntegrity:Sealing:EpochWidth</c>, default 100,000). It is
  /// written to the store's <c>integrity_epoch_width</c> setting before each closure pass, and a lane pins the width in
  /// effect the first time it is closed: changing it later changes only lanes met for the first time afterwards. Must be
  /// at least 1.
  /// </summary>
  /// <docs>resilience/stream-integrity#sealing</docs>
  public long EpochWidth { get; set; } = 100_000;

  /// <summary>
  /// Epochs closed per maintenance cycle across all lanes (<c>Whizbang:StreamIntegrity:Sealing:MaxClosuresPerCycle</c>,
  /// default 64). Each closure folds one epoch's events, so this bounds a cycle by the operator's cap rather than by the
  /// backlog: a long-unclosed store catches up over several cycles. Must be at least 1. Replaces
  /// <c>MaxEpochClosuresPerMaintenanceCycle</c>, which stays an alias for one release.
  /// </summary>
  /// <docs>resilience/stream-integrity#sealing</docs>
  public int MaxClosuresPerCycle { get; set; } = 64;

  /// <summary>
  /// A cut of this service's own history (a truncate, close the books, an archive, an end-of-life cut) also waits until
  /// every consumer has reported verifying it (<c>Whizbang:StreamIntegrity:Sealing:CutWaitsForConsumers</c>, default
  /// false, #2363). Every cut already waits for this service's own seal while sealing is on, so it removes only history
  /// it has verified; with this on, it also removes only history every consumer verified while this service could still
  /// resend it. The cost is that the slowest consumer holds back this service's cuts. A consumer reports its seal with
  /// each windowed manifest request, and only reports from the last 7 days count, so a consumer that stopped asking
  /// holds nothing back for long.
  /// </summary>
  /// <docs>resilience/stream-integrity#a-cut-waits-for-the-seal</docs>
  public bool CutWaitsForConsumers { get; set; }
}

/// <summary>What the sealed-history re-check does with an epoch whose recompute disagrees with its seal.</summary>
/// <docs>resilience/stream-integrity#seal-re-check</docs>
public enum SealDriftAction {
  /// <summary>Refold the epoch from the store, so the seal matches what is stored (the behavior before #2353).</summary>
  Refold = 0,

  /// <summary>Count and log the drift and leave the seal as it is, so an operator can investigate before anything moves.</summary>
  ReportOnly = 1,
}

/// <summary>
/// The sealed-history re-check: recomputing sealed epochs from the store and comparing each with its seal
/// (<c>Whizbang:StreamIntegrity:SealRecheck</c>, #2353). Off by default.
/// </summary>
/// <remarks>
/// <para>
/// Integrity answers trust sealed epochs without re-verifying them, which is what keeps them cheap. The re-check is the
/// suspenders on that belt: it catches a seal that disagrees with what is stored, which only an unaccounted write path
/// produces. Its cost grows with sealed history, so it is off by default and sized by the operator.
/// </para>
/// <para>
/// A run re-checks the epochs sealed within <see cref="Lookback"/> that were not re-checked within
/// <see cref="RecheckEvery"/>, least recently re-checked first, at most <see cref="MaxEpochsPerRun"/> of them. Each
/// epoch records when it was sealed and when it was last re-checked, so a run resumes where the last one stopped and
/// never starts again at the first epoch.
/// </para>
/// </remarks>
/// <docs>resilience/stream-integrity#seal-re-check</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/IntegrityCheckOptionsTests.cs</tests>
public sealed class SealRecheckOptions {
  /// <summary>
  /// Run the sealed-history re-check (<c>Whizbang:StreamIntegrity:SealRecheck:Enabled</c>, default false).
  /// </summary>
  /// <docs>resilience/stream-integrity#seal-re-check</docs>
  public bool Enabled { get; set; }

  /// <summary>
  /// The cron the re-check runs on, on the temporal engine (<c>Whizbang:StreamIntegrity:SealRecheck:Schedule</c>,
  /// default <c>"0 3 * * *"</c>, daily at 03:00 UTC). A minute field of <c>0</c> is replaced with a stable per-service
  /// minute so a fleet sharing one database server does not re-check in unison. Null or empty leaves the re-check to the
  /// every-Nth-audit fallback (<see cref="StreamIntegrityOptions.FullSweepEveryNthAudit"/>), as on a host without the
  /// temporal engine.
  /// </summary>
  /// <docs>resilience/stream-integrity#seal-re-check</docs>
  public string? Schedule { get; set; } = "0 3 * * *";

  /// <summary>
  /// How far back, by the time an epoch was sealed, the re-check reaches (<c>Whizbang:StreamIntegrity:SealRecheck:Lookback</c>,
  /// default 30 days). An epoch sealed earlier than this is trusted as sealed. Must be positive.
  /// </summary>
  /// <docs>resilience/stream-integrity#seal-re-check</docs>
  public TimeSpan Lookback { get; set; } = TimeSpan.FromDays(30);

  /// <summary>
  /// The rotation interval (<c>Whizbang:StreamIntegrity:SealRecheck:RecheckEvery</c>, default 7 days): each epoch is
  /// re-checked at most once per interval, so daily runs spread the lookback over a week instead of repeating it. Must be
  /// positive.
  /// </summary>
  /// <docs>resilience/stream-integrity#seal-re-check</docs>
  public TimeSpan RecheckEvery { get; set; } = TimeSpan.FromDays(7);

  /// <summary>
  /// Epochs re-checked per run across all lanes (<c>Whizbang:StreamIntegrity:SealRecheck:MaxEpochsPerRun</c>, default
  /// 10,000). Each re-check folds one epoch's events. Must be at least 1. Replaces <c>MaxEpochVerificationsPerSweep</c>,
  /// which stays an alias for one release.
  /// </summary>
  /// <docs>resilience/stream-integrity#seal-re-check</docs>
  public int MaxEpochsPerRun { get; set; } = 10_000;

  /// <summary>
  /// What a drifted epoch gets (<c>Whizbang:StreamIntegrity:SealRecheck:OnDrift</c>, default
  /// <see cref="SealDriftAction.Refold"/>).
  /// </summary>
  /// <docs>resilience/stream-integrity#seal-re-check</docs>
  public SealDriftAction OnDrift { get; set; } = SealDriftAction.Refold;
}

/// <summary>
/// The full-history compare: healing the whole stored digest table against a recompute, and asking each origin for a
/// manifest over its full history instead of the window above the seal (<c>Whizbang:StreamIntegrity:FullHistoryCompare</c>,
/// #2353). Off by default.
/// </summary>
/// <remarks>
/// The routine audit compares only the open window above each seal, and the routine windowed heal stays on; neither
/// grows with history. The full-history compare does, and it re-verifies exactly what the seals already proved, so it
/// is an opt-in.
/// </remarks>
/// <docs>resilience/stream-integrity#full-history-compare</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/IntegrityCheckOptionsTests.cs</tests>
public sealed class FullHistoryCompareOptions {
  /// <summary>
  /// Run the full-history compare (<c>Whizbang:StreamIntegrity:FullHistoryCompare:Enabled</c>, default false).
  /// </summary>
  /// <docs>resilience/stream-integrity#full-history-compare</docs>
  public bool Enabled { get; set; }

  /// <summary>
  /// The cron the compare runs on, on the temporal engine (<c>Whizbang:StreamIntegrity:FullHistoryCompare:Schedule</c>,
  /// default <c>"0 3 * * *"</c>), splayed per service like <see cref="SealRecheckOptions.Schedule"/>. Null or empty leaves
  /// the compare to the every-Nth-audit fallback (<see cref="StreamIntegrityOptions.FullSweepEveryNthAudit"/>).
  /// </summary>
  /// <docs>resilience/stream-integrity#full-history-compare</docs>
  public string? Schedule { get; set; } = "0 3 * * *";

  /// <summary>
  /// How far back the compare reaches (<c>Whizbang:StreamIntegrity:FullHistoryCompare:Lookback</c>, default 90 days). The
  /// heal covers streams last written within it, and the ask starts from the oldest epoch sealed within it; older history
  /// is the sealed epochs', which the sealed-history re-check covers. Must be positive (#2361).
  /// </summary>
  /// <docs>resilience/stream-integrity#full-history-compare</docs>
  public TimeSpan Lookback { get; set; } = TimeSpan.FromDays(90);

  /// <summary>
  /// Streams one run heals (<c>Whizbang:StreamIntegrity:FullHistoryCompare:MaxPerRun</c>, default 10,000). The heal takes
  /// the next this many streams in key order after a stored cursor, and the next run resumes after the last of them, so
  /// one run stays within the idle maintenance budget however large the store is. Must be at least 1 (#2361).
  /// </summary>
  /// <docs>resilience/stream-integrity#full-history-compare</docs>
  public int MaxPerRun { get; set; } = 10_000;
}

/// <summary>
/// One run of the full-history compare's heal (<see cref="IWorkCoordinator.VerifyDigestTableSliceAsync"/>, #2361).
/// </summary>
/// <param name="SettleWindow">Buckets and events younger than this are left out of the comparison.</param>
/// <param name="Lookback">Only streams last written within this are healed.</param>
/// <param name="MaxPerRun">Streams the run takes after the stored cursor.</param>
/// <docs>resilience/stream-integrity#full-history-compare</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamDigestTableSqlTests.Slices.cs:ASlice_HealsAtMostMaxPerRunStreams_AndTheNextResumesAfterItAsync</tests>
public sealed record FullHistoryCompareRequest(TimeSpan SettleWindow, TimeSpan Lookback, int MaxPerRun) {
  /// <summary>The request the configured <see cref="FullHistoryCompareOptions"/> describe.</summary>
  /// <param name="options">The compare's options.</param>
  /// <param name="settleWindow">The audit's settle window.</param>
  /// <returns>The request.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/IntegrityCheckOptionsTests.cs:FullHistoryCompareRequest_From_CarriesTheConfiguredLimitsAsync</tests>
  public static FullHistoryCompareRequest From(FullHistoryCompareOptions options, TimeSpan settleWindow) {
    ArgumentNullException.ThrowIfNull(options);
    return new(settleWindow, options.Lookback, options.MaxPerRun);
  }
}

/// <summary>
/// One call of the sealed-history re-check (<see cref="IWorkCoordinator.RecheckDigestEpochsAsync"/>, #2353).
/// </summary>
/// <param name="SettleWindow">An arrival younger than this blocks its epoch, which is skipped and stays first in line.</param>
/// <param name="MaxEpochs">Epochs recomputed this call across all lanes.</param>
/// <param name="Lookback">Only epochs sealed within this are re-checked.</param>
/// <param name="RecheckEvery">An epoch re-checked within this is not due yet; zero makes every epoch due.</param>
/// <param name="Refold">Refold a drifted epoch from the store (true), or only count it (false).</param>
/// <docs>resilience/stream-integrity#seal-re-check</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SealRecheckRotationSqlTests.cs:Coordinator_RecheckDigestEpochsAsync_DrivesTheSqlFunctionAsync</tests>
public sealed record SealRecheckRequest(
  TimeSpan SettleWindow, int MaxEpochs, TimeSpan Lookback, TimeSpan RecheckEvery, bool Refold) {

  /// <summary>The request the configured <see cref="SealRecheckOptions"/> describe.</summary>
  /// <param name="options">The re-check options.</param>
  /// <param name="settleWindow">The audit's settle window, which sealing and the re-check share.</param>
  /// <returns>The request.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/IntegrityCheckOptionsTests.cs:SealRecheckRequest_From_CarriesTheConfiguredLimitsAsync</tests>
  public static SealRecheckRequest From(SealRecheckOptions options, TimeSpan settleWindow) {
    ArgumentNullException.ThrowIfNull(options);
    return new(settleWindow, options.MaxEpochsPerRun, options.Lookback, options.RecheckEvery,
      options.OnDrift == SealDriftAction.Refold);
  }
}
