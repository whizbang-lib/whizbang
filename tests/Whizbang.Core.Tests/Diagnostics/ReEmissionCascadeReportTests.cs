using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Diagnostics;

namespace Whizbang.Core.Tests.Diagnostics;

/// <summary>
/// A service that both consumes and raises the same event type multiplies traffic on every hop, and
/// nothing reports it.
/// </summary>
/// <remarks>
/// <para>
/// When a consumer's aggregates re-raise event types they subscribe to, each hop stores the event
/// into its own event store, that store publishes onward, and the next consumer does the same. The
/// multiplier compounds rather than adds.
/// </para>
/// <para>
/// Measured on one event type in a bulk-import workload: a producer emitted 4,035 events; four
/// consumers subsequently held 100,427, 91,933, 48,443 and 40,001 rows of that type — 280,804 in
/// total, roughly seventy times the source. Handler count was ONE on every consumer, so this was
/// neither multiple handlers nor duplicate delivery of a single publish. It was the cascade.
/// </para>
/// <para>
/// Every throughput control the framework offers — claim windows, outstanding budgets, publish
/// batching, concurrency governors — acts on the volume AFTER this multiplication, so none of them
/// can reduce it. An operator tuning them has no way to learn the load itself is the problem.
/// </para>
/// <para>
/// The intersection of subscribed types and raised types is known at wire-up and costs nothing to
/// compute. Reporting it makes the cascade a design decision rather than something discovered by
/// hand-joining event stores during an incident.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Diagnostics/ReEmissionCascadeReport.cs</code-under-test>
[Category("Diagnostics")]
public class ReEmissionCascadeReportTests {

  private static readonly string[] _bothDraftTypes =
    ["DraftJobCompetencyRowAdded", "DraftJobSkillRowAdded"];
  private static readonly string[] _justB = ["B"];
  private static readonly string[] _alphaMikeZebra = ["Alpha", "Mike", "Zebra"];

  [Test]
  public async Task AServiceThatOnlyProjectsIsSilentAsync() {
    var report = ReEmissionCascadeReport.Analyze(
      subscribedTypes: ["A", "B", "C"],
      raisedTypes: ["ProjectionUpdated"]);

    await Assert.That(report.HasCascade).IsFalse();
    await Assert.That(report.ReEmittedTypes.Count).IsEqualTo(0)
      .Because("consuming events and raising unrelated ones is ordinary — flagging it would make "
             + "the signal worthless on every service that does normal work");
  }

  [Test]
  public async Task AServiceThatRaisesWhatItConsumesIsReportedAsync() {
    var report = ReEmissionCascadeReport.Analyze(
      subscribedTypes: ["DraftJobCompetencyRowAdded", "DraftJobSkillRowAdded", "Unrelated"],
      raisedTypes: ["DraftJobCompetencyRowAdded", "DraftJobSkillRowAdded", "SomethingElse"]);

    await Assert.That(report.HasCascade).IsTrue();
    await Assert.That(report.ReEmittedTypes).IsEquivalentTo(_bothDraftTypes)
      .Because("exactly the types on BOTH sides are the ones that compound per hop; naming them "
             + "turns an unexplained load into a specific design decision");
  }

  [Test]
  public async Task OnlyTheIntersectionIsReportedAsync() {
    var report = ReEmissionCascadeReport.Analyze(
      subscribedTypes: ["A", "B"],
      raisedTypes: ["B", "C", "D"]);

    await Assert.That(report.ReEmittedTypes).IsEquivalentTo(_justB)
      .Because("types raised but not consumed are ordinary output, and types consumed but not "
             + "raised are ordinary input — neither compounds, and reporting them would bury the "
             + "one that does");
  }

  [Test]
  public async Task TheReportIsOrderedSoTheLogLineIsStableAsync() {
    var report = ReEmissionCascadeReport.Analyze(
      subscribedTypes: ["Zebra", "Alpha", "Mike"],
      raisedTypes: ["Mike", "Zebra", "Alpha"]);

    await Assert.That(report.ReEmittedTypes.ToArray()).IsEquivalentTo(_alphaMikeZebra)
      .Because("an unstable order makes the startup line differ between identical deployments, "
             + "which defeats diffing it across services to find which one introduced a cascade");
  }

  [Test]
  public async Task DuplicatesInTheInputDoNotDuplicateTheFindingAsync() {
    var report = ReEmissionCascadeReport.Analyze(
      subscribedTypes: ["A", "A", "B"],
      raisedTypes: ["A", "A", "A"]);

    await Assert.That(report.ReEmittedTypes.Count).IsEqualTo(1)
      .Because("a type registered twice is a registration detail, not two cascades — counting it "
             + "twice would overstate the finding and erode trust in the number");
  }

  [Test]
  public async Task EmptyInputsAreSafeAsync() {
    var a = ReEmissionCascadeReport.Analyze([], ["X"]);
    var b = ReEmissionCascadeReport.Analyze(["X"], []);

    await Assert.That(a.HasCascade).IsFalse();
    await Assert.That(b.HasCascade).IsFalse()
      .Because("a service that subscribes to nothing, or raises nothing, cannot cascade — and a "
             + "diagnostic that throws at wire-up would take down every host that reaches it");
  }

  [Test]
  public async Task NullInputsAreRejectedRatherThanTreatedAsEmptyAsync() {
    await Assert.That(() => ReEmissionCascadeReport.Analyze(null!, ["X"]))
      .Throws<ArgumentNullException>();
    await Assert.That(() => ReEmissionCascadeReport.Analyze(["X"], null!))
      .Throws<ArgumentNullException>()
      .Because("a null registry silently reporting 'no cascade' is the worst outcome — it is the "
             + "same answer a healthy service gives, so the failure would never be noticed");
  }
}
