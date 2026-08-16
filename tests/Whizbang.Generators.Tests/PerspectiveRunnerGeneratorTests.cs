using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Tests for the PerspectiveRunnerGenerator source generator.
/// Ensures correct perspective runner generation for perspectives with IPerspectiveModel.
/// </summary>
public class PerspectiveRunnerGeneratorTests {

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_EmptyCompilation_GeneratesNothingAsync() {
    // Arrange
    const string source = @"
using System;

namespace TestNamespace {
  public class SomeClass {
    public void SomeMethod() { }
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);

    // Assert - Should not generate any runner files when no perspectives with models exist
    await Assert.That(result.GeneratedTrees).Count().IsEqualTo(0);
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_PerspectiveWithoutModel_GeneratesNothingAsync() {
    // Arrange - Perspective without IPerspectiveModel should not generate runner
    const string source = """

using Whizbang.Core;
using System.Threading;
using System.Threading.Tasks;

namespace TestNamespace {
  public record OrderCreatedEvent : IEvent {
    public string OrderId { get; init; } = "";
  }

  public class OrderPerspective {
    public Task Update(OrderCreatedEvent @event, CancellationToken cancellationToken = default) {
      return Task.CompletedTask;
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);

    // Assert - No runner should be generated (perspective doesn't implement IPerspectiveModel)
    await Assert.That(result.GeneratedTrees).Count().IsEqualTo(0);
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_PerspectiveWithModel_GeneratesRunnerAsync() {
    // Arrange - Perspective with IPerspectiveModel<TModel> should generate runner
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace TestNamespace {
  public record OrderCreatedEvent : IEvent {
    public string OrderId { get; init; } = "";
  }

  public record OrderReadModel {
    [StreamId]
    public string OrderId { get; init; } = "";
    public string Status { get; init; } = "";
  }

  public class OrderPerspective : IPerspectiveFor<OrderReadModel, OrderCreatedEvent> {
    public OrderReadModel Apply(OrderReadModel currentData, OrderCreatedEvent @event) {
      return currentData with { Status = "Created" };
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);

    // Assert - Should generate OrderPerspectiveRunner
    await Assert.That(result.GeneratedTrees).Count().IsEqualTo(1);

    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "OrderPerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();
    await Assert.That(runnerSource).Contains("class OrderPerspectiveRunner");
    await Assert.That(runnerSource).Contains("IPerspectiveRunner");
    await Assert.That(runnerSource).Contains("OrderPerspective");
    await Assert.That(runnerSource).Contains("OrderReadModel");
    await Assert.That(runnerSource).Contains("OrderId");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_EphemeralPerspective_UsesAggressiveSnapshotSettingsAsync() {
    // A perspective that applies an [Ephemeral] event is ephemeral-tainted → snapshots on the aggressive,
    // single-slot ephemeral cadence so a fresh rewind floor exists within the grace window.
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Perspectives;

namespace TestNamespace {
  [Ephemeral(Destruction = Destruction.WhenConsumed, Storage = TransientStorage.InMemory)]
  public record UserIsTyping : IEvent {
    public string ConversationId { get; init; } = "";
  }

  public record PresenceModel {
    [StreamId]
    public string ConversationId { get; init; } = "";
  }

  public class PresencePerspective : IPerspectiveFor<PresenceModel, UserIsTyping> {
    public PresenceModel Apply(PresenceModel currentData, UserIsTyping @event) => currentData;
  }
}
""";

    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);
    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "PresencePerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();
    await Assert.That(runnerSource!).Contains("EphemeralSnapshotEveryNEvents")
      .Because("An ephemeral perspective snapshots on the aggressive ephemeral cadence.");
    await Assert.That(runnerSource!).Contains("EphemeralMaxSnapshotsPerStream")
      .Because("An ephemeral perspective prunes to the single-slot ephemeral retention.");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_EphemeralPerspective_GuardsRewindFallbackAgainstReapedBodiesAsync() {
    // E1 (3): an ephemeral perspective's runner must carry _isEphemeralPerspective = true so the
    // rewind guard fires — an out-of-grace straggler with no snapshot floor is SKIPPED instead of
    // replayed from zero over reaped (NULL) bodies, which would corrupt the authoritative model.
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Perspectives;

namespace TestNamespace {
  [Ephemeral(Destruction = Destruction.WhenConsumed, Storage = TransientStorage.InMemory)]
  public record UserIsTyping : IEvent {
    public string ConversationId { get; init; } = "";
  }

  public record PresenceModel {
    [StreamId]
    public string ConversationId { get; init; } = "";
  }

  public class PresencePerspective : IPerspectiveFor<PresenceModel, UserIsTyping> {
    public PresenceModel Apply(PresenceModel currentData, UserIsTyping @event) => currentData;
  }
}
""";

    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);
    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "PresencePerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();
    await Assert.That(runnerSource!).Contains("private const bool _isEphemeralPerspective = true;")
      .Because("An ephemeral perspective marks itself ephemeral so the rewind fallback guard fires.");
    await Assert.That(runnerSource!).Contains("_isEphemeralPerspective && !hasSnapshot")
      .Because("The rewind fallback is guarded so an ephemeral stream never replays from zero over reaped bodies.");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_TtlRowPerspective_RegistersRowTtlAsync() {
    // E2-4d: a perspective whose [Ephemeral] events chose TransientStorage.TtlRow emits a [ModuleInitializer]
    // registering its row TTL (max across its TtlRow events) so the upsert stamps expires_at.
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Perspectives;

namespace TestNamespace {
  [Ephemeral(Destruction = Destruction.AfterTtl, Storage = TransientStorage.TtlRow, TtlSeconds = 7776000)]
  public record ChatMessage : IEvent {
    public string ThreadId { get; init; } = "";
  }

  public record ThreadModel {
    [StreamId]
    public string ThreadId { get; init; } = "";
  }

  public class ThreadPerspective : IPerspectiveFor<ThreadModel, ChatMessage> {
    public ThreadModel Apply(ThreadModel currentData, ChatMessage @event) => currentData;
  }
}
""";

    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);
    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "ThreadPerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();
    await Assert.That(runnerSource!).Contains("[global::System.Runtime.CompilerServices.ModuleInitializer]")
      .Because("A TtlRow perspective registers its row TTL via a module initializer.");
    await Assert.That(runnerSource!).Contains("PerspectiveTtlRegistry.Register(typeof(global::TestNamespace.ThreadModel), 7776000)")
      .Because("The registration carries the perspective's model type and its resolved row TTL in seconds.");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_NonTtlRowPerspective_DoesNotRegisterRowTtlAsync() {
    // A WhenConsumed/InMemory ephemeral perspective is NOT TtlRow — its rows never expire, so no registration.
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Perspectives;

namespace TestNamespace {
  [Ephemeral(Destruction = Destruction.WhenConsumed, Storage = TransientStorage.InMemory)]
  public record UserIsTyping : IEvent {
    public string ConversationId { get; init; } = "";
  }

  public record PresenceModel {
    [StreamId]
    public string ConversationId { get; init; } = "";
  }

  public class PresencePerspective : IPerspectiveFor<PresenceModel, UserIsTyping> {
    public PresenceModel Apply(PresenceModel currentData, UserIsTyping @event) => currentData;
  }
}
""";

    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);
    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "PresencePerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();
    await Assert.That(runnerSource!).DoesNotContain("PerspectiveTtlRegistry.Register")
      .Because("A non-TtlRow perspective's rows never expire, so no TTL is registered.");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_RowTtlOnSourcedPerspective_RegistersRowTtlAsync() {
    // Perspective-row-retention increment 2: [RowTtl] declares a row TTL directly on the
    // perspective class — no [Ephemeral] events required. Row lifecycle is a read-model
    // property; a Sourced perspective's rows can age out while its event log stays durable
    // (safe since the event-time anchor makes rebuild deterministic and the sourced log can
    // re-fold a reaped row on wake).
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Perspectives;

namespace TestNamespace {
  public record ThreadArchived : IEvent {
    public string ThreadId { get; init; } = "";
  }

  public record SourcedThreadModel {
    [StreamId]
    public string ThreadId { get; init; } = "";
  }

  [RowTtl(Days = 60)]
  public class SourcedThreadPerspective : IPerspectiveFor<SourcedThreadModel, ThreadArchived> {
    public SourcedThreadModel Apply(SourcedThreadModel currentData, ThreadArchived @event) => currentData;
  }
}
""";

    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);
    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "SourcedThreadPerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();
    await Assert.That(runnerSource!).Contains("PerspectiveTtlRegistry.Register(typeof(global::TestNamespace.SourcedThreadModel), 5184000)")
      .Because("[RowTtl(Days = 60)] registers 60 days in seconds with no ephemeral involvement.");
  }

  [Test]
  public async Task PerspectiveRunnerGenerator_StreamGroup_RegistersEachMembershipWithItsDialsAsync() {
    // Stream groups follow the same turnkey chain as the TTL and the cap: attribute -> generated
    // [ModuleInitializer] -> registry. A perspective in TWO groups (the case the dials exist for)
    // must register two memberships, each with its own Announce/Follow/Bridge values.
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Perspectives;

namespace TestNamespace {
  public record ThreadTouched : IEvent {
    public string ThreadId { get; init; } = "";
  }

  public record GroupedThreadModel {
    [StreamId]
    public string ThreadId { get; init; } = "";
  }

  [StreamGroup("chat")]
  [StreamGroup("audit", Follow = false, Bridge = true)]
  public class GroupedThreadPerspective : IPerspectiveFor<GroupedThreadModel, ThreadTouched> {
    public GroupedThreadModel Apply(GroupedThreadModel currentData, ThreadTouched @event) => currentData;
  }
}
""";

    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);
    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "GroupedThreadPerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();
    await Assert.That(runnerSource!)
      .Contains("PerspectiveStreamGroupRegistry.Register(typeof(global::TestNamespace.GroupedThreadModel), \"chat\", true, true, false)")
      .Because("the first membership keeps the defaults: announce on, follow on, bridge OFF");
    await Assert.That(runnerSource)
      .Contains("PerspectiveStreamGroupRegistry.Register(typeof(global::TestNamespace.GroupedThreadModel), \"audit\", true, false, true)")
      .Because("the second membership carries its own dials — per-MEMBERSHIP, not per-perspective");
  }

  [Test]
  public async Task PerspectiveRunnerGenerator_NoStreamGroup_EmitsNoRegistrationAsync() {
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;

namespace TestNamespace {
  public record LoneEvent : IEvent {
    public string Id { get; init; } = "";
  }

  public record LoneModel {
    [StreamId]
    public string Id { get; init; } = "";
  }

  public class LonePerspective : IPerspectiveFor<LoneModel, LoneEvent> {
    public LoneModel Apply(LoneModel currentData, LoneEvent @event) => currentData;
  }
}
""";

    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);
    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "LonePerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();
    await Assert.That(runnerSource!).DoesNotContain("PerspectiveStreamGroupRegistry")
      .Because("an ungrouped perspective registers nothing — it must stay untouchable by cascades");
  }

  [Test]
  public async Task PerspectiveRunnerGenerator_RowCap_RegistersCapAsync() {
    // The cardinality half. A cap must reach PerspectiveRowCapRegistry the same turnkey way the TTL
    // reaches PerspectiveTtlRegistry — a generated [ModuleInitializer], no consumer code, no
    // reflection. Without this the attribute compiles, the registry stays empty, the startup
    // reconciler syncs a null cap, and the SQL reaper (which is itself correct and tested) is simply
    // never told about the declaration. That is the exact shape the feature shipped in.
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Perspectives;

namespace TestNamespace {
  public record ChatArchived : IEvent {
    public string ChatId { get; init; } = "";
  }

  public record CappedChatModel {
    [StreamId]
    public string ChatId { get; init; } = "";
  }

  [RowCap(PerScope = 200)]
  public class CappedChatPerspective : IPerspectiveFor<CappedChatModel, ChatArchived> {
    public CappedChatModel Apply(CappedChatModel currentData, ChatArchived @event) => currentData;
  }
}
""";

    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);
    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "CappedChatPerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();
    await Assert.That(runnerSource!)
      .Contains("PerspectiveRowCapRegistry.Register(typeof(global::TestNamespace.CappedChatModel), 200, \"u\")")
      .Because("a declared cap must register itself, partitioned per (tenant, user) — a cap nothing "
        + "registers is a declaration the reaper never sees");
  }

  [Test]
  public async Task PerspectiveRunnerGenerator_RowCapPerTenant_RegistersTenantScopeKeyAsync() {
    // PerTenant ranks across the whole tenant rather than per user. The scope key is what the SQL
    // sweep partitions its ROW_NUMBER() by, so getting it wrong silently changes who evicts whom.
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Perspectives;

namespace TestNamespace {
  public record RunFinished : IEvent {
    public string RunId { get; init; } = "";
  }

  public record TenantRunModel {
    [StreamId]
    public string RunId { get; init; } = "";
  }

  [RowCap(PerTenant = 50)]
  public class TenantRunPerspective : IPerspectiveFor<TenantRunModel, RunFinished> {
    public TenantRunModel Apply(TenantRunModel currentData, RunFinished @event) => currentData;
  }
}
""";

    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);
    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "TenantRunPerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();
    await Assert.That(runnerSource!)
      .Contains("PerspectiveRowCapRegistry.Register(typeof(global::TestNamespace.TenantRunModel), 50, \"t\")");
  }

  [Test]
  public async Task PerspectiveRunnerGenerator_NoRowCap_RegistersNoCapAsync() {
    // Absent must stay distinct from a cap of zero, which would evict everything.
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Perspectives;

namespace TestNamespace {
  public record PlainHappened : IEvent {
    public string Id { get; init; } = "";
  }

  public record PlainModel {
    [StreamId]
    public string Id { get; init; } = "";
  }

  public class PlainPerspective : IPerspectiveFor<PlainModel, PlainHappened> {
    public PlainModel Apply(PlainModel currentData, PlainHappened @event) => currentData;
  }
}
""";

    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);
    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "PlainPerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();
    await Assert.That(runnerSource!).DoesNotContain("PerspectiveRowCapRegistry.Register")
      .Because("an undeclared cap must emit nothing at all — registering 0 or -1 would be a cap "
        + "meaning 'evict everything' or a lie the reconciler then syncs");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_RowTtl_OverridesEphemeralDerivedTtlAsync() {
    // Ladder precedence: an explicit [RowTtl] on the perspective wins over the TTL derived
    // virally from its [Ephemeral(TtlRow)] events (most-specific wins — the read model's own
    // declaration outranks what its events imply).
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Perspectives;

namespace TestNamespace {
  [Ephemeral(Destruction = Destruction.AfterTtl, Storage = TransientStorage.TtlRow, TtlSeconds = 7776000)]
  public record ChatMessage : IEvent {
    public string ThreadId { get; init; } = "";
  }

  public record OverriddenThreadModel {
    [StreamId]
    public string ThreadId { get; init; } = "";
  }

  [RowTtl(Seconds = 42)]
  public class OverriddenThreadPerspective : IPerspectiveFor<OverriddenThreadModel, ChatMessage> {
    public OverriddenThreadModel Apply(OverriddenThreadModel currentData, ChatMessage @event) => currentData;
  }
}
""";

    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);
    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "OverriddenThreadPerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();
    await Assert.That(runnerSource!).Contains("PerspectiveTtlRegistry.Register(typeof(global::TestNamespace.OverriddenThreadModel), 42)")
      .Because("the explicit [RowTtl] outranks the ephemeral-derived TTL on the override ladder.");
    await Assert.That(runnerSource!).DoesNotContain(", 7776000)")
      .Because("the derived value must not leak through when an explicit declaration exists.");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_FullHistoryPerspective_RegistersNameAsync() {
    // A1-6b: a [FullHistory] perspective emits a [ModuleInitializer] registering its name so the close guard
    // refuses a discard-close of any stream it consumes.
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Perspectives;

namespace TestNamespace {
  public record LedgerEntry : IEvent {
    public string AccountId { get; init; } = "";
  }

  public record LedgerListModel {
    [StreamId]
    public string AccountId { get; init; } = "";
  }

  [FullHistory]
  public class LedgerListPerspective : IPerspectiveFor<LedgerListModel, LedgerEntry> {
    public LedgerListModel Apply(LedgerListModel currentData, LedgerEntry @event) => currentData;
  }
}
""";

    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);
    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "LedgerListPerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();
    await Assert.That(runnerSource!).Contains("FullHistoryPerspectiveRegistry.Register(")
      .Because("A [FullHistory] perspective registers its name via a module initializer for the A1 close guard.");
    await Assert.That(runnerSource!).Contains("LedgerListPerspective")
      .Because("The registration carries the perspective's name (its association target_name).");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_ResumablePerspective_DoesNotRegisterFullHistoryAsync() {
    // An unmarked perspective is resumable (rebuilds from the closing event forward) — no registration.
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Perspectives;

namespace TestNamespace {
  public record BalanceChanged : IEvent {
    public string AccountId { get; init; } = "";
  }

  public record BalanceModel {
    [StreamId]
    public string AccountId { get; init; } = "";
  }

  public class BalancePerspective : IPerspectiveFor<BalanceModel, BalanceChanged> {
    public BalanceModel Apply(BalanceModel currentData, BalanceChanged @event) => currentData;
  }
}
""";

    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);
    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "BalancePerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();
    await Assert.That(runnerSource!).DoesNotContain("FullHistoryPerspectiveRegistry.Register")
      .Because("A resumable (unmarked) perspective needs no full-history guard registration.");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_SourcedPerspective_UsesStandardSnapshotSettingsAsync() {
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;

namespace TestNamespace {
  public record OrderCreatedEvent : IEvent {
    public string OrderId { get; init; } = "";
  }

  public record OrderReadModel {
    [StreamId]
    public string OrderId { get; init; } = "";
  }

  public class OrderPerspective : IPerspectiveFor<OrderReadModel, OrderCreatedEvent> {
    public OrderReadModel Apply(OrderReadModel currentData, OrderCreatedEvent @event) => currentData;
  }
}
""";

    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);
    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "OrderPerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();
    await Assert.That(runnerSource!).Contains("_snapshotOptions.Value.SnapshotEveryNEvents")
      .Because("A Sourced perspective uses the standard snapshot cadence.");
    await Assert.That(runnerSource!).DoesNotContain("EphemeralSnapshotEveryNEvents")
      .Because("A Sourced perspective does not use the ephemeral cadence.");
    await Assert.That(runnerSource!).Contains("private const bool _isEphemeralPerspective = false;")
      .Because("A Sourced perspective is not ephemeral, so the rewind fallback guard stays inert and it always replays.");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_PerspectiveWithModelNoStreamId_GeneratesNothingAsync() {
    // Arrange - Model without [StreamId] attribute should not generate runner
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace TestNamespace {
  public record OrderCreatedEvent : IEvent {
    public string OrderId { get; init; } = "";
  }

  public record OrderReadModel {
    // Missing [StreamId] attribute
    public string OrderId { get; init; } = "";
    public string Status { get; init; } = "";
  }

  public class OrderPerspective : IPerspectiveFor<OrderReadModel, OrderCreatedEvent> {
    public OrderReadModel Apply(OrderReadModel currentData, OrderCreatedEvent @event) {
      return currentData with { Status = "Created" };
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);

    // Assert - Should not generate runner (model missing [StreamId])
    await Assert.That(result.GeneratedTrees).Count().IsEqualTo(0);
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_AbstractPerspective_IsIgnoredAsync() {
    // Arrange - Abstract perspectives should not generate runners
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace TestNamespace {
  public record OrderEvent : IEvent {
    public string OrderId { get; init; } = "";
  }

  public record OrderReadModel {
    [StreamId]
    public string OrderId { get; init; } = "";
  }

  public abstract class BasePerspective : IPerspectiveFor<OrderReadModel, OrderEvent> {
    public abstract OrderReadModel Apply(OrderReadModel currentData, OrderEvent @event);
  }

  public class ConcretePerspective : BasePerspective {
    public override OrderReadModel Apply(OrderReadModel currentData, OrderEvent @event) {
      return currentData;
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);

    // Assert - Should only generate runner for concrete class
    await Assert.That(result.GeneratedTrees).Count().IsEqualTo(1);

    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "ConcretePerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();
    await Assert.That(runnerSource).Contains("ConcretePerspectiveRunner");
    await Assert.That(runnerSource).DoesNotContain("BasePerspectiveRunner");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_GeneratesDiagnosticAsync() {
    // Arrange
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace TestNamespace {
  public record OrderEvent : IEvent {
    public string OrderId { get; init; } = "";
  }

  public record OrderReadModel {
    [StreamId]
    public string OrderId { get; init; } = "";
  }

  public class OrderPerspective : IPerspectiveFor<OrderReadModel, OrderEvent> {
    public OrderReadModel Apply(OrderReadModel currentData, OrderEvent @event) {
      return currentData;
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);

    // Assert - Should report WHIZ027 diagnostic
    var diagnostics = result.Diagnostics;
    var whiz027 = diagnostics.FirstOrDefault(d => d.Id == "WHIZ027");
    await Assert.That(whiz027).IsNotNull();
    await Assert.That(whiz027!.Severity).IsEqualTo(DiagnosticSeverity.Info);
    await Assert.That(whiz027.GetMessage(CultureInfo.InvariantCulture)).Contains("OrderPerspective");
    await Assert.That(whiz027.GetMessage(CultureInfo.InvariantCulture)).Contains("OrderPerspectiveRunner");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_UsesFullyQualifiedTypeNamesAsync() {
    // Arrange
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace TestNamespace {
  public record OrderEvent : IEvent {
    public string OrderId { get; init; } = "";
  }

  public record OrderReadModel {
    [StreamId]
    public string OrderId { get; init; } = "";
  }

  public class OrderPerspective : IPerspectiveFor<OrderReadModel, OrderEvent> {
    public OrderReadModel Apply(OrderReadModel currentData, OrderEvent @event) {
      return currentData;
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);

    // Assert - Should use global:: qualified names
    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "OrderPerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();
    await Assert.That(runnerSource).Contains("global::TestNamespace.OrderPerspective");
    await Assert.That(runnerSource).Contains("global::TestNamespace.OrderReadModel");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_CorrectRunnerNameAsync() {
    // Arrange - Test runner naming convention
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace TestNamespace {
  public record InventoryEvent : IEvent {
    public string InventoryId { get; init; } = "";
  }

  public record InventoryModel {
    [StreamId]
    public string InventoryId { get; init; } = "";
    public int Quantity { get; init; }
  }

  public class InventoryPerspective : IPerspectiveFor<InventoryModel, InventoryEvent> {
    public InventoryModel Apply(InventoryModel currentData, InventoryEvent @event) {
      return currentData;
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);

    // Assert - Runner name should be InventoryPerspectiveRunner
    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "InventoryPerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();
    await Assert.That(runnerSource).Contains("class InventoryPerspectiveRunner");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_ImplementsIPerspectiveRunnerAsync() {
    // Arrange
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace TestNamespace {
  public record OrderEvent : IEvent {
    public string OrderId { get; init; } = "";
  }

  public record OrderReadModel {
    [StreamId]
    public string OrderId { get; init; } = "";
  }

  public class OrderPerspective : IPerspectiveFor<OrderReadModel, OrderEvent> {
    public OrderReadModel Apply(OrderReadModel currentData, OrderEvent @event) {
      return currentData;
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);

    // Assert - Should implement IPerspectiveRunner interface
    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "OrderPerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();
    await Assert.That(runnerSource).Contains("IPerspectiveRunner");
    await Assert.That(runnerSource).Contains("RunAsync");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_MultiplePerspectives_GeneratesMultipleRunnersAsync() {
    // Arrange - Multiple perspectives with models should generate multiple runners
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace TestNamespace {
  public record OrderEvent : IEvent {
    public string OrderId { get; init; } = "";
  }

  public record InventoryEvent : IEvent {
    public string InventoryId { get; init; } = "";
  }

  public record OrderModel {
    [StreamId]
    public string OrderId { get; init; } = "";
  }

  public record InventoryModel {
    [StreamId]
    public string InventoryId { get; init; } = "";
  }

  public class OrderPerspective : IPerspectiveFor<OrderModel, OrderEvent> {
    public OrderModel Apply(OrderModel currentData, OrderEvent @event) {
      return currentData;
    }
  }

  public class InventoryPerspective : IPerspectiveFor<InventoryModel, InventoryEvent> {
    public InventoryModel Apply(InventoryModel currentData, InventoryEvent @event) {
      return currentData;
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);

    // Assert - Should generate two runners
    await Assert.That(result.GeneratedTrees).Count().IsEqualTo(2);

    var orderRunner = GeneratorTestHelper.GetGeneratedSource(result, "OrderPerspectiveRunner.g.cs");
    var inventoryRunner = GeneratorTestHelper.GetGeneratedSource(result, "InventoryPerspectiveRunner.g.cs");

    await Assert.That(orderRunner).IsNotNull();
    await Assert.That(inventoryRunner).IsNotNull();

    await Assert.That(orderRunner).Contains("OrderPerspectiveRunner");
    await Assert.That(inventoryRunner).Contains("InventoryPerspectiveRunner");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_GeneratedCodeUsesCorrectNamespaceAsync() {
    // Arrange
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace TestNamespace {
  public record OrderEvent : IEvent { }

  public record OrderModel {
    [StreamId]
    public string OrderId { get; init; } = "";
  }

  public class OrderPerspective : IPerspectiveFor<OrderModel, OrderEvent> {
    public OrderModel Apply(OrderModel currentData, OrderEvent @event) {
      return currentData;
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);

    // Assert - Should use TestAssembly.Generated namespace
    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "OrderPerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();
    await Assert.That(runnerSource).Contains("namespace TestAssembly.Generated");
    await Assert.That(runnerSource).Contains("using System");
    await Assert.That(runnerSource).Contains("using Whizbang.Core");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_StreamIdPropertyNameIncludedAsync() {
    // Arrange - Test that stream key property name is used in generated runner
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace TestNamespace {
  public record OrderEvent : IEvent { }

  public record OrderModel {
    [StreamId]
    public string CustomOrderIdentifier { get; init; } = "";
    public string Status { get; init; } = "";
  }

  public class OrderPerspective : IPerspectiveFor<OrderModel, OrderEvent> {
    public OrderModel Apply(OrderModel currentData, OrderEvent @event) {
      return currentData;
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);

    // Assert - Should include stream key property name in generated code
    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "OrderPerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();
    await Assert.That(runnerSource).Contains("CustomOrderIdentifier");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_GeneratesExtractStreamIdMethod_UsingEventStreamIdAsync() {
    // Arrange - Test that runner generates ExtractStreamId method using event's [StreamId]
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace TestNamespace {
  public record ProductCreatedEvent : IEvent {
    [StreamId]
    public Guid ProductId { get; init; }  // Event's stream key
    public string ProductName { get; init; } = "";
  }

  public record ProductModel {
    [StreamId]
    public Guid ProductId { get; init; }  // Model's stream key (same property)
    public string ProductName { get; init; } = "";
  }

  public class ProductPerspective : IPerspectiveFor<ProductModel, ProductCreatedEvent> {
    public ProductModel Apply(ProductModel currentData, ProductCreatedEvent @event) {
      return currentData with { ProductName = @event.ProductName };
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);

    // Assert - Should generate ExtractStreamId method
    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "ProductPerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();

    // DEBUG: Print generated source
    Console.WriteLine("=== GENERATED SOURCE ===");
    Console.WriteLine(runnerSource);
    Console.WriteLine("=== END GENERATED SOURCE ===");

    // Should have ExtractStreamId method
    await Assert.That(runnerSource).Contains("ExtractStreamId");

    // Should access event's ProductId property (the [StreamId] property)
    await Assert.That(runnerSource).Contains("@event.ProductId");

    // Should return the stream ID as string
    await Assert.That(runnerSource).Contains("private static string ExtractStreamId");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_MultipleEvents_GeneratesExtractStreamIdForEachAsync() {
    // Arrange - Perspective with multiple events should generate ExtractStreamId for each
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace TestNamespace {
  public record OrderCreatedEvent : IEvent {
    [StreamId]
    public Guid OrderId { get; init; }
    public string CustomerName { get; init; } = "";
  }

  public record OrderShippedEvent : IEvent {
    [StreamId]
    public Guid OrderId { get; init; }  // Same property name, different event
    public string TrackingNumber { get; init; } = "";
  }

  public record OrderModel {
    [StreamId]
    public Guid OrderId { get; init; }
    public string Status { get; init; } = "";
  }

  public class OrderPerspective :
    IPerspectiveFor<OrderModel, OrderCreatedEvent>,
    IPerspectiveFor<OrderModel, OrderShippedEvent> {

    public OrderModel Apply(OrderModel currentData, OrderCreatedEvent @event) {
      return currentData with { Status = "Created" };
    }

    public OrderModel Apply(OrderModel currentData, OrderShippedEvent @event) {
      return currentData with { Status = "Shipped" };
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);

    // Assert - Should have multiple ExtractStreamId overloads (one per event type)
    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "OrderPerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();

    // Should have ExtractStreamId for OrderCreatedEvent
    await Assert.That(runnerSource).Contains("ExtractStreamId(global::TestNamespace.OrderCreatedEvent @event)");

    // Should have ExtractStreamId for OrderShippedEvent
    await Assert.That(runnerSource).Contains("ExtractStreamId(global::TestNamespace.OrderShippedEvent @event)");

    // Both should access OrderId property
    var orderIdCount = _countOccurrences(runnerSource!, "@event.OrderId");
    await Assert.That(orderIdCount).IsGreaterThanOrEqualTo(2); // At least one for each event type
  }

  /// <summary>
  /// Helper method to count occurrences of a substring in a string.
  /// </summary>
  private static int _countOccurrences(string text, string substring) {
    var count = 0;
    var index = 0;
    while ((index = text.IndexOf(substring, index, StringComparison.Ordinal)) != -1) {
      count++;
      index += substring.Length;
    }
    return count;
  }

  // ==================== [MustExist] Attribute Tests ====================

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_MustExistAttribute_GeneratesNullCheckAsync() {
    // Arrange - Perspective with [MustExist] on one Apply method
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;

namespace TestNamespace {
  public record OrderCreatedEvent : IEvent {
    [StreamId]
    public Guid OrderId { get; init; }
  }

  public record OrderShippedEvent : IEvent {
    [StreamId]
    public Guid OrderId { get; init; }
  }

  public record OrderModel {
    [StreamId]
    public Guid OrderId { get; init; }
    public string Status { get; init; } = "";
  }

  public class OrderPerspective :
    IPerspectiveFor<OrderModel, OrderCreatedEvent>,
    IPerspectiveFor<OrderModel, OrderShippedEvent> {

    // Creation - handles null (no [MustExist])
    public OrderModel Apply(OrderModel? currentData, OrderCreatedEvent @event) {
      return new OrderModel { OrderId = @event.OrderId, Status = "Created" };
    }

    // Update - requires existing model
    [MustExist]
    public OrderModel Apply(OrderModel currentData, OrderShippedEvent @event) {
      return currentData with { Status = "Shipped" };
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);

    // Assert - Should generate null check for [MustExist] method
    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "OrderPerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();

    // Should have null check before calling Apply for OrderShippedEvent
    await Assert.That(runnerSource).Contains("case global::TestNamespace.OrderShippedEvent typedEvent:");
    await Assert.That(runnerSource).Contains("if (currentModel == null)");
    await Assert.That(runnerSource).Contains("OrderModel must exist when applying OrderShippedEvent in OrderPerspective");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_MustExistAttribute_NoNullCheckForNonAttributedMethodAsync() {
    // Arrange - Perspective with [MustExist] on one Apply but not the other
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;

namespace TestNamespace {
  public record OrderCreatedEvent : IEvent {
    [StreamId]
    public Guid OrderId { get; init; }
  }

  public record OrderShippedEvent : IEvent {
    [StreamId]
    public Guid OrderId { get; init; }
  }

  public record OrderModel {
    [StreamId]
    public Guid OrderId { get; init; }
    public string Status { get; init; } = "";
  }

  public class OrderPerspective :
    IPerspectiveFor<OrderModel, OrderCreatedEvent>,
    IPerspectiveFor<OrderModel, OrderShippedEvent> {

    // No [MustExist] - should NOT have null check
    public OrderModel Apply(OrderModel? currentData, OrderCreatedEvent @event) {
      return new OrderModel { OrderId = @event.OrderId, Status = "Created" };
    }

    [MustExist]
    public OrderModel Apply(OrderModel currentData, OrderShippedEvent @event) {
      return currentData with { Status = "Shipped" };
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);

    // Assert
    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "OrderPerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();

    // Should only have ONE MustExist null check (for OrderShippedEvent only)
    // Template has its own null check for model initialization, so count the specific error message pattern
    var mustExistCheckCount = _countOccurrences(runnerSource!, "must exist when applying");
    await Assert.That(mustExistCheckCount).IsEqualTo(1);

    // The null check should be for OrderShippedEvent, not OrderCreatedEvent
    await Assert.That(runnerSource).Contains("OrderModel must exist when applying OrderShippedEvent");
    await Assert.That(runnerSource).DoesNotContain("OrderModel must exist when applying OrderCreatedEvent");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_MustExistAttribute_AllEventsWithAttribute_GeneratesNullCheckForAllAsync() {
    // Arrange - All Apply methods have [MustExist]
    const string source = @"
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;

namespace TestNamespace {
  public record OrderEvent1 : IEvent {
    [StreamId]
    public Guid OrderId { get; init; }
  }

  public record OrderEvent2 : IEvent {
    [StreamId]
    public Guid OrderId { get; init; }
  }

  public record OrderModel {
    [StreamId]
    public Guid OrderId { get; init; }
  }

  public class OrderPerspective :
    IPerspectiveFor<OrderModel, OrderEvent1>,
    IPerspectiveFor<OrderModel, OrderEvent2> {

    [MustExist]
    public OrderModel Apply(OrderModel currentData, OrderEvent1 @event) => currentData;

    [MustExist]
    public OrderModel Apply(OrderModel currentData, OrderEvent2 @event) => currentData;
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);

    // Assert - Both cases should have MustExist null checks
    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "OrderPerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();

    // Count MustExist-specific pattern (template has its own null check for model initialization)
    var mustExistCheckCount = _countOccurrences(runnerSource!, "must exist when applying");
    await Assert.That(mustExistCheckCount).IsEqualTo(2);

    // Both should have descriptive error messages
    await Assert.That(runnerSource).Contains("OrderModel must exist when applying OrderEvent1 in OrderPerspective");
    await Assert.That(runnerSource).Contains("OrderModel must exist when applying OrderEvent2 in OrderPerspective");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_NoMustExistAttribute_NoNullCheckGeneratedAsync() {
    // Arrange - No [MustExist] attributes at all
    const string source = @"
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;

namespace TestNamespace {
  public record OrderEvent : IEvent {
    [StreamId]
    public Guid OrderId { get; init; }
  }

  public record OrderModel {
    [StreamId]
    public Guid OrderId { get; init; }
  }

  public class OrderPerspective : IPerspectiveFor<OrderModel, OrderEvent> {
    public OrderModel Apply(OrderModel? currentData, OrderEvent @event) {
      return currentData ?? new OrderModel { OrderId = @event.OrderId };
    }
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);

    // Assert - No MustExist null check generated (template has separate null check for model initialization)
    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "OrderPerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();
    // Check for absence of MustExist-specific error message (the template has its own null check for initialization)
    await Assert.That(runnerSource).DoesNotContain("must exist when applying");
    await Assert.That(runnerSource).DoesNotContain("throw new global::System.InvalidOperationException");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_MustExistAttribute_ErrorMessageIncludesContextAsync() {
    // Arrange - Verify error message format includes perspective, model, and event names
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;

namespace TestNamespace {
  public record CustomerUpdatedEvent : IEvent {
    [StreamId]
    public Guid CustomerId { get; init; }
  }

  public record CustomerReadModel {
    [StreamId]
    public Guid CustomerId { get; init; }
    public string Name { get; init; } = "";
  }

  public class CustomerPerspective : IPerspectiveFor<CustomerReadModel, CustomerUpdatedEvent> {
    [MustExist]
    public CustomerReadModel Apply(CustomerReadModel currentData, CustomerUpdatedEvent @event) {
      return currentData with { Name = "Updated" };
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);

    // Assert - Error message should include all context
    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "CustomerPerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();

    // Should include model type name
    await Assert.That(runnerSource).Contains("CustomerReadModel must exist");

    // Should include event type name
    await Assert.That(runnerSource).Contains("when applying CustomerUpdatedEvent");

    // Should include perspective class name
    await Assert.That(runnerSource).Contains("in CustomerPerspective");

    // Full expected message
    await Assert.That(runnerSource).Contains(
        "CustomerReadModel must exist when applying CustomerUpdatedEvent in CustomerPerspective");
  }

  // ==================== ModelAction Return Type Tests ====================

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_ModelActionReturn_GeneratesActionHandlingAsync() {
    // Arrange - Apply returns ModelAction for deletion
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;

namespace TestNamespace {
  public record OrderCancelledEvent : IEvent {
    [StreamId]
    public Guid OrderId { get; init; }
  }

  public record OrderModel {
    [StreamId]
    public Guid OrderId { get; init; }
    public string Status { get; init; } = "";
    public DateTimeOffset? DeletedAt { get; init; }
  }

  public class OrderPerspective : IPerspectiveFor<OrderModel, OrderCancelledEvent> {
    public ModelAction Apply(OrderModel currentData, OrderCancelledEvent @event) {
      return ModelAction.Delete;
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);

    // Assert - Should generate code handling ModelAction return
    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "OrderPerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();

    // Should have handling for ModelAction return type
    await Assert.That(runnerSource).Contains("ModelAction");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_NullableModelReturn_GeneratesNoChangeCheckAsync() {
    // Arrange - Apply returns TModel? (nullable) for optional updates
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;

namespace TestNamespace {
  public record OrderUpdatedEvent : IEvent {
    [StreamId]
    public Guid OrderId { get; init; }
    public bool ShouldSkip { get; init; }
  }

  public record OrderModel {
    [StreamId]
    public Guid OrderId { get; init; }
    public string Status { get; init; } = "";
  }

  public class OrderPerspective : IPerspectiveFor<OrderModel, OrderUpdatedEvent> {
    public OrderModel? Apply(OrderModel? currentData, OrderUpdatedEvent @event) {
      if (@event.ShouldSkip) return null;  // No change
      return currentData with { Status = "Updated" };
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);

    // Assert - Should generate runner that handles null return (no change)
    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "OrderPerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();

    // For now just verify a runner is generated (the return type handling will be added)
    await Assert.That(runnerSource).Contains("OrderPerspectiveRunner");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_TupleReturn_GeneratesHybridHandlingAsync() {
    // Arrange - Apply returns (TModel?, ModelAction) tuple for hybrid modify+action
    const string source = @"
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;

namespace TestNamespace {
  public record OrderArchivedEvent : IEvent {
    [StreamId]
    public Guid OrderId { get; init; }
    public bool ShouldPurge { get; init; }
  }

  public record OrderModel {
    [StreamId]
    public Guid OrderId { get; init; }
    public DateTimeOffset? ArchivedAt { get; init; }
    public DateTimeOffset? DeletedAt { get; init; }
  }

  public class OrderPerspective : IPerspectiveFor<OrderModel, OrderArchivedEvent> {
    public (OrderModel?, ModelAction) Apply(OrderModel currentData, OrderArchivedEvent @event) {
      if (@event.ShouldPurge)
        return (null, ModelAction.Purge);
      return (currentData with { ArchivedAt = DateTimeOffset.UtcNow }, ModelAction.None);
    }
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);

    // Assert - Should generate runner
    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "OrderPerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();
    await Assert.That(runnerSource).Contains("OrderPerspectiveRunner");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_ApplyResultReturn_GeneratesFullHandlingAsync() {
    // Arrange - Apply returns ApplyResult<TModel> for full flexibility
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;

namespace TestNamespace {
  public record OrderProcessedEvent : IEvent {
    [StreamId]
    public Guid OrderId { get; init; }
    public string Action { get; init; } = "";
  }

  public record OrderModel {
    [StreamId]
    public Guid OrderId { get; init; }
    public string Status { get; init; } = "";
    public DateTimeOffset? DeletedAt { get; init; }
  }

  public class OrderPerspective : IPerspectiveFor<OrderModel, OrderProcessedEvent> {
    public ApplyResult<OrderModel> Apply(OrderModel currentData, OrderProcessedEvent @event) {
      return @event.Action switch {
        "delete" => ApplyResult<OrderModel>.Delete(),
        "purge" => ApplyResult<OrderModel>.Purge(),
        "skip" => ApplyResult<OrderModel>.None(),
        _ => ApplyResult<OrderModel>.Update(currentData with { Status = @event.Action })
      };
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);

    // Assert - Should generate runner
    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "OrderPerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();
    await Assert.That(runnerSource).Contains("OrderPerspectiveRunner");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_MixedReturnTypes_GeneratesCorrectlyAsync() {
    // Arrange - Different Apply methods with different return types
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;

namespace TestNamespace {
  public record OrderCreatedEvent : IEvent {
    [StreamId]
    public Guid OrderId { get; init; }
  }

  public record OrderCancelledEvent : IEvent {
    [StreamId]
    public Guid OrderId { get; init; }
  }

  public record OrderModel {
    [StreamId]
    public Guid OrderId { get; init; }
    public string Status { get; init; } = "";
    public DateTimeOffset? DeletedAt { get; init; }
  }

  public class OrderPerspective :
    IPerspectiveFor<OrderModel, OrderCreatedEvent>,
    IPerspectiveFor<OrderModel, OrderCancelledEvent> {

    // Standard return - returns model
    public OrderModel Apply(OrderModel? currentData, OrderCreatedEvent @event) {
      return new OrderModel { OrderId = @event.OrderId, Status = "Created" };
    }

    // Action return - returns ModelAction for deletion
    public ModelAction Apply(OrderModel currentData, OrderCancelledEvent @event) {
      return ModelAction.Delete;
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);

    // Assert - Should generate runner with both event types handled
    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "OrderPerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();
    await Assert.That(runnerSource).Contains("OrderPerspectiveRunner");
    await Assert.That(runnerSource).Contains("OrderCreatedEvent");
    await Assert.That(runnerSource).Contains("OrderCancelledEvent");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_NestedClasses_GeneratesUniqueHintNamesAsync() {
    // Arrange - Multiple nested classes with the same simple name "Projection"
    // should generate unique hintNames like "OrderStatusProjectionRunner.g.cs"
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;

namespace TestNamespace {
  public record DraftCreatedEvent : IEvent {
    [StreamId]
    public string DraftId { get; init; } = "";
  }

  public record EmbeddingCreatedEvent : IEvent {
    [StreamId]
    public string EmbeddingId { get; init; } = "";
  }

  public record DraftModel {
    [StreamId]
    public string DraftId { get; init; } = "";
    public string Content { get; init; } = "";
  }

  public record EmbeddingModel {
    [StreamId]
    public string EmbeddingId { get; init; } = "";
    public string Content { get; init; } = "";
  }

  public static class OrderStatus {
    public class Projection : IPerspectiveFor<DraftModel, DraftCreatedEvent> {
      public DraftModel Apply(DraftModel currentData, DraftCreatedEvent @event) {
        return currentData with { Content = "Draft" };
      }
    }
  }

  public static class Embedding {
    public class Projection : IPerspectiveFor<EmbeddingModel, EmbeddingCreatedEvent> {
      public EmbeddingModel Apply(EmbeddingModel currentData, EmbeddingCreatedEvent @event) {
        return currentData with { Content = "Embedding" };
      }
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);

    // Assert - Should generate 2 runners with unique hintNames
    // Check that no CS8785 error (duplicate hintName) exists
    var duplicateHintErrors = result.Diagnostics
        .Where(d => d.Id == "CS8785" || d.GetMessage(CultureInfo.InvariantCulture).Contains("hintName"))
        .ToList();
    await Assert.That(duplicateHintErrors).Count().IsEqualTo(0);

    await Assert.That(result.GeneratedTrees).Count().IsEqualTo(2);

    // The hintNames should include parent type names for uniqueness
    var draftRunner = GeneratorTestHelper.GetGeneratedSource(result, "OrderStatusProjectionRunner.g.cs");
    var embeddingRunner = GeneratorTestHelper.GetGeneratedSource(result, "EmbeddingProjectionRunner.g.cs");

    await Assert.That(draftRunner).IsNotNull();
    await Assert.That(embeddingRunner).IsNotNull();

    // Verify the class names also include parent type
    await Assert.That(draftRunner).Contains("class OrderStatusProjectionRunner");
    await Assert.That(embeddingRunner).Contains("class EmbeddingProjectionRunner");
  }

  // ==================== Multi-Event Support Tests (6-50 events) ====================

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_PerspectiveWith10Events_GeneratesRunnerAsync() {
    // Arrange - Perspective implementing IPerspectiveFor with 10 event types
    const string source = @"
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;

namespace TestNamespace {
  public record Event1 : IEvent { [StreamId] public Guid Id { get; init; } }
  public record Event2 : IEvent { [StreamId] public Guid Id { get; init; } }
  public record Event3 : IEvent { [StreamId] public Guid Id { get; init; } }
  public record Event4 : IEvent { [StreamId] public Guid Id { get; init; } }
  public record Event5 : IEvent { [StreamId] public Guid Id { get; init; } }
  public record Event6 : IEvent { [StreamId] public Guid Id { get; init; } }
  public record Event7 : IEvent { [StreamId] public Guid Id { get; init; } }
  public record Event8 : IEvent { [StreamId] public Guid Id { get; init; } }
  public record Event9 : IEvent { [StreamId] public Guid Id { get; init; } }
  public record Event10 : IEvent { [StreamId] public Guid Id { get; init; } }

  public record MultiEventModel {
    [StreamId]
    public Guid Id { get; init; }
    public int Counter { get; init; }
  }

  public class MultiEventPerspective : IPerspectiveFor<MultiEventModel, Event1, Event2, Event3, Event4, Event5, Event6, Event7, Event8, Event9, Event10> {
    public MultiEventModel Apply(MultiEventModel current, Event1 @event) => current with { Counter = current.Counter + 1 };
    public MultiEventModel Apply(MultiEventModel current, Event2 @event) => current with { Counter = current.Counter + 2 };
    public MultiEventModel Apply(MultiEventModel current, Event3 @event) => current with { Counter = current.Counter + 3 };
    public MultiEventModel Apply(MultiEventModel current, Event4 @event) => current with { Counter = current.Counter + 4 };
    public MultiEventModel Apply(MultiEventModel current, Event5 @event) => current with { Counter = current.Counter + 5 };
    public MultiEventModel Apply(MultiEventModel current, Event6 @event) => current with { Counter = current.Counter + 6 };
    public MultiEventModel Apply(MultiEventModel current, Event7 @event) => current with { Counter = current.Counter + 7 };
    public MultiEventModel Apply(MultiEventModel current, Event8 @event) => current with { Counter = current.Counter + 8 };
    public MultiEventModel Apply(MultiEventModel current, Event9 @event) => current with { Counter = current.Counter + 9 };
    public MultiEventModel Apply(MultiEventModel current, Event10 @event) => current with { Counter = current.Counter + 10 };
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);

    // Assert - Should generate runner for perspective with 10 events
    await Assert.That(result.GeneratedTrees).Count().IsEqualTo(1);

    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "MultiEventPerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();
    await Assert.That(runnerSource).Contains("class MultiEventPerspectiveRunner");
    await Assert.That(runnerSource).Contains("IPerspectiveRunner");

    // Verify all 10 event types are handled
    await Assert.That(runnerSource).Contains("Event1");
    await Assert.That(runnerSource).Contains("Event10");
    await Assert.That(runnerSource).Contains("MultiEventModel");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_PerspectiveWith25Events_GeneratesRunnerAsync() {
    // Arrange - Perspective implementing IPerspectiveFor with 25 event types
    var eventDeclarations = string.Join("\n",
        Enumerable.Range(1, 25).Select(i =>
            $"  public record Evt{i} : IEvent {{ [StreamId] public Guid Id {{ get; init; }} }}"));

    var applyMethods = string.Join("\n",
        Enumerable.Range(1, 25).Select(i =>
            $"    public Model Apply(Model c, Evt{i} e) => c with {{ Counter = c.Counter + {i} }};"));

    var eventTypeParams = string.Join(", ", Enumerable.Range(1, 25).Select(i => $"Evt{i}"));

    var source = $@"
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;

namespace TestNamespace {{
{eventDeclarations}

  public record Model {{
    [StreamId]
    public Guid Id {{ get; init; }}
    public int Counter {{ get; init; }}
  }}

  public class BigPerspective : IPerspectiveFor<Model, {eventTypeParams}> {{
{applyMethods}
  }}
}}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);

    // Assert - Should generate runner for perspective with 25 events
    await Assert.That(result.GeneratedTrees).Count().IsEqualTo(1);

    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "BigPerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();
    await Assert.That(runnerSource).Contains("class BigPerspectiveRunner");
    await Assert.That(runnerSource).Contains("IPerspectiveRunner");
    await Assert.That(runnerSource).Contains("Evt1");
    await Assert.That(runnerSource).Contains("Evt25");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_ModelMissingStreamId_EmitsWarningAsync() {
    // Arrange - perspective with model that has no [StreamId]
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;

namespace TestNamespace {
  public record OrderCreatedEvent : IEvent {
    public string OrderId { get; init; } = "";
  }

  public record OrderReadModel {
    public string OrderId { get; init; } = "";  // No [StreamId]!
    public string Status { get; init; } = "";
  }

  public class OrderPerspective : IPerspectiveFor<OrderReadModel, OrderCreatedEvent> {
    public OrderReadModel Apply(OrderReadModel currentData, OrderCreatedEvent @event) {
      return currentData with { Status = "Created" };
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);

    // Assert - warning should be emitted (WHIZ033)
    var warning = result.Diagnostics.FirstOrDefault(d => d.Id == "WHIZ033");
    await Assert.That(warning).IsNotNull();
    await Assert.That(warning!.Severity).IsEqualTo(DiagnosticSeverity.Warning);
    await Assert.That(warning.GetMessage(CultureInfo.InvariantCulture)).Contains("OrderPerspective");
    await Assert.That(warning.GetMessage(CultureInfo.InvariantCulture)).Contains("OrderReadModel");
    await Assert.That(warning.GetMessage(CultureInfo.InvariantCulture)).Contains("[StreamId]");

    // No runner should be generated
    await Assert.That(result.GeneratedTrees).Count().IsEqualTo(0);
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_ModelHasStreamId_NoWarningAsync() {
    // Arrange - perspective with model that HAS [StreamId]
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;

namespace TestNamespace {
  public record OrderCreatedEvent : IEvent {
    public string OrderId { get; init; } = "";
  }

  public record OrderReadModel {
    [StreamId]
    public string OrderId { get; init; } = "";  // Has [StreamId]!
    public string Status { get; init; } = "";
  }

  public class OrderPerspective : IPerspectiveFor<OrderReadModel, OrderCreatedEvent> {
    public OrderReadModel Apply(OrderReadModel currentData, OrderCreatedEvent @event) {
      return currentData with { Status = "Created" };
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);

    // Assert - no WHIZ033 warning should be emitted
    var warning = result.Diagnostics.FirstOrDefault(d => d.Id == "WHIZ033");
    await Assert.That(warning).IsNull();

    // Runner SHOULD be generated
    await Assert.That(result.GeneratedTrees).Count().IsEqualTo(1);
  }

  // ========================================
  // Physical Field Tests
  // ========================================

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_VectorField_GeneratesUpsertWithPhysicalFieldsAsync() {
    // Arrange - model with [VectorField] should generate UpsertWithPhysicalFieldsAsync call
    const string source = @"
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;

namespace TestNamespace {
  public record EmbeddingUpdatedEvent : IEvent {
    public Guid Id { get; init; }
    public float[]? Embeddings { get; init; }
  }

  public record EmbeddingModel {
    [StreamId]
    public Guid Id { get; init; }

    [VectorField(1536)]
    public float[]? Embeddings { get; init; }
  }

  public class EmbeddingPerspective : IPerspectiveFor<EmbeddingModel, EmbeddingUpdatedEvent> {
    public EmbeddingModel Apply(EmbeddingModel currentData, EmbeddingUpdatedEvent @event) {
      return currentData with { Embeddings = @event.Embeddings };
    }
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);

    // Assert - Should generate runner with UpsertWithPhysicalFieldsAsync
    await Assert.That(result.GeneratedTrees).Count().IsEqualTo(1);

    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "EmbeddingPerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();

    // Should use UpsertWithPhysicalFieldsAsync instead of UpsertAsync
    await Assert.That(runnerSource).Contains("UpsertWithPhysicalFieldsAsync");
    await Assert.That(runnerSource).Contains("physicalFieldValues");
    await Assert.That(runnerSource).Contains(@"""embeddings""");  // snake_case column name
    await Assert.That(runnerSource).Contains("model.Embeddings");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_PhysicalField_GeneratesUpsertWithPhysicalFieldsAsync() {
    // Arrange - model with [PhysicalField] should generate UpsertWithPhysicalFieldsAsync call
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;

namespace TestNamespace {
  public record OrderUpdatedEvent : IEvent {
    public Guid Id { get; init; }
    public string Status { get; init; } = "";
  }

  public record OrderModel {
    [StreamId]
    public Guid Id { get; init; }

    [PhysicalField(Indexed = true)]
    public string Status { get; init; } = "";
  }

  public class OrderPerspective : IPerspectiveFor<OrderModel, OrderUpdatedEvent> {
    public OrderModel Apply(OrderModel currentData, OrderUpdatedEvent @event) {
      return currentData with { Status = @event.Status };
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);

    // Assert - Should generate runner with UpsertWithPhysicalFieldsAsync
    await Assert.That(result.GeneratedTrees).Count().IsEqualTo(1);

    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "OrderPerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();

    // Should use UpsertWithPhysicalFieldsAsync instead of UpsertAsync
    await Assert.That(runnerSource).Contains("UpsertWithPhysicalFieldsAsync");
    await Assert.That(runnerSource).Contains("physicalFieldValues");
    await Assert.That(runnerSource).Contains(@"""status""");  // snake_case column name
    await Assert.That(runnerSource).Contains("model.Status");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_NoPhysicalFields_UsesSimpleUpsertAsync() {
    // Arrange - model without physical fields should use simple UpsertAsync
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;

namespace TestNamespace {
  public record SimpleEvent : IEvent {
    public Guid Id { get; init; }
  }

  public record SimpleModel {
    [StreamId]
    public Guid Id { get; init; }
    public string Name { get; init; } = "";
  }

  public class SimplePerspective : IPerspectiveFor<SimpleModel, SimpleEvent> {
    public SimpleModel Apply(SimpleModel currentData, SimpleEvent @event) {
      return currentData;
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);

    // Assert - Should generate runner with simple UpsertAsync (no physical fields)
    await Assert.That(result.GeneratedTrees).Count().IsEqualTo(1);

    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "SimplePerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();

    // Should use UpsertAsync, NOT UpsertWithPhysicalFieldsAsync
    await Assert.That(runnerSource).Contains("UpsertAsync(");
    await Assert.That(runnerSource).DoesNotContain("UpsertWithPhysicalFieldsAsync");
    await Assert.That(runnerSource).DoesNotContain("physicalFieldValues");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_MultiplePhysicalFields_GeneratesAllFieldsAsync() {
    // Arrange - model with multiple physical fields
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;

namespace TestNamespace {
  public record ProductUpdatedEvent : IEvent {
    public Guid Id { get; init; }
  }

  public record ProductModel {
    [StreamId]
    public Guid Id { get; init; }

    [PhysicalField(Indexed = true)]
    public string Sku { get; init; } = "";

    [VectorField(768)]
    public float[]? DescriptionEmbedding { get; init; }

    [PhysicalField]
    public decimal Price { get; init; }
  }

  public class ProductPerspective : IPerspectiveFor<ProductModel, ProductUpdatedEvent> {
    public ProductModel Apply(ProductModel currentData, ProductUpdatedEvent @event) {
      return currentData;
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);

    // Assert - Should generate all physical fields in dictionary
    await Assert.That(result.GeneratedTrees).Count().IsEqualTo(1);

    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "ProductPerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();

    // Should have all three physical fields
    await Assert.That(runnerSource).Contains(@"""sku""");
    await Assert.That(runnerSource).Contains(@"""description_embedding""");
    await Assert.That(runnerSource).Contains(@"""price""");
    await Assert.That(runnerSource).Contains("model.Sku");
    await Assert.That(runnerSource).Contains("model.DescriptionEmbedding");
    await Assert.That(runnerSource).Contains("model.Price");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_VectorFieldWithCustomColumnName_UsesCustomNameAsync() {
    // Arrange - VectorField with custom ColumnName
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;

namespace TestNamespace {
  public record EmbeddingEvent : IEvent {
    public Guid Id { get; init; }
  }

  public record EmbeddingModel {
    [StreamId]
    public Guid Id { get; init; }

    [VectorField(1536, ColumnName = "custom_embedding_col")]
    public float[]? Vector { get; init; }
  }

  public class EmbeddingPerspective : IPerspectiveFor<EmbeddingModel, EmbeddingEvent> {
    public EmbeddingModel Apply(EmbeddingModel currentData, EmbeddingEvent @event) {
      return currentData;
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);

    // Assert - Should use custom column name
    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "EmbeddingPerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();

    // Should use custom column name instead of snake_case default
    await Assert.That(runnerSource).Contains(@"""custom_embedding_col""");
    await Assert.That(runnerSource).Contains("model.Vector");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_StaticProperty_NotIncludedInPhysicalFieldsAsync() {
    // Arrange - Static properties with VectorField should be ignored
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;

namespace TestNamespace {
  public record TestEvent : IEvent {
    public Guid Id { get; init; }
  }

  public class TestModel {
    [StreamId]
    public Guid Id { get; init; }

    [VectorField(512)]
    public static float[]? StaticVector { get; set; }  // Static - should be ignored

    public string Name { get; init; } = "";
  }

  public class TestPerspective : IPerspectiveFor<TestModel, TestEvent> {
    public TestModel Apply(TestModel currentData, TestEvent @event) {
      return currentData;
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);

    // Assert - Static vector field should NOT be in physical fields
    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "TestPerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();

    // Should use simple UpsertAsync (no physical fields after excluding static)
    await Assert.That(runnerSource).DoesNotContain("UpsertWithPhysicalFieldsAsync");
    await Assert.That(runnerSource).DoesNotContain("StaticVector");
    await Assert.That(runnerSource).Contains("UpsertAsync(");
  }

  // ==================== Security Context Propagation Tests ====================

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_PostPerspectiveDetached_EstablishesSecurityContextAsync() {
    // Arrange - This test verifies that PostPerspectiveDetached lifecycle handlers
    // have access to TenantId from the message envelope's security context.
    // Security context is now established by ReceptorInvoker.InvokeAsync() internally,
    // not by the generated template. The template uses scoped IReceptorInvoker which
    // handles ALL security context setup (IScopeContextAccessor, IMessageContextAccessor).
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;

namespace TestNamespace {
  public record OrderCreatedEvent : IEvent {
    [StreamId]
    public Guid OrderId { get; init; }
    public string CustomerName { get; init; } = "";
  }

  public record OrderModel {
    [StreamId]
    public Guid OrderId { get; init; }
    public string Status { get; init; } = "";
  }

  public class OrderPerspective : IPerspectiveFor<OrderModel, OrderCreatedEvent> {
    public OrderModel Apply(OrderModel currentData, OrderCreatedEvent @event) {
      return currentData with { Status = "Created" };
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);

    // Assert - Should generate runner
    await Assert.That(result.GeneratedTrees).Count().IsEqualTo(1);

    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "OrderPerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();

    // The generated template now uses scoped IReceptorInvoker for lifecycle invocation.
    // ReceptorInvoker.InvokeAsync() handles ALL security context setup internally:
    // - Calls IMessageSecurityContextProvider.EstablishContextAsync
    // - Sets IScopeContextAccessor.Current
    // - Sets IMessageContextAccessor.Current with TenantId from envelope

    // Should resolve IReceptorInvoker from scoped service provider
    await Assert.That(runnerSource).Contains("GetService<global::Whizbang.Core.Messaging.IReceptorInvoker>()");

    // Should invoke lifecycle via ReceptorInvoker with PostPerspectiveDetached stage
    await Assert.That(runnerSource).Contains("receptorInvoker.InvokeAsync(envelope, LifecycleStage.PostPerspectiveDetached");

    // Should create a scope for lifecycle invocation
    await Assert.That(runnerSource).Contains("_scopeFactory.CreateAsyncScope()");
  }

  #region IPerspectiveWithActionsFor Integration Tests

  /// <summary>
  /// Tests that IPerspectiveWithActionsFor interface generates a runner correctly.
  /// This is the primary integration test for the new strongly-typed deletion interface.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_IPerspectiveWithActionsFor_GeneratesRunnerAsync() {
    // Arrange - Perspective implementing IPerspectiveWithActionsFor (returns ApplyResult<TModel>)
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;

namespace TestNamespace {
  public record OrderDeletedEvent : IEvent {
    [StreamId]
    public Guid OrderId { get; init; }
  }

  public record OrderModel {
    [StreamId]
    public Guid OrderId { get; init; }
    public string Status { get; init; } = "";
  }

  public class OrderPerspective : IPerspectiveWithActionsFor<OrderModel, OrderDeletedEvent> {
    public ApplyResult<OrderModel> Apply(OrderModel currentData, OrderDeletedEvent @event) {
      return ApplyResult<OrderModel>.Delete();
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);

    // Assert - Should generate runner
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);
    await Assert.That(result.GeneratedTrees).Count().IsEqualTo(1);

    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "OrderPerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();
    await Assert.That(runnerSource).Contains("OrderPerspectiveRunner");
    await Assert.That(runnerSource).Contains("OrderDeletedEvent");
    await Assert.That(runnerSource).Contains("ModelAction");
  }

  /// <summary>
  /// Tests that a class can implement both IPerspectiveFor and IPerspectiveWithActionsFor
  /// for different event types on the same class.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_MixedInterfaces_BothDetectedAsync() {
    // Arrange - Class implementing both IPerspectiveFor and IPerspectiveWithActionsFor
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;

namespace TestNamespace {
  public record OrderCreatedEvent : IEvent {
    [StreamId]
    public Guid OrderId { get; init; }
  }

  public record OrderUpdatedEvent : IEvent {
    [StreamId]
    public Guid OrderId { get; init; }
    public string NewStatus { get; init; } = "";
  }

  public record OrderDeletedEvent : IEvent {
    [StreamId]
    public Guid OrderId { get; init; }
  }

  public record OrderPurgedEvent : IEvent {
    [StreamId]
    public Guid OrderId { get; init; }
  }

  public record OrderModel {
    [StreamId]
    public Guid OrderId { get; init; }
    public string Status { get; init; } = "";
  }

  public class OrderPerspective :
    IPerspectiveFor<OrderModel, OrderCreatedEvent>,           // Returns TModel
    IPerspectiveFor<OrderModel, OrderUpdatedEvent>,           // Returns TModel
    IPerspectiveWithActionsFor<OrderModel, OrderDeletedEvent>, // Returns ApplyResult<TModel>
    IPerspectiveWithActionsFor<OrderModel, OrderPurgedEvent> { // Returns ApplyResult<TModel>

    public OrderModel Apply(OrderModel currentData, OrderCreatedEvent @event) {
      return new OrderModel { OrderId = @event.OrderId, Status = "Created" };
    }

    public OrderModel Apply(OrderModel currentData, OrderUpdatedEvent @event) {
      return currentData with { Status = @event.NewStatus };
    }

    public ApplyResult<OrderModel> Apply(OrderModel currentData, OrderDeletedEvent @event) {
      return ApplyResult<OrderModel>.Delete();
    }

    public ApplyResult<OrderModel> Apply(OrderModel currentData, OrderPurgedEvent @event) {
      return ApplyResult<OrderModel>.Purge();
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);

    // Assert - Should generate runner handling all 4 event types
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);
    await Assert.That(result.GeneratedTrees).Count().IsEqualTo(1);

    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "OrderPerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();

    // All event types should be handled
    await Assert.That(runnerSource).Contains("OrderCreatedEvent");
    await Assert.That(runnerSource).Contains("OrderUpdatedEvent");
    await Assert.That(runnerSource).Contains("OrderDeletedEvent");
    await Assert.That(runnerSource).Contains("OrderPurgedEvent");

    // Should have ModelAction handling for Delete and Purge
    await Assert.That(runnerSource).Contains("ModelAction.Delete");
    await Assert.That(runnerSource).Contains("ModelAction.Purge");
  }

  /// <summary>
  /// Tests that IPerspectiveWithActionsFor with multiple event types generates correctly.
  /// Uses IPerspectiveWithActionsFor<TModel, TEvent1, TEvent2> variant.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_IPerspectiveWithActionsFor_MultipleEventVariant_GeneratesRunnerAsync() {
    // Arrange - Using IPerspectiveWithActionsFor<TModel, TEvent1, TEvent2>
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;

namespace TestNamespace {
  public record OrderCancelledEvent : IEvent {
    [StreamId]
    public Guid OrderId { get; init; }
  }

  public record OrderArchivedEvent : IEvent {
    [StreamId]
    public Guid OrderId { get; init; }
  }

  public record OrderModel {
    [StreamId]
    public Guid OrderId { get; init; }
    public string Status { get; init; } = "";
  }

  public class OrderPerspective : IPerspectiveWithActionsFor<OrderModel, OrderCancelledEvent, OrderArchivedEvent> {
    public ApplyResult<OrderModel> Apply(OrderModel currentData, OrderCancelledEvent @event) {
      return ApplyResult<OrderModel>.Delete();
    }

    public ApplyResult<OrderModel> Apply(OrderModel currentData, OrderArchivedEvent @event) {
      return ApplyResult<OrderModel>.Purge();
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);

    // Assert - Should generate runner handling both events
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);
    await Assert.That(result.GeneratedTrees).Count().IsEqualTo(1);

    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "OrderPerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();
    await Assert.That(runnerSource).Contains("OrderCancelledEvent");
    await Assert.That(runnerSource).Contains("OrderArchivedEvent");
  }

  /// <summary>
  /// Tests that the generated runner correctly handles Delete action flow.
  /// Delete should: keep model (with DeletedAt set by perspective), set action to Delete.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_DeleteAction_GeneratesCorrectHandlingAsync() {
    // Arrange
    const string source = @"
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;

namespace TestNamespace {
  public record OrderDeletedEvent : IEvent {
    [StreamId]
    public Guid OrderId { get; init; }
  }

  public record OrderModel {
    [StreamId]
    public Guid OrderId { get; init; }
    public DateTimeOffset? DeletedAt { get; init; }
  }

  public class OrderPerspective : IPerspectiveWithActionsFor<OrderModel, OrderDeletedEvent> {
    public ApplyResult<OrderModel> Apply(OrderModel currentData, OrderDeletedEvent @event) {
      return ApplyResult<OrderModel>.Delete();
    }
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);

    // Assert
    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "OrderPerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();

    // Should have Delete case handling
    await Assert.That(runnerSource).Contains("case global::Whizbang.Core.Perspectives.ModelAction.Delete:");
  }

  /// <summary>
  /// Tests that the generated runner correctly handles Purge action flow.
  /// Purge should: set pendingPurge = true, set model to null for hard delete.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_PurgeAction_GeneratesCorrectHandlingAsync() {
    // Arrange
    const string source = @"
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;

namespace TestNamespace {
  public record OrderPurgedEvent : IEvent {
    [StreamId]
    public Guid OrderId { get; init; }
  }

  public record OrderModel {
    [StreamId]
    public Guid OrderId { get; init; }
  }

  public class OrderPerspective : IPerspectiveWithActionsFor<OrderModel, OrderPurgedEvent> {
    public ApplyResult<OrderModel> Apply(OrderModel currentData, OrderPurgedEvent @event) {
      return ApplyResult<OrderModel>.Purge();
    }
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);

    // Assert
    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "OrderPerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();

    // Should have Purge case handling with pendingPurge flag
    await Assert.That(runnerSource).Contains("case global::Whizbang.Core.Perspectives.ModelAction.Purge:");
    await Assert.That(runnerSource).Contains("pendingPurge = true");
  }

  /// <summary>
  /// Tests that IPerspectiveWithActionsFor-only class (no IPerspectiveFor) generates correctly.
  /// This ensures the interface works standalone without requiring IPerspectiveFor.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_IPerspectiveWithActionsForOnly_GeneratesRunnerAsync() {
    // Arrange - Class implementing only IPerspectiveWithActionsFor (no IPerspectiveFor)
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;

namespace TestNamespace {
  public record OrderCreatedEvent : IEvent {
    [StreamId]
    public Guid OrderId { get; init; }
    public decimal Amount { get; init; }
  }

  public record OrderUpdatedEvent : IEvent {
    [StreamId]
    public Guid OrderId { get; init; }
    public string NewStatus { get; init; } = "";
  }

  public record OrderDeletedEvent : IEvent {
    [StreamId]
    public Guid OrderId { get; init; }
  }

  public record OrderModel {
    [StreamId]
    public Guid OrderId { get; init; }
    public string Status { get; init; } = "";
    public decimal Amount { get; init; }
  }

  public class OrderPerspective :
    IPerspectiveWithActionsFor<OrderModel, OrderCreatedEvent>,
    IPerspectiveWithActionsFor<OrderModel, OrderUpdatedEvent>,
    IPerspectiveWithActionsFor<OrderModel, OrderDeletedEvent> {

    public ApplyResult<OrderModel> Apply(OrderModel currentData, OrderCreatedEvent @event) {
      // Implicit conversion from TModel to ApplyResult<TModel>
      return new OrderModel { OrderId = @event.OrderId, Status = "Created", Amount = @event.Amount };
    }

    public ApplyResult<OrderModel> Apply(OrderModel currentData, OrderUpdatedEvent @event) {
      // Explicit Update factory method
      return ApplyResult<OrderModel>.Update(currentData with { Status = @event.NewStatus });
    }

    public ApplyResult<OrderModel> Apply(OrderModel currentData, OrderDeletedEvent @event) {
      return ApplyResult<OrderModel>.Delete();
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);

    // Assert - Should generate runner for IPerspectiveWithActionsFor-only class
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);
    await Assert.That(result.GeneratedTrees).Count().IsEqualTo(1);

    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "OrderPerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();
    await Assert.That(runnerSource).Contains("OrderCreatedEvent");
    await Assert.That(runnerSource).Contains("OrderUpdatedEvent");
    await Assert.That(runnerSource).Contains("OrderDeletedEvent");
  }

  #endregion

  #region IScopeEvent / IPerspectiveScopeFor Tests

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithIScopeEvent_GeneratesScopeHandlingCodeAsync() {
    // Arrange - Event implements IScopeEvent
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;

namespace TestNamespace {
  public record TenantChangedEvent : IScopeEvent {
    public string OrderId { get; init; } = "";
    public PerspectiveScope Scope { get; init; } = new();
  }

  public record OrderReadModel {
    [StreamId]
    public string OrderId { get; init; } = "";
  }

  public class OrderPerspective : IPerspectiveFor<OrderReadModel, TenantChangedEvent> {
    public OrderReadModel Apply(OrderReadModel currentData, TenantChangedEvent @event) {
      return currentData;
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);

    // Assert - scope handling code generated
    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "OrderPerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();
    await Assert.That(runnerSource).Contains("IScopeEvent scopeEvent");
    await Assert.That(runnerSource).Contains("scopeChanged = true");
    await Assert.That(runnerSource).Contains("forceUpdateScope");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithIPerspectiveScopeFor_GeneratesApplyScopeCallAsync() {
    // Arrange - Perspective implements IPerspectiveScopeFor
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;

namespace TestNamespace {
  public record TenantChangedEvent : IScopeEvent {
    public string OrderId { get; init; } = "";
    public PerspectiveScope Scope { get; init; } = new();
  }

  public record OrderReadModel {
    [StreamId]
    public string OrderId { get; init; } = "";
  }

  public class OrderPerspective :
    IPerspectiveFor<OrderReadModel, TenantChangedEvent>,
    IPerspectiveScopeFor<OrderReadModel> {

    public OrderReadModel Apply(OrderReadModel currentData, TenantChangedEvent @event) {
      return currentData;
    }

    public PerspectiveScope ApplyScope(PerspectiveScope currentScope, PerspectiveScope proposedScope) {
      return proposedScope;
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);

    // Assert - ApplyScope call generated
    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "OrderPerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();
    await Assert.That(runnerSource).Contains("IPerspectiveScopeFor");
    await Assert.That(runnerSource).Contains("ApplyScope");
    await Assert.That(runnerSource).Contains("scopeChanged = true");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithIScopeEventWithoutIPerspectiveScopeFor_UsesDirectScopeAsync() {
    // Arrange - Event is IScopeEvent but perspective doesn't implement IPerspectiveScopeFor
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;

namespace TestNamespace {
  public record TenantChangedEvent : IScopeEvent {
    public string OrderId { get; init; } = "";
    public PerspectiveScope Scope { get; init; } = new();
  }

  public record OrderReadModel {
    [StreamId]
    public string OrderId { get; init; } = "";
  }

  public class OrderPerspective : IPerspectiveFor<OrderReadModel, TenantChangedEvent> {
    public OrderReadModel Apply(OrderReadModel currentData, TenantChangedEvent @event) {
      return currentData;
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);

    // Assert - Direct scope assignment, no ApplyScope call
    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "OrderPerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();
    await Assert.That(runnerSource).Contains("IScopeEvent scopeEvent");
    await Assert.That(runnerSource).Contains("lastScope = proposedScope");
    await Assert.That(runnerSource).DoesNotContain("ApplyScope");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithIScopeEventAndIPerspectiveFor_GeneratesBothCallsAsync() {
    // Arrange - Both Apply and scope handling should be generated
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;

namespace TestNamespace {
  public record OrderCreatedEvent : IEvent {
    public string OrderId { get; init; } = "";
  }

  public record TenantChangedEvent : IScopeEvent {
    public string OrderId { get; init; } = "";
    public PerspectiveScope Scope { get; init; } = new();
  }

  public record OrderReadModel {
    [StreamId]
    public string OrderId { get; init; } = "";
  }

  public class OrderPerspective :
    IPerspectiveFor<OrderReadModel, OrderCreatedEvent, TenantChangedEvent>,
    IPerspectiveScopeFor<OrderReadModel> {

    public OrderReadModel Apply(OrderReadModel currentData, OrderCreatedEvent @event) {
      return currentData;
    }

    public OrderReadModel Apply(OrderReadModel currentData, TenantChangedEvent @event) {
      return currentData;
    }

    public PerspectiveScope ApplyScope(PerspectiveScope currentScope, PerspectiveScope proposedScope) {
      return proposedScope;
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);

    // Assert - Both Apply cases and scope handling generated
    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "OrderPerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();
    // Apply cases for both events
    await Assert.That(runnerSource).Contains("OrderCreatedEvent");
    await Assert.That(runnerSource).Contains("TenantChangedEvent");
    // Scope handling
    await Assert.That(runnerSource).Contains("IScopeEvent scopeEvent");
    await Assert.That(runnerSource).Contains("ApplyScope");
    await Assert.That(runnerSource).Contains("forceUpdateScope");
  }

  #endregion

  #region Split Storage Mode Tests

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_SplitModeRecordModel_StripsPhysicalFieldsWithExpressionAsync() {
    // Arrange - record model with [PerspectiveStorage(Split)]: physical fields are stripped
    // from the JSONB payload using an immutable 'with' expression
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;

namespace TestNamespace {
  public class ProductUpdatedEvent : IEvent {
    public Guid Id { get; set; }
    public string Status { get; set; } = "";
  }

  [PerspectiveStorage(FieldStorageMode.Split)]
  public record ProductModel {
    [StreamId]
    public Guid Id { get; init; }

    [PhysicalField]
    public string Status { get; init; } = "";

    [VectorField(1536)]
    public float[]? Embedding { get; init; }
  }

  public class ProductPerspective : IPerspectiveFor<ProductModel, ProductUpdatedEvent> {
    public ProductModel Apply(ProductModel currentData, ProductUpdatedEvent @event) {
      return currentData with { Status = @event.Status };
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);

    // Assert - split mode with a record uses a 'with' expression to strip physical fields
    await Assert.That(result.GeneratedTrees).Count().IsEqualTo(1);

    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "ProductPerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();
    await Assert.That(runnerSource).Contains("model = model with {");
    await Assert.That(runnerSource).Contains("Status = default!");
    await Assert.That(runnerSource).Contains("Embedding = System.Array.Empty<float>()");
    await Assert.That(runnerSource).Contains("UpsertWithPhysicalFieldsAsync");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_SplitModeClassModel_StripsPhysicalFieldsByMutationAsync() {
    // Arrange - class (non-record) model with [PerspectiveStorage(Split)]: physical fields are
    // stripped by mutating the model in place (no 'with' expression available)
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;

namespace TestNamespace {
  public class ProductUpdatedEvent : IEvent {
    public Guid Id { get; set; }
    public string Status { get; set; } = "";
  }

  [PerspectiveStorage(FieldStorageMode.Split)]
  public class ProductModel {
    [StreamId]
    public Guid Id { get; set; }

    [PhysicalField]
    public string Status { get; set; } = "";

    [VectorField(1536)]
    public float[]? Embedding { get; set; }
  }

  public class ProductPerspective : IPerspectiveFor<ProductModel, ProductUpdatedEvent> {
    public ProductModel Apply(ProductModel currentData, ProductUpdatedEvent @event) {
      currentData.Status = @event.Status;
      return currentData;
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);

    // Assert - split mode with a class mutates properties directly
    await Assert.That(result.GeneratedTrees).Count().IsEqualTo(1);

    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "ProductPerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();
    await Assert.That(runnerSource).Contains("model.Status = default!;");
    await Assert.That(runnerSource).Contains("model.Embedding = System.Array.Empty<float>();");
    await Assert.That(runnerSource).DoesNotContain("model = model with {");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_JsonOnlyMode_DoesNotStripPhysicalFieldsAsync() {
    // Arrange - explicit JsonOnly storage mode keeps physical fields in the JSONB payload
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;

namespace TestNamespace {
  public class ProductUpdatedEvent : IEvent {
    public Guid Id { get; set; }
    public string Status { get; set; } = "";
  }

  [PerspectiveStorage(FieldStorageMode.JsonOnly)]
  public class ProductModel {
    [StreamId]
    public Guid Id { get; set; }

    [PhysicalField]
    public string Status { get; set; } = "";
  }

  public class ProductPerspective : IPerspectiveFor<ProductModel, ProductUpdatedEvent> {
    public ProductModel Apply(ProductModel currentData, ProductUpdatedEvent @event) {
      currentData.Status = @event.Status;
      return currentData;
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);

    // Assert - physical field dictionary is still built, but no stripping happens
    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "ProductPerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();
    await Assert.That(runnerSource).Contains("UpsertWithPhysicalFieldsAsync");
    await Assert.That(runnerSource).DoesNotContain("model.Status = default!;");
    await Assert.That(runnerSource).DoesNotContain("model = model with {");
  }

  #endregion

  #region InheritScope Tests

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_InheritScopeWithOnCreate_UsesConfiguredFlagsAsync() {
    // Arrange - [InheritScope(OnCreate = Tenant | User)] should emit flag value 3
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;
using System;

namespace TestNamespace {
  public class OrderCreatedEvent : IEvent {
    public Guid Id { get; set; }
  }

  [InheritScope(OnCreate = ScopeFields.Tenant | ScopeFields.User)]
  public class OrderModel {
    [StreamId]
    public Guid Id { get; set; }
  }

  public class OrderPerspective : IPerspectiveFor<OrderModel, OrderCreatedEvent> {
    public OrderModel Apply(OrderModel currentData, OrderCreatedEvent @event) {
      return currentData;
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);

    // Assert - Tenant (1) | User (2) == 3
    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "OrderPerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();
    await Assert.That(runnerSource).Contains("_inheritScopeOnCreate = (global::Whizbang.Core.Lenses.ScopeFields)3;");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_InheritScopeWithoutOnCreate_DefaultsToTenantAsync() {
    // Arrange - [InheritScope] with only Always set: OnCreate falls back to the attribute
    // default (ScopeFields.Tenant == 1)
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;
using System;

namespace TestNamespace {
  public class OrderCreatedEvent : IEvent {
    public Guid Id { get; set; }
  }

  [InheritScope(Always = ScopeFields.User)]
  public class OrderModel {
    [StreamId]
    public Guid Id { get; set; }
  }

  public class OrderPerspective : IPerspectiveFor<OrderModel, OrderCreatedEvent> {
    public OrderModel Apply(OrderModel currentData, OrderCreatedEvent @event) {
      return currentData;
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);

    // Assert - attribute present without OnCreate override -> Tenant (1)
    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "OrderPerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();
    await Assert.That(runnerSource).Contains("_inheritScopeOnCreate = (global::Whizbang.Core.Lenses.ScopeFields)1;");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_NoInheritScope_DefaultsToAllFieldsAsync() {
    // Arrange - no [InheritScope] attribute: legacy behavior copies every scope field (63)
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;

namespace TestNamespace {
  public class OrderCreatedEvent : IEvent {
    public Guid Id { get; set; }
  }

  public class OrderModel {
    [StreamId]
    public Guid Id { get; set; }
  }

  public class OrderPerspective : IPerspectiveFor<OrderModel, OrderCreatedEvent> {
    public OrderModel Apply(OrderModel currentData, OrderCreatedEvent @event) {
      return currentData;
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);

    // Assert - ScopeFields.All == 63
    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "OrderPerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();
    await Assert.That(runnerSource).Contains("_inheritScopeOnCreate = (global::Whizbang.Core.Lenses.ScopeFields)63;");
  }

  #endregion

  #region Global Perspective Tests

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_GlobalPerspective_GeneratesRunnerAsync() {
    // Arrange - IGlobalPerspectiveFor<TModel, TPartitionKey, TEvent1> multi-stream perspective
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;

namespace TestNamespace {
  public class RegionSaleEvent : IEvent {
    [StreamId]
    public Guid Id { get; set; }
    public string Region { get; set; } = "";
  }

  public class RegionSummaryModel {
    [StreamId]
    public string Region { get; set; } = "";
    public int SaleCount { get; set; }
  }

  public class RegionSummaryPerspective : IGlobalPerspectiveFor<RegionSummaryModel, string, RegionSaleEvent> {
    public string GetPartitionKey(RegionSaleEvent eventData) {
      return eventData.Region;
    }

    public RegionSummaryModel Apply(RegionSummaryModel currentData, RegionSaleEvent eventData) {
      currentData.SaleCount++;
      return currentData;
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);

    // Assert - global perspective produces a runner; events come from type args after
    // TModel and TPartitionKey
    await Assert.That(result.GeneratedTrees).Count().IsEqualTo(1);

    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "RegionSummaryPerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();
    await Assert.That(runnerSource).Contains("class RegionSummaryPerspectiveRunner");
    await Assert.That(runnerSource).Contains("RegionSaleEvent");
    await Assert.That(runnerSource).Contains("RegionSummaryModel");
  }

  #endregion

  #region Inherited StreamId Tests

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_EventWithInheritedStreamId_GeneratesExtractStreamIdAsync() {
    // Arrange - the [StreamId] property lives on the event's base class; the generator must
    // walk the inheritance chain to find it
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;

namespace TestNamespace {
  public abstract class BaseSagaEvent : IEvent {
    [StreamId]
    public Guid SagaId { get; set; }
  }

  public class StepCompletedEvent : BaseSagaEvent {
    public string StepName { get; set; } = "";
  }

  public class SagaModel {
    [StreamId]
    public Guid SagaId { get; set; }
    public string LastStep { get; set; } = "";
  }

  public class SagaPerspective : IPerspectiveFor<SagaModel, StepCompletedEvent> {
    public SagaModel Apply(SagaModel currentData, StepCompletedEvent @event) {
      currentData.LastStep = @event.StepName;
      return currentData;
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);

    // Assert - ExtractStreamId is generated using the inherited SagaId property
    await Assert.That(result.GeneratedTrees).Count().IsEqualTo(1);

    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "SagaPerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();
    await Assert.That(runnerSource).Contains("ExtractStreamId");
    await Assert.That(runnerSource).Contains("@event.SagaId");
  }

  #endregion

  #region CreateEmptyModel direct construction (no reflection)

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_CreateEmptyModel_UsesDirectConstructionWithoutReflectionAsync() {
    // Arrange - the generated CreateEmptyModel must construct the model
    // directly instead of Activator.CreateInstance + PropertyInfo.SetValue
    // (reflection is banned and breaks AOT).
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace TestNamespace {
  public record WidgetCreatedEvent : IEvent {
    public Guid WidgetId { get; init; }
  }

  public record WidgetReadModel {
    [StreamId]
    public Guid WidgetId { get; init; }
    public string Status { get; init; } = "";
  }

  public class WidgetPerspective : IPerspectiveFor<WidgetReadModel, WidgetCreatedEvent> {
    public WidgetReadModel Apply(WidgetReadModel currentData, WidgetCreatedEvent @event) {
      return new WidgetReadModel { WidgetId = @event.WidgetId, Status = "Created" };
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);

    // Assert
    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "WidgetPerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();
    await Assert.That(runnerSource).DoesNotContain("Activator.CreateInstance")
      .Because("CreateEmptyModel must not use runtime activation; reflection breaks AOT.");
    await Assert.That(runnerSource).DoesNotContain(".GetProperty(")
      .Because("CreateEmptyModel must not assign the stream key via reflection.");
    await Assert.That(runnerSource).Contains("new global::TestNamespace.WidgetReadModel { WidgetId = streamId }");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_StronglyTypedStreamId_ConstructsViaFromFactoryAsync() {
    // Arrange - a model whose [StreamId] property is a strongly-typed id
    // struct with a static From(Guid) factory. The reflection-based
    // CreateEmptyModel threw ArgumentException at runtime ('System.Guid'
    // cannot be converted); direct construction must use the factory.
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace TestNamespace {
  public readonly struct WidgetId {
    public Guid Value { get; }
    private WidgetId(Guid value) { Value = value; }
    public static WidgetId From(Guid value) => new WidgetId(value);
  }

  public record WidgetCreatedEvent : IEvent {
    public Guid WidgetId { get; init; }
  }

  public record WidgetReadModel {
    [StreamId]
    public WidgetId WidgetId { get; init; }
    public string Status { get; init; } = "";
  }

  public class WidgetPerspective : IPerspectiveFor<WidgetReadModel, WidgetCreatedEvent> {
    public WidgetReadModel Apply(WidgetReadModel currentData, WidgetCreatedEvent @event) {
      return currentData with { Status = "Created" };
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);

    // Assert
    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "WidgetPerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();
    await Assert.That(runnerSource).Contains("global::TestNamespace.WidgetId.From(streamId)")
      .Because("strongly-typed stream ids must be constructed through their From(Guid) factory.");
    await Assert.That(runnerSource).DoesNotContain("Activator.CreateInstance");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_RequiredModelMembers_AreInitializedSoGeneratedCodeCompilesAsync() {
    // Arrange - required members must appear in the object initializer or the
    // generated runner will not compile in the consumer's build.
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace TestNamespace {
  public record WidgetCreatedEvent : IEvent {
    public Guid WidgetId { get; init; }
  }

  public record WidgetReadModel {
    [StreamId]
    public required Guid WidgetId { get; init; }
    public required string Name { get; init; }
  }

  public class WidgetPerspective : IPerspectiveFor<WidgetReadModel, WidgetCreatedEvent> {
    public WidgetReadModel Apply(WidgetReadModel currentData, WidgetCreatedEvent @event) {
      return currentData with { Name = "Created" };
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);

    // Assert
    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "WidgetPerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();
    await Assert.That(runnerSource).Contains("WidgetId = streamId");
    await Assert.That(runnerSource).Contains("Name = default!")
      .Because("other required members must be explicitly initialized (to default!, matching the old reflection behavior of leaving them unset).");
  }

  #endregion
}
