using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Tests for <see cref="EphemeralAnalyzer"/> — the WHIZ130-139 band that makes the ephemeral virality
/// rules safe-by-construction at build time. This file covers WHIZ130: a perspective whose Apply methods
/// span both modes (a viral ephemeral event AND a normal Sourced event) can't be both authoritative state
/// and a rebuildable cache — a Warning, backstopped by the runtime guard.
/// </summary>
/// <tests>Whizbang.Generators/EphemeralAnalyzer.cs</tests>
[Category("Analyzers")]
public class EphemeralAnalyzerTests {
  private const string HEADER = """
    using Whizbang.Core;
    using Whizbang.Core.Attributes;
    using Whizbang.Core.Perspectives;
    namespace TestApp;
    public class Model { public System.Guid Id { get; set; } }
    [Ephemeral]
    public record PresencePing : IEvent;
    public record OrderPlaced : IEvent;

    """;

  [Test]
  [RequiresAssemblyFiles]
  public async Task PerspectiveMixesEphemeralAndSourcedApply_ReportsWHIZ130WarningAsync() {
    const string source = HEADER + """
      public class MixedProjection
        : IPerspectiveFor<Model, PresencePing>, IPerspectiveFor<Model, OrderPlaced> {
        public Model Apply(Model current, PresencePing e) => current;
        public Model Apply(Model current, OrderPlaced e) => current;
      }
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<EphemeralAnalyzer>(source);

    var found = diagnostics.Where(d => d.Id == "WHIZ130").ToArray();
    await Assert.That(found.Length).IsEqualTo(1);
    await Assert.That(found[0].Severity).IsEqualTo(DiagnosticSeverity.Warning);
    await Assert.That(found[0].GetMessage(CultureInfo.InvariantCulture)).Contains("MixedProjection");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task PerspectiveAllEphemeralApply_NoWHIZ130Async() {
    const string source = HEADER + """
      [Ephemeral]
      public record PresenceGone : IEvent;
      public class PresenceProjection
        : IPerspectiveFor<Model, PresencePing>, IPerspectiveFor<Model, PresenceGone> {
        public Model Apply(Model current, PresencePing e) => current;
        public Model Apply(Model current, PresenceGone e) => current;
      }
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<EphemeralAnalyzer>(source);
    await Assert.That(diagnostics.Any(d => d.Id == "WHIZ130")).IsFalse();
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task PerspectiveAllSourcedApply_NoWHIZ130Async() {
    const string source = HEADER + """
      public record OrderShipped : IEvent;
      public class OrderProjection
        : IPerspectiveFor<Model, OrderPlaced>, IPerspectiveFor<Model, OrderShipped> {
        public Model Apply(Model current, OrderPlaced e) => current;
        public Model Apply(Model current, OrderShipped e) => current;
      }
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<EphemeralAnalyzer>(source);
    await Assert.That(diagnostics.Any(d => d.Id == "WHIZ130")).IsFalse();
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task MixedPerspectiveWithDangerousOptIn_NoWHIZ130Async() {
    // Explicit, in-code opt-in: the developer has acknowledged the dangerous path. The warning goes away
    // BECAUSE they decorated the perspective — a first-class escape hatch for teams that run
    // warnings-as-errors with no #pragma/editorconfig suppression, and a greppable, reviewable choice.
    const string source = HEADER + """
      [DangerouslyAllowMixedEphemeralAndSourcedEvents]
      public class DeliberatelyMixedProjection
        : IPerspectiveFor<Model, PresencePing>, IPerspectiveFor<Model, OrderPlaced> {
        public Model Apply(Model current, PresencePing e) => current;
        public Model Apply(Model current, OrderPlaced e) => current;
      }
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<EphemeralAnalyzer>(source);
    await Assert.That(diagnostics.Any(d => d.Id == "WHIZ130")).IsFalse();
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task MixViaComposedProfileInterface_StillReportsWHIZ130Async() {
    // The ephemeral event gets its mode from a profile interface, not a direct attribute — the analyzer
    // must resolve it the same way the generator does (walk base/interfaces), or virality goes unenforced.
    const string source = HEADER + """
      [Ephemeral(Destruction = Destruction.WhenConsumed, Storage = TransientStorage.TtlRow)]
      public interface ISessionSignal : IEvent { }
      public record TabOpened : ISessionSignal;
      public class SessionAndOrder
        : IPerspectiveFor<Model, TabOpened>, IPerspectiveFor<Model, OrderPlaced> {
        public Model Apply(Model current, TabOpened e) => current;
        public Model Apply(Model current, OrderPlaced e) => current;
      }
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<EphemeralAnalyzer>(source);
    await Assert.That(diagnostics.Count(d => d.Id == "WHIZ130")).IsEqualTo(1);
  }

  // ==================== WHIZ134: ambiguous composition ====================

  private const string PROFILES = """
    using Whizbang.Core;
    using Whizbang.Core.Attributes;
    namespace TestApp;
    [Ephemeral(Destruction = Destruction.WhenConsumed, Storage = TransientStorage.InMemory)]
    public interface IPresenceProfile : IEvent { }
    [Ephemeral(Destruction = Destruction.AfterTtl, Storage = TransientStorage.TtlRow)]
    public interface ISessionProfile : IEvent { }

    """;

  [Test]
  [RequiresAssemblyFiles]
  public async Task TwoConflictingProfiles_ReportsWHIZ134ErrorAsync() {
    // Two sibling profiles that disagree, with no more-specific override to break the tie — the resolver
    // would otherwise pick one silently; the developer must decide by declaring an explicit [Ephemeral].
    const string source = PROFILES + """
      public record Ambiguous : IPresenceProfile, ISessionProfile;
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<EphemeralAnalyzer>(source);
    var found = diagnostics.Where(d => d.Id == "WHIZ134").ToArray();
    await Assert.That(found.Length).IsEqualTo(1);
    await Assert.That(found[0].Severity).IsEqualTo(DiagnosticSeverity.Error);
    await Assert.That(found[0].GetMessage(CultureInfo.InvariantCulture)).Contains("Ambiguous");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task ConflictingProfilesWithOwnOverride_NoWHIZ134Async() {
    // The type's own [Ephemeral] resolves the tie unambiguously (most-specific wins).
    const string source = PROFILES + """
      [Ephemeral(Destruction = Destruction.WhenConsumed, Storage = TransientStorage.InMemory)]
      public record Resolved : IPresenceProfile, ISessionProfile;
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<EphemeralAnalyzer>(source);
    await Assert.That(diagnostics.Any(d => d.Id == "WHIZ134")).IsFalse();
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task TwoAgreeingProfiles_NoWHIZ134Async() {
    // Same config on both profiles — no ambiguity, nothing to resolve.
    const string source = """
      using Whizbang.Core;
      using Whizbang.Core.Attributes;
      namespace TestApp;
      [Ephemeral(Destruction = Destruction.WhenConsumed, Storage = TransientStorage.InMemory)]
      public interface IProfileA : IEvent { }
      [Ephemeral(Destruction = Destruction.WhenConsumed, Storage = TransientStorage.InMemory)]
      public interface IProfileB : IEvent { }
      public record Agree : IProfileA, IProfileB;
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<EphemeralAnalyzer>(source);
    await Assert.That(diagnostics.Any(d => d.Id == "WHIZ134")).IsFalse();
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task RefinedProfileInheritance_NoWHIZ134Async() {
    // IRefined extends IBase — IRefined is more specific, so its config wins; not a sibling conflict.
    const string source = """
      using Whizbang.Core;
      using Whizbang.Core.Attributes;
      namespace TestApp;
      [Ephemeral(Destruction = Destruction.WhenConsumed, Storage = TransientStorage.InMemory)]
      public interface IBaseProfile : IEvent { }
      [Ephemeral(Destruction = Destruction.AfterTtl, Storage = TransientStorage.TtlRow)]
      public interface IRefinedProfile : IBaseProfile { }
      public record Refined : IRefinedProfile;
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<EphemeralAnalyzer>(source);
    await Assert.That(diagnostics.Any(d => d.Id == "WHIZ134")).IsFalse();
  }
}
