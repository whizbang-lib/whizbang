using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Coverage for <see cref="StreamGroupAnalyzer"/> paths the existing
/// <c>StreamGroupAnalyzerTests</c> never exercise: a non-class symbol that otherwise looks like an
/// event-sharing perspective, a compilation with no stream groups at all, deterministic ordering of
/// multiple WHIZ140 sharer names, a <c>[StreamGroup("")]</c> empty key, and an evictor whose
/// <c>Announce</c> is explicitly turned off.
/// </summary>
/// <tests>Whizbang.Generators/StreamGroupAnalyzer.cs</tests>
[Category("Analyzers")]
public class StreamGroupAnalyzerCoverageTests {
  private const string HEADER = """
    using Whizbang.Core;
    using Whizbang.Core.Attributes;
    using Whizbang.Core.Perspectives;
    namespace TestApp;
    public class Model { public System.Guid Id { get; set; } }
    public record ThreadTouched : IEvent;
    public record ThreadClosed : IEvent;

    """;

  /// <summary>
  /// Verifies that a non-class named type (here, an interface) that inherits the same
  /// event-applying shape as a real perspective is excluded from drift detection entirely. If this
  /// guard regresses, a marker/facade interface that merely extends
  /// <c>IPerspectiveFor&lt;TModel, TEvent&gt;</c> would be misidentified as an actual ungrouped
  /// perspective implementation and spuriously flagged by WHIZ140 for "sharing" event types with a
  /// real grouped perspective.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task NonClassSymbolSharingEventType_ExcludedFromDriftDetectionAsync() {
    const string source = HEADER + """
      [StreamGroup("chat")]
      [RowTtl(Days = 60)]
      public class GroupedList : IPerspectiveFor<Model, ThreadTouched> {
        public Model Apply(Model current, ThreadTouched e) => current;
      }
      public interface ISharedThreadMarker : IPerspectiveFor<Model, ThreadTouched> { }
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<StreamGroupAnalyzer>(source);

    await Assert.That(diagnostics.Count(d => d.Id == "WHIZ140")).IsEqualTo(0)
      .Because("ISharedThreadMarker is an interface, not a perspective implementation, and must not be treated as an ungrouped sharer");
  }

  /// <summary>
  /// Verifies that a compilation with no <c>[StreamGroup]</c> membership anywhere produces no
  /// diagnostics. If this early-out regresses, every compilation with zero stream groups would still
  /// pay the cost of building the group-key and event-type indexes for no purpose.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task NoPerspectiveBelongsToAnyGroup_NoDiagnosticsAsync() {
    const string source = HEADER + """
      public class UngroupedOnly : IPerspectiveFor<Model, ThreadTouched> {
        public Model Apply(Model current, ThreadTouched e) => current;
      }
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<StreamGroupAnalyzer>(source);

    await Assert.That(diagnostics).IsEmpty().Because("no perspective anywhere joins a stream group, so there is nothing to evaluate");
  }

  /// <summary>
  /// Verifies that when an ungrouped perspective shares an event type with more than one grouped
  /// member, WHIZ140 lists the sharer names in deterministic ordinal order rather than whatever
  /// order the underlying concurrent collection happened to enumerate them. Without this, the
  /// message text would vary between runs/builds, breaking any diff- or snapshot-based tooling that
  /// reads it.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task MultipleUngroupedSharers_ListedInOrdinalOrderAsync() {
    const string source = HEADER + """
      [StreamGroup("chat")]
      [RowTtl(Days = 60)]
      public class ZetaMember : IPerspectiveFor<Model, ThreadTouched> {
        public Model Apply(Model current, ThreadTouched e) => current;
      }
      [StreamGroup("chat")]
      public class AlphaMember : IPerspectiveFor<Model, ThreadTouched> {
        public Model Apply(Model current, ThreadTouched e) => current;
      }
      public class UngroupedSharer : IPerspectiveFor<Model, ThreadTouched> {
        public Model Apply(Model current, ThreadTouched e) => current;
      }
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<StreamGroupAnalyzer>(source);

    var drift = diagnostics.Where(d => d.Id == "WHIZ140").ToArray();
    await Assert.That(drift.Length).IsEqualTo(1);
    await Assert.That(drift[0].GetMessage(CultureInfo.InvariantCulture)).Contains("AlphaMember, ZetaMember")
      .Because("sharer names must be sorted ordinally regardless of collection/declaration order");
  }

  /// <summary>
  /// Verifies that <c>[StreamGroup("")]</c> is treated as no membership at all, not as a real
  /// (oddly-named) group. Without this guard, an accidentally empty group key would form a real
  /// membership bucket, silently swallowing the perspective into an unnamed group instead of
  /// leaving it correctly detectable as ungrouped.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task StreamGroupWithEmptyKey_TreatedAsNoMembershipAsync() {
    const string source = HEADER + """
      [StreamGroup("chat")]
      [RowTtl(Days = 60)]
      public class GroupedList : IPerspectiveFor<Model, ThreadTouched> {
        public Model Apply(Model current, ThreadTouched e) => current;
      }
      [StreamGroup("")]
      public class EmptyKeyMember : IPerspectiveFor<Model, ThreadTouched> {
        public Model Apply(Model current, ThreadTouched e) => current;
      }
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<StreamGroupAnalyzer>(source);

    var drift = diagnostics.Where(d => d.Id == "WHIZ140").ToArray();
    await Assert.That(drift.Length).IsEqualTo(1)
      .Because("the empty-key attribute must not count as a real membership, leaving EmptyKeyMember detectably ungrouped");
    await Assert.That(drift[0].GetMessage(CultureInfo.InvariantCulture)).Contains("EmptyKeyMember");
  }

  /// <summary>
  /// Verifies that a member whose <c>[StreamGroup(..., Announce = false)]</c> does not count toward
  /// satisfying the group's "at least one announcing member has an evictor" requirement, even though
  /// it carries a real evictor. If this regresses, a silent evictor would be mistaken for the
  /// group's trigger, hiding a genuinely inert group (nothing this member evicts is ever announced
  /// to its own group).
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task AnnounceFalseWithEvictor_StillReportsInertGroupAsync() {
    const string source = HEADER + """
      [StreamGroup("chat", Announce = false)]
      [RowTtl(Days = 30)]
      public class SilentEvictor : IPerspectiveFor<Model, ThreadTouched> {
        public Model Apply(Model current, ThreadTouched e) => current;
      }
      [StreamGroup("chat")]
      public class Follower : IPerspectiveFor<Model, ThreadClosed> {
        public Model Apply(Model current, ThreadClosed e) => current;
      }
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<StreamGroupAnalyzer>(source);

    await Assert.That(diagnostics.Count(d => d.Id == "WHIZ141")).IsEqualTo(2)
      .Because("SilentEvictor's evictor is never announced to the group, so the group has no real trigger and both members are inert");
  }
}
