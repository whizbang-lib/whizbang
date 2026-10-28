// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

extern alias shared;

using System.Diagnostics.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using TemplateUtilities = shared::Whizbang.Generators.Shared.Utilities.TemplateUtilities;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Phase H step 7 slice 6 — invariant locks for PerspectiveRunnerTemplate.cs log levels.
/// The template controls every generated runner's idempotency-guard logging. Bumping a level
/// here changes log volume on every Whizbang consumer, so we lock the rules in tests.
/// </summary>
public class PerspectiveRunnerTemplateInvariantTests {
  private static readonly System.Reflection.Assembly _generatorsAssembly = typeof(MessageRegistryGenerator).Assembly;

  private static string _loadTemplate() {
    return TemplateUtilities.GetEmbeddedTemplate(
        _generatorsAssembly,
        "PerspectiveRunnerTemplate.cs"
    );
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task AllSkippedBranch_LogsAtDebugLevel_NotInformationAsync() {
    var template = _loadTemplate();

    // The all-skipped self-heal branch must be Debug. With the drainer's cooldown gate
    // (step 7 slice 5) active, this branch only fires post-restart / TTL expiry / handler
    // failure recovery — rare. INF firing per cursor-flush race produced a high volume of
    // noise on a consumer's BFF; Debug is the right level for the post-step-7 invariant.
    await Assert.That(template).Contains("[diag.filter.all-dropped]")
      .Because("the all-skipped branch's log message is the anchor we're pinning");
    var idx = template.IndexOf("[diag.filter.all-dropped]", StringComparison.Ordinal);
    await Assert.That(idx).IsGreaterThan(0);

    // Walk back to find the LogXxx call that owns this message.
    var prefix = template[..idx];
    var lastLogDebug = prefix.LastIndexOf("LogDebug", StringComparison.Ordinal);
    var lastLogInformation = prefix.LastIndexOf("LogInformation", StringComparison.Ordinal);

    await Assert.That(lastLogDebug).IsGreaterThan(lastLogInformation)
      .Because("the all-skipped self-heal log must be LogDebug, not LogInformation (step 7 slice 6)");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task FilterBlock_RewindsFromAnEventBelowThePositionBeforeItDropsAnythingAsync() {
    // #1347 regression lock: the filter must never drop an event below the row's position on that evidence alone. The
    // runner asks FindRewindAnchor first and re-folds through the lock-free rewind core; RewindAndRunAsync would take
    // the apply lock this method already holds and deadlock.
    var template = _loadTemplate();
    var anchor = template.IndexOf("PerspectiveIdempotencyFilter.FindRewindAnchor(", StringComparison.Ordinal);
    var drop = template.IndexOf("PerspectiveIdempotencyFilter.IsAlreadyApplied(", StringComparison.Ordinal);
    var rewindCall = template.IndexOf("return await _rewindAndRunCoreAsync(streamId, perspectiveName, anchorEventId", StringComparison.Ordinal);

    await Assert.That(anchor).IsGreaterThan(0).Because("the runner must ask for a rewind anchor");
    await Assert.That(drop).IsGreaterThan(anchor).Because("the anchor check must come before any event is judged already applied");
    await Assert.That(rewindCall).IsGreaterThan(anchor).Because("an anchor must re-fold the stream");
    await Assert.That(rewindCall).IsLessThan(drop).Because("the re-fold must happen before the drop loop, not after it");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task FoldCheckpoints_RecordTheCommitSequenceThroughOneRuleAsync() {
    // #1352 regression lock: both checkpoint writes of a fold go through _recordedCommitSequence, which records a
    // rebuild's highest folded commit sequence. Looking up the last event's own sequence directly put a rebuilt row's
    // position below events it had folded, and the store's ordering guard refused the corrected write.
    var template = _loadTemplate();
    var uses = template.Split("_recordedCommitSequence(").Length - 1;

    await Assert.That(uses).IsEqualTo(3).Because("one definition and the two checkpoint writes (full and partial)");
    await Assert.That(template).DoesNotContain("var checkpointCommitSequence = await _eventStore.GetCommitSequenceAsync(")
      .Because("the full checkpoint must not take the last event's sequence as the row's position");
    await Assert.That(template).DoesNotContain("var partialCheckpointCommitSequence = await _eventStore.GetCommitSequenceAsync(")
      .Because("the partial checkpoint must not either");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task ApplyLoop_CallsThrowIfCancellationRequestedBetweenEventsAsync() {
    // Phase H step 9 slice 4 regression lock: the apply loop must check the lease CT between
    // events. Apply is pure synchronous so can't be canceled mid-call, but per-event CT checks
    // let a hot stream with many pending events stop respecting the deadline.
    var template = _loadTemplate();

    // The apply loop folds foldEvents (the batch, or its last event alone on the jump path). This anchor once named
    // `events`, which only the rebuild's partition loop iterated, so the check passed on that loop's position instead.
    await Assert.That(template).Contains("foreach (var envelope in foldEvents)")
      .Because("the apply loop is the anchor for the CT check");
    await Assert.That(template).Contains("cancellationToken.ThrowIfCancellationRequested()")
      .Because("the apply loop must call ThrowIfCancellationRequested at the top of each iteration");

    // Ensure the CT check appears INSIDE the events foreach (not just somewhere in the file).
    var foreachIdx = template.IndexOf("foreach (var envelope in foldEvents)", StringComparison.Ordinal);
    var ctCheckIdx = template.IndexOf("cancellationToken.ThrowIfCancellationRequested()", foreachIdx, StringComparison.Ordinal);
    await Assert.That(ctCheckIdx).IsGreaterThan(foreachIdx)
      .Because("the CT check must appear inside the foreach body, not before it");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task EphemeralRewind_GuardsFullReplayFallbackAgainstReapedBodiesAsync() {
    // E1 (3) invariant lock. An ephemeral stream's consumed bodies are reaped (wh_event_body rows
    // deleted, pointer-only). If RewindAndRunAsync finds no qualifying snapshot floor, it MUST NOT
    // fall through to the full replay-from-zero (RunFromModelAsync with a null model / null anchor) —
    // that would read reaped/absent bodies and silently corrupt the authoritative model. The template
    // guards the fallback with `_isEphemeralPerspective && !hasSnapshot`; the guard must remain, and it
    // must sit between the snapshot lookup and the RunFromModelAsync call so it can short-circuit it.
    var template = _loadTemplate();

    await Assert.That(template).Contains("_isEphemeralPerspective && !hasSnapshot")
      .Because("the ephemeral rewind fallback guard is the anchor we're pinning");
    await Assert.That(template).Contains("#region IS_EPHEMERAL")
      .Because("the generator injects the per-perspective ephemeral flag into this region");

    // The guard must appear inside RewindAndRunAsync and BEFORE the RunFromModelAsync call it protects.
    var rewindIdx = template.IndexOf("async Task<PerspectiveCursorCompletion> RewindAndRunAsync", StringComparison.Ordinal);
    await Assert.That(rewindIdx).IsGreaterThan(0);
    var guardIdx = template.IndexOf("_isEphemeralPerspective && !hasSnapshot", rewindIdx, StringComparison.Ordinal);
    var replayCallIdx = template.IndexOf("result = await RunFromModelAsync(", rewindIdx, StringComparison.Ordinal);
    await Assert.That(guardIdx).IsGreaterThan(rewindIdx)
      .Because("the guard must live inside RewindAndRunAsync");
    await Assert.That(replayCallIdx).IsGreaterThan(guardIdx)
      .Because("the guard must short-circuit the full replay-from-zero, so it precedes the RunFromModelAsync call");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task PartialSkipBranch_StaysAtDebugLevelAsync() {
    // The partial-skip branch was already at Debug per step 6 slice 6. Lock it so a future
    // refactor can't quietly bump it back to Information.
    var template = _loadTemplate();

    await Assert.That(template).Contains("[diag.filter.partial-dropped]");
    var idx = template.IndexOf("[diag.filter.partial-dropped]", StringComparison.Ordinal);
    await Assert.That(idx).IsGreaterThan(0);

    var prefix = template[..idx];
    var lastLogDebug = prefix.LastIndexOf("LogDebug", StringComparison.Ordinal);
    var lastLogInformation = prefix.LastIndexOf("LogInformation", StringComparison.Ordinal);

    await Assert.That(lastLogDebug).IsGreaterThan(lastLogInformation)
      .Because("partial-skip is benign cursor-flush race; must stay at Debug");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task CheckpointMetadata_StampsAppliedEventTime_NotApplyWallClockAsync() {
    // Perspective-row-retention increment 1b: checkpoint metadata's Timestamp is the row-TTL
    // expiry anchor (BaseUpsertStrategy: expires_at = metadata.Timestamp + ttl). Stamping the
    // APPLY wall clock there makes every rebuild re-stamp fresh windows — thousands of
    // expired rows resurrecting per rebuild. The template must thread the checkpoint EVENT's
    // own time (from the envelope's stored hops — deterministic under replay) instead.
    var template = _loadTemplate();

    await Assert.That(template).Contains("EventId = checkpointEventId.ToString(\"D\")")
      .Because("the checkpoint-metadata block is the anchor being pinned");
    await Assert.That(template).Contains("Timestamp = checkpointEventAt")
      .Because("metadata.Timestamp must carry the checkpoint event's time, threaded from the apply loop");
    await Assert.That(template).DoesNotContain("Timestamp = DateTime.UtcNow")
      .Because("apply-time wall clock in checkpoint metadata breaks replay determinism for row TTLs");
    await Assert.That(template).Contains("lastSuccessfulEventAt = ")
      .Because("the apply loops must track the applied envelope's event time alongside its id/type");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task RowNullBranch_ResurrectsSourcedRowTtlStreams_ViaRewindCoreAsync() {
    // Perspective-row-retention increment 4 (resurrection-on-wake): for a row-TTL SOURCED
    // perspective, a missing row is ambiguous — brand-new stream OR a reaped row whose stream
    // just woke. A Sourced row is the fold of ALL its events; applying only the incoming batch
    // onto a fresh model would silently build a corrupt partial row. The row-null branch must
    // probe the log (events before this batch?) and, when history exists, re-fold via the
    // rewind CORE — not the public RewindAndRunAsync, which re-acquires the apply lock RunAsync
    // already holds (deadlock).
    var template = _loadTemplate();

    await Assert.That(template).Contains("HasStreamEventsBeforeAsync")
      .Because("the row-null branch probes the event store for pre-batch history");
    await Assert.That(template).Contains("!_isEphemeralPerspective")
      .Because("an ephemeral perspective rebuilds only from a snapshot floor; its history before the floor is reaped");
    await Assert.That(template).Contains("PerspectiveRowEviction.CanEvict(typeof(__MODEL_TYPE_NAME__))")
      .Because("the probe runs for every perspective whose rows can be evicted (TTL, cap, stream group), and only those (#1483)");
    await Assert.That(template).Contains("_rewindAndRunCoreAsync")
      .Because("resurrection delegates to the lock-free rewind core");

    // Deadlock guard: exactly ONE _applyCoordinator acquisition may exist in each public entry
    // (RunAsync + RewindAndRunAsync); the rewind CORE itself must not acquire it.
    var acquisitions = 0;
    var idx = 0;
    while ((idx = template.IndexOf("_applyCoordinator.AcquireAsync", idx, StringComparison.Ordinal)) >= 0) {
      acquisitions++;
      idx++;
    }
    await Assert.That(acquisitions).IsEqualTo(2)
      .Because("only the two public entry points acquire the apply lock; the shared core assumes it is held");
  }

  [Test]
  public async Task RowNullBranch_ProbesHistoryOfTheHandledEventTypesOnlyAsync() {
    // Issue #696: "does this stream have any earlier event" is the wrong question on a stream
    // shared by several contracts — history the perspective never folded read as "reaped and
    // woken", and every first contact re-folded. The probe must carry the event types this
    // perspective folds, which the generator emits as a static array.
    var template = _loadTemplate();

    await Assert.That(template).Contains("#region HANDLED_EVENT_TYPES")
      .Because("the generator fills the perspective's handled event types into a static array");
    await Assert.That(template).Contains("HasStreamEventsBeforeAsync(streamId, events[0].MessageId.Value, _handledEventTypes, cancellationToken)")
      .Because("the resurrection probe asks about the handled event types only");
    await Assert.That(template).DoesNotContain("HasStreamEventsBeforeAsync(streamId, events[0].MessageId.Value, cancellationToken)")
      .Because("the untyped probe is the false positive");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task RewindLifecycleEvents_AreStampedWithTheirOwnStream_NotTheOneBeingRewoundAsync() {
    // #1360: both publishes passed the domain stream as the event's stream id, and the attribute on that
    // parameter routes the event there, so every rewind appended two framework events into the application
    // stream it had just rewound. They take a version and a commit sequence in it, so they show up in every
    // later replay, audit, stream read and version count of that aggregate.
    var template = _loadTemplate();

    await Assert.That(_firstArgumentOf(template, "new PerspectiveRewindStarted(")).IsEqualTo("rewindStreamId")
      .Because("the event's stream is the rewind's own, which is what the record's own parameter "
             + "documentation has always said it is");
    await Assert.That(_firstArgumentOf(template, "new PerspectiveRewindCompleted(")).IsEqualTo("rewindStreamId")
      .Because("the pair belongs to one rewind, so both carry the same stream and a reader can find them together");

    await Assert.That(_secondArgumentOf(template, "new PerspectiveRewindStarted(")).IsEqualTo("streamId")
      .Because("the rewound stream stays on the event as a plain property; losing it would make the event "
             + "unreadable without joining back through the triggering event");
    await Assert.That(_secondArgumentOf(template, "new PerspectiveRewindCompleted(")).IsEqualTo("streamId");

    var mints = _countOf(template, "TrackedGuid.New()");
    await Assert.That(mints).IsEqualTo(1)
      .Because("one id per rewind, minted once and shared by the pair. Minting per publish would scatter "
             + "the Started and Completed of one rewind across two streams");
  }

  /// <summary>The first argument of a constructor call, as written, with nesting respected.</summary>
  private static string _firstArgumentOf(string template, string ctor) => _argumentsOf(template, ctor)[0];

  private static string _secondArgumentOf(string template, string ctor) => _argumentsOf(template, ctor)[1];

  private static string[] _argumentsOf(string template, string ctor) {
    var open = template.IndexOf(ctor, StringComparison.Ordinal);
    if (open < 0) {
      return ["<not found: " + ctor + ">"];
    }
    var i = open + ctor.Length;
    var depth = 1;
    var args = new List<string>();
    var current = new System.Text.StringBuilder();
    while (i < template.Length && depth > 0) {
      var c = template[i];
      if (c is '(' or '[') {
        depth++;
      } else if (c is ')' or ']') {
        depth--;
        if (depth == 0) {
          break;
        }
      }
      if (c == ',' && depth == 1) {
        args.Add(current.ToString().Trim());
        current.Clear();
      } else {
        current.Append(c);
      }
      i++;
    }
    args.Add(current.ToString().Trim());
    return [.. args];
  }

  private static int _countOf(string template, string needle) {
    var count = 0;
    var i = template.IndexOf(needle, StringComparison.Ordinal);
    while (i >= 0) {
      count++;
      i = template.IndexOf(needle, i + needle.Length, StringComparison.Ordinal);
    }
    return count;
  }
  [Test]
  [RequiresAssemblyFiles]
  public async Task SaveModelAndCheckpoint_AsksTheWriteGuardBeforeItWritesAsync() {
    // #1677 regression lock: every row write goes through SaveModelAndCheckpointAsync, which asks
    // PerspectiveRowWriteGuard first, so a model no Apply produced (the scaffolded default) is never stored.
    var template = _loadTemplate();
    var method = template.IndexOf("private async Task<bool> SaveModelAndCheckpointAsync(", StringComparison.Ordinal);
    var guard = template.IndexOf("PerspectiveRowWriteGuard.AllowsWrite(producedByApply", StringComparison.Ordinal);
    var upsert = template.IndexOf("#region UPSERT_CALL", StringComparison.Ordinal);

    await Assert.That(method).IsGreaterThan(0);
    await Assert.That(guard).IsGreaterThan(method).Because("the guard is inside the write method");
    await Assert.That(guard).IsLessThan(upsert).Because("the guard runs before the upsert");
    var calls = template.Split("await SaveModelAndCheckpointAsync(").Length - 1;
    var provenance = template.Split("perspectiveName,\n                updatedModel,\n                hasWrittenUpdate,").Length - 1
      + template.Split("perspectiveName,\n              updatedModel,\n              hasWrittenUpdate,").Length - 1
      + template.Split("streamId, perspectiveName, updatedModel, producedByApply,").Length - 1;
    await Assert.That(provenance).IsEqualTo(calls)
      .Because("every write passes whether an Apply produced the model: the live, partial and rewind writes");
  }
}
