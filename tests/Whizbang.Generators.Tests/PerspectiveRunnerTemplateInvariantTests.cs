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
    // failure recovery — rare. INF firing per cursor-flush race produced ~8.6k/day of noise
    // on a consumer BFF; Debug is the right level for the post-step-7 invariant.
    await Assert.That(template).Contains("returning Completed for self-heal")
      .Because("the all-skipped branch's log message is the anchor we're pinning");
    var idx = template.IndexOf("returning Completed for self-heal", StringComparison.Ordinal);
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
  public async Task ApplyLoop_CallsThrowIfCancellationRequestedBetweenEventsAsync() {
    // Phase H step 9 slice 4 regression lock: the apply loop must check the lease CT between
    // events. Apply is pure synchronous so can't be cancelled mid-call, but per-event CT checks
    // let a hot stream with many pending events stop respecting the deadline.
    var template = _loadTemplate();

    await Assert.That(template).Contains("foreach (var envelope in events)")
      .Because("the apply loop is the anchor for the CT check");
    await Assert.That(template).Contains("cancellationToken.ThrowIfCancellationRequested()")
      .Because("the apply loop must call ThrowIfCancellationRequested at the top of each iteration");

    // Ensure the CT check appears INSIDE the events foreach (not just somewhere in the file).
    var foreachIdx = template.IndexOf("foreach (var envelope in events)", StringComparison.Ordinal);
    var ctCheckIdx = template.IndexOf("cancellationToken.ThrowIfCancellationRequested()", foreachIdx, StringComparison.Ordinal);
    await Assert.That(ctCheckIdx).IsGreaterThan(foreachIdx)
      .Because("the CT check must appear inside the foreach body, not before it");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task PartialSkipBranch_StaysAtDebugLevelAsync() {
    // The partial-skip branch was already at Debug per step 6 slice 6. Lock it so a future
    // refactor can't quietly bump it back to Information.
    var template = _loadTemplate();

    await Assert.That(template).Contains("cursor-flush lag, applying {Remaining} new events");
    var idx = template.IndexOf("cursor-flush lag, applying {Remaining} new events", StringComparison.Ordinal);
    await Assert.That(idx).IsGreaterThan(0);

    var prefix = template[..idx];
    var lastLogDebug = prefix.LastIndexOf("LogDebug", StringComparison.Ordinal);
    var lastLogInformation = prefix.LastIndexOf("LogInformation", StringComparison.Ordinal);

    await Assert.That(lastLogDebug).IsGreaterThan(lastLogInformation)
      .Because("partial-skip is benign cursor-flush race; must stay at Debug");
  }
}
