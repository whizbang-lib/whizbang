using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Tests for <see cref="StreamGroupAnalyzer"/> — WHIZ140 (drift: an ungrouped perspective sharing
/// stream event types with grouped members, silenced by [StreamGroupIsolated]), WHIZ141 (a group
/// with no announcing evictor is inert), WHIZ142 (Bridge on a sole membership crosses into
/// nothing). Groups fail by SILENCE; each check makes the silence loud at build time.
/// </summary>
/// <tests>Whizbang.Generators/StreamGroupAnalyzer.cs</tests>
[Category("Analyzers")]
public class StreamGroupAnalyzerTests {
  private const string HEADER = """
    using Whizbang.Core;
    using Whizbang.Core.Attributes;
    using Whizbang.Core.Perspectives;
    namespace TestApp;
    public class Model { public System.Guid Id { get; set; } }
    public record ThreadTouched : IEvent;
    public record ThreadClosed : IEvent;

    """;

  [Test]
  [RequiresAssemblyFiles]
  public async Task BridgeOnSoleMembership_ReportsWHIZ142Async() {
    const string source = HEADER + """
      [StreamGroup("chat", Bridge = true)]
      public class LonelyBridge : IPerspectiveFor<Model, ThreadTouched> {
        public Model Apply(Model current, ThreadTouched e) => current;
      }
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<StreamGroupAnalyzer>(source);

    var found = diagnostics.Where(d => d.Id == "WHIZ142").ToArray();
    await Assert.That(found.Length).IsEqualTo(1);
    await Assert.That(found[0].GetMessage(CultureInfo.InvariantCulture)).Contains("LonelyBridge");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task BridgeWithTwoMemberships_NoWHIZ142Async() {
    const string source = HEADER + """
      [StreamGroup("chat")]
      [StreamGroup("audit", Bridge = true)]
      [RowTtl(Days = 30)]
      public class DualMember : IPerspectiveFor<Model, ThreadTouched> {
        public Model Apply(Model current, ThreadTouched e) => current;
      }
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<StreamGroupAnalyzer>(source);

    await Assert.That(diagnostics.Count(d => d.Id == "WHIZ142")).IsEqualTo(0)
      .Because("with two memberships a bridge has somewhere to cross into — that is its purpose");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task GroupWithNoAnnouncingEvictor_ReportsWHIZ141OnEachMemberAsync() {
    const string source = HEADER + """
      [StreamGroup("chat")]
      public class ListSide : IPerspectiveFor<Model, ThreadTouched> {
        public Model Apply(Model current, ThreadTouched e) => current;
      }
      [StreamGroup("chat")]
      public class MachinerySide : IPerspectiveFor<Model, ThreadClosed> {
        public Model Apply(Model current, ThreadClosed e) => current;
      }
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<StreamGroupAnalyzer>(source);

    var found = diagnostics.Where(d => d.Id == "WHIZ141").ToArray();
    await Assert.That(found.Length).IsEqualTo(2)
      .Because("no member carries [RowTtl]/[RowCap], so nothing ever triggers the cascade — the group is inert");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task GroupWithAnAnnouncingEvictor_NoWHIZ141Async() {
    const string source = HEADER + """
      [StreamGroup("chat")]
      [RowTtl(Days = 60)]
      public class EvictingLeader : IPerspectiveFor<Model, ThreadTouched> {
        public Model Apply(Model current, ThreadTouched e) => current;
      }
      [StreamGroup("chat")]
      public class Follower : IPerspectiveFor<Model, ThreadClosed> {
        public Model Apply(Model current, ThreadClosed e) => current;
      }
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<StreamGroupAnalyzer>(source);

    await Assert.That(diagnostics.Count(d => d.Id == "WHIZ141")).IsEqualTo(0);
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task UngroupedSharer_ReportsWHIZ140_IsolationMarkerSilencesItAsync() {
    const string source = HEADER + """
      [StreamGroup("chat")]
      [RowTtl(Days = 60)]
      public class GroupedList : IPerspectiveFor<Model, ThreadTouched> {
        public Model Apply(Model current, ThreadTouched e) => current;
      }
      public class ForgottenSibling : IPerspectiveFor<Model, ThreadTouched> {
        public Model Apply(Model current, ThreadTouched e) => current;
      }
      [StreamGroupIsolated]
      public class DeliberateKeeper : IPerspectiveFor<Model, ThreadTouched> {
        public Model Apply(Model current, ThreadTouched e) => current;
      }
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<StreamGroupAnalyzer>(source);

    var drift = diagnostics.Where(d => d.Id == "WHIZ140").ToArray();
    await Assert.That(drift.Length).IsEqualTo(1)
      .Because("the forgotten sibling shares the stream's event types but joined nothing — its rows "
             + "will linger after the group evicts; the deliberate keeper stated its choice");
    await Assert.That(drift[0].GetMessage(CultureInfo.InvariantCulture)).Contains("ForgottenSibling");
  }
}
