using System.Collections.Generic;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Generated;
using Whizbang.Core.Messaging;
using Whizbang.Core.Registry;

namespace Whizbang.Core.Tests.Generated;

/// <summary>
/// Locks the slice 1 redesign (2026-05-06) — <see cref="WhizbangReceptorRegistryQuery"/>
/// reads aggregated <see cref="ReceptorRegistryContribution"/> records from
/// <see cref="AssemblyRegistry{T}"/>, which each consuming assembly populates at load
/// time via <c>[ModuleInitializer]</c>. The original design had each assembly's
/// generator emit a duplicate static class with HashSets baked in; the in-Core adapter
/// always saw Whizbang.Core's empty copy in production, silently dropping every
/// message at the receive-boundary drop-gate. This test cohort guards against any
/// regression that re-introduces the duplicate-static-class pattern OR breaks the
/// AssemblyRegistry-based aggregation.
/// </summary>
[NotInParallel("ReceptorRegistryQueryAggregation")]
public class WhizbangReceptorRegistryQueryAggregationTests {

  [Before(Test)]
  public void ResetRegistry() {
    AssemblyRegistry<ReceptorRegistryContribution>.ClearForTesting();
    WhizbangReceptorRegistryQuery.ClearCacheForTesting();
  }

  [After(Test)]
  public void TeardownRegistry() {
    AssemblyRegistry<ReceptorRegistryContribution>.ClearForTesting();
    WhizbangReceptorRegistryQuery.ClearCacheForTesting();
  }

  // ===== Single-contribution lookups =====

  // Contributions are stored in the form the source generator emits — dotted, no assembly
  // qualifier (e.g., "MyApp.SomeEvent"). At query time, _normalizeTypeName strips any
  // assembly qualifier supplied by Type.AssemblyQualifiedName. Tests below mix both forms
  // on the QUERY side to exercise normalization.

  [Test]
  public async Task SingleContribution_HasAnyConsumer_FindsTheTypeAsync() {
    AssemblyRegistry<ReceptorRegistryContribution>.Register(new ReceptorRegistryContribution {
      AnyConsumerTypes = ["MyApp.SomeEvent"],
      InboxHandlerTypes = [],
      StageTypes = new Dictionary<LifecycleStage, IReadOnlyCollection<string>>(),
    });

    await Assert.That(WhizbangReceptorRegistryQuery.HasAnyConsumer("MyApp.SomeEvent, MyApp.Contracts")).IsTrue()
      .Because("Qualified runtime name must normalize to the stored unqualified form.");
    await Assert.That(WhizbangReceptorRegistryQuery.HasAnyConsumer("MyApp.SomeEvent")).IsTrue();
    await Assert.That(WhizbangReceptorRegistryQuery.HasAnyConsumer("Other.Type, Other")).IsFalse();
  }

  [Test]
  public async Task SingleContribution_HasInboxHandler_FindsTheTypeAsync() {
    AssemblyRegistry<ReceptorRegistryContribution>.Register(new ReceptorRegistryContribution {
      AnyConsumerTypes = ["MyApp.Cmd"],
      InboxHandlerTypes = ["MyApp.Cmd"],
      StageTypes = new Dictionary<LifecycleStage, IReadOnlyCollection<string>>(),
    });

    await Assert.That(WhizbangReceptorRegistryQuery.HasInboxHandler("MyApp.Cmd, MyApp")).IsTrue();
    await Assert.That(WhizbangReceptorRegistryQuery.HasInboxHandler("Other.Cmd, Other")).IsFalse();
  }

  [Test]
  public async Task SingleContribution_HasReceptors_PerStageLookupAsync() {
    AssemblyRegistry<ReceptorRegistryContribution>.Register(new ReceptorRegistryContribution {
      AnyConsumerTypes = ["MyApp.Evt"],
      InboxHandlerTypes = [],
      StageTypes = new Dictionary<LifecycleStage, IReadOnlyCollection<string>> {
        [LifecycleStage.PreInboxInline] = ["MyApp.Evt"],
      },
    });

    await Assert.That(WhizbangReceptorRegistryQuery.HasReceptors(LifecycleStage.PreInboxInline, "MyApp.Evt, MyApp")).IsTrue();
    await Assert.That(WhizbangReceptorRegistryQuery.HasReceptors(LifecycleStage.PostInboxInline, "MyApp.Evt, MyApp")).IsFalse();
    await Assert.That(WhizbangReceptorRegistryQuery.HasReceptors(LifecycleStage.PreInboxInline, "Other.Evt, Other")).IsFalse();
  }

  // ===== Multi-assembly aggregation (the load-bearing slice 1 invariant) =====

  [Test]
  public async Task TwoContributions_HasAnyConsumer_UnionsBothAsync() {
    // Simulates two consuming assemblies — e.g., a consumer.UserService + a consumer.BffService —
    // each contributing distinct receptor types via their respective ModuleInitializers.
    // The aggregator MUST see the union; this is the regression that broke chat in a consumer
    // when each assembly emitted its own static-with-HashSets and Whizbang.Core's adapter
    // saw only the empty Core copy.
    AssemblyRegistry<ReceptorRegistryContribution>.Register(new ReceptorRegistryContribution {
      AnyConsumerTypes = ["UserService.AccountCreated"],
      InboxHandlerTypes = [],
      StageTypes = new Dictionary<LifecycleStage, IReadOnlyCollection<string>>(),
    });
    AssemblyRegistry<ReceptorRegistryContribution>.Register(new ReceptorRegistryContribution {
      AnyConsumerTypes = ["BffService.PermissionsMaterialized"],
      InboxHandlerTypes = [],
      StageTypes = new Dictionary<LifecycleStage, IReadOnlyCollection<string>>(),
    });

    await Assert.That(WhizbangReceptorRegistryQuery.HasAnyConsumer("UserService.AccountCreated, UserService")).IsTrue()
      .Because("Aggregating registry must see the first assembly's contribution.");
    await Assert.That(WhizbangReceptorRegistryQuery.HasAnyConsumer("BffService.PermissionsMaterialized, BffService")).IsTrue()
      .Because("Aggregating registry must see the second assembly's contribution.");
  }

  [Test]
  public async Task TwoContributions_HasReceptors_UnionsPerStageAsync() {
    AssemblyRegistry<ReceptorRegistryContribution>.Register(new ReceptorRegistryContribution {
      AnyConsumerTypes = [],
      InboxHandlerTypes = [],
      StageTypes = new Dictionary<LifecycleStage, IReadOnlyCollection<string>> {
        [LifecycleStage.PreInboxInline] = ["AssemblyA.Evt"],
      },
    });
    AssemblyRegistry<ReceptorRegistryContribution>.Register(new ReceptorRegistryContribution {
      AnyConsumerTypes = [],
      InboxHandlerTypes = [],
      StageTypes = new Dictionary<LifecycleStage, IReadOnlyCollection<string>> {
        [LifecycleStage.PreInboxInline] = ["AssemblyB.Evt"],
        [LifecycleStage.PostInboxDetached] = ["AssemblyB.Cmd"],
      },
    });

    await Assert.That(WhizbangReceptorRegistryQuery.HasReceptors(LifecycleStage.PreInboxInline, "AssemblyA.Evt, A")).IsTrue();
    await Assert.That(WhizbangReceptorRegistryQuery.HasReceptors(LifecycleStage.PreInboxInline, "AssemblyB.Evt, B")).IsTrue();
    await Assert.That(WhizbangReceptorRegistryQuery.HasReceptors(LifecycleStage.PostInboxDetached, "AssemblyB.Cmd, B")).IsTrue();
    await Assert.That(WhizbangReceptorRegistryQuery.HasReceptors(LifecycleStage.PostInboxDetached, "AssemblyA.Evt, A")).IsFalse()
      .Because("Cross-stage isolation: AssemblyA's PreInbox receptor must not appear under PostInboxDetached.");
  }

  // ===== Empty / missing registry =====

  [Test]
  public async Task NoContributions_AllLookupsReturnFalseAsync() {
    // ResetRegistry runs first via [Before(Test)] — registry is empty.
    await Assert.That(WhizbangReceptorRegistryQuery.HasAnyConsumer("Any.Type, Asm")).IsFalse();
    await Assert.That(WhizbangReceptorRegistryQuery.HasInboxHandler("Any.Type, Asm")).IsFalse();
    await Assert.That(WhizbangReceptorRegistryQuery.HasReceptors(LifecycleStage.PreInboxInline, "Any.Type, Asm")).IsFalse();
  }

  // ===== Cache invalidation on additional registrations =====

  [Test]
  public async Task NewContribution_AfterPriorQuery_ShowsUpInNextQueryAsync() {
    // Prime the cache with one contribution
    AssemblyRegistry<ReceptorRegistryContribution>.Register(new ReceptorRegistryContribution {
      AnyConsumerTypes = ["First.Evt"],
      InboxHandlerTypes = [],
      StageTypes = new Dictionary<LifecycleStage, IReadOnlyCollection<string>>(),
    });
    await Assert.That(WhizbangReceptorRegistryQuery.HasAnyConsumer("First.Evt, First")).IsTrue();
    await Assert.That(WhizbangReceptorRegistryQuery.HasAnyConsumer("Second.Evt, Second")).IsFalse();

    // Register another contribution — the count-based cache invalidation must pick it up
    AssemblyRegistry<ReceptorRegistryContribution>.Register(new ReceptorRegistryContribution {
      AnyConsumerTypes = ["Second.Evt"],
      InboxHandlerTypes = [],
      StageTypes = new Dictionary<LifecycleStage, IReadOnlyCollection<string>>(),
    });
    await Assert.That(WhizbangReceptorRegistryQuery.HasAnyConsumer("First.Evt, First")).IsTrue();
    await Assert.That(WhizbangReceptorRegistryQuery.HasAnyConsumer("Second.Evt, Second")).IsTrue()
      .Because("Cache must invalidate when AssemblyRegistry's contribution count changes.");
  }
}
