using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Minting;
using Whizbang.Core.Routing;
using Whizbang.Core.Transports;

namespace Whizbang.Core.Tests.Minting;

/// <summary>
/// Tests for <see cref="CompositeFactory"/> — the one splitter unifying the routing strategy's
/// group key, the count cap, and the byte budget. Callers hand constituents plus a family
/// builder and get back plans already split so that every plan's constituents share one
/// transport destination (same key ⇔ same destination) and respect the sender-side bounds.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Minting/CompositeFactory.cs</code-under-test>
public class CompositeFactoryTests {
  private sealed record ProbeConstituent(string Key, string Name, long Size);

  private sealed class ProbeComposite : CompositeEventBase {
    public IReadOnlyList<string> Names { get; init; } = [];
  }

  private static CompositeMintRequest<ProbeConstituent> _request(
      IReadOnlyList<ProbeConstituent> constituents,
      int? maxCount = null,
      long? maxBytes = null,
      bool withSizes = false) => new() {
        Constituents = constituents,
        GroupKey = CompositeGroupKey.FromKey<ProbeConstituent>(c => c.Key),
        BuildComposite = batch => new ProbeComposite { Names = [.. batch.Constituents.Select(c => c.Name)] },
        MaxConstituentsPerComposite = maxCount,
        MaxBytesPerComposite = maxBytes,
        ConstituentSizeBytes = withSizes ? c => c.Size : null,
      };

  [Test]
  public async Task Create_MixedGroupKeys_OneCompositePerGroupKeyAsync() {
    // A composite has ONE destination — constituents with different group keys must never share
    // a plan (the CoalesceShipWorker mixed-destination split, now owned by the factory).
    var factory = new CompositeFactory();
    var constituents = new List<ProbeConstituent> {
      new("topic-a", "a1", 1), new("topic-a", "a2", 1), new("topic-b", "b1", 1),
    };

    var plans = factory.Create(_request(constituents));

    await Assert.That(plans.Count).IsEqualTo(2);
    await Assert.That(plans[0].GroupKey).IsEqualTo("topic-a")
      .Because("groups surface in first-occurrence order — plan order is deterministic");
    await Assert.That(plans[0].Constituents.Select(c => c.Name).ToList()).IsEquivalentTo(["a1", "a2"]);
    await Assert.That(plans[1].GroupKey).IsEqualTo("topic-b");
    await Assert.That(plans[1].Constituents.Select(c => c.Name).ToList()).IsEquivalentTo(["b1"]);
  }

  [Test]
  public async Task Create_BuilderReceivesTheChunkAndItsKey_PlanCarriesTheBuiltCompositeAsync() {
    var factory = new CompositeFactory();
    var seenKeys = new List<string>();
    var request = new CompositeMintRequest<ProbeConstituent> {
      Constituents = [new("topic-a", "a1", 1)],
      GroupKey = CompositeGroupKey.FromKey<ProbeConstituent>(c => c.Key),
      BuildComposite = batch => {
        seenKeys.Add(batch.GroupKey ?? "<null>");
        return new ProbeComposite { Names = [.. batch.Constituents.Select(c => c.Name)] };
      },
    };

    var plans = factory.Create(request);

    await Assert.That(seenKeys).IsEquivalentTo(["topic-a"])
      .Because("the family builder sees the group key so it can stamp destination-derived state");
    var composite = (ProbeComposite)plans[0].Composite;
    await Assert.That(composite.Names).IsEquivalentTo(["a1"]);
  }

  [Test]
  public async Task Create_GroupLargerThanCountCap_SplitsIntoChunksPreservingOrderAsync() {
    // Five constituents at a cap of two → plans of 2 + 2 + 1 (the RedeliveryPump chunk bound).
    var factory = new CompositeFactory();
    var constituents = Enumerable.Range(1, 5)
      .Select(i => new ProbeConstituent("stream-1", $"e{i}", 1)).ToList();

    var plans = factory.Create(_request(constituents, maxCount: 2));

    await Assert.That(plans.Count).IsEqualTo(3);
    await Assert.That(plans[0].Constituents.Select(c => c.Name).ToList()).IsEquivalentTo(["e1", "e2"]);
    await Assert.That(plans[1].Constituents.Select(c => c.Name).ToList()).IsEquivalentTo(["e3", "e4"]);
    await Assert.That(plans[2].Constituents.Select(c => c.Name).ToList()).IsEquivalentTo(["e5"]);
  }

  [Test]
  public async Task Create_ByteBudgetCrossed_FlushesBelowTheCountCapAsync() {
    // Each constituent carries ~102 bytes against a 100-byte budget: every one crosses the budget
    // and flushes alone — a count-only bound would build one three-constituent plan here (the
    // RedeliveryPump byte-budget behavior, verbatim).
    var factory = new CompositeFactory();
    var constituents = Enumerable.Range(1, 3)
      .Select(i => new ProbeConstituent("stream-1", $"e{i}", 102)).ToList();

    var plans = factory.Create(_request(constituents, maxCount: 500, maxBytes: 100, withSizes: true));

    await Assert.That(plans.Count).IsEqualTo(3)
      .Because("large-bodied constituents must flush by BYTES below the count bound — a count-only "
             + "split builds composites that exhaust memory or exceed the broker message-size limit");
    foreach (var plan in plans) {
      await Assert.That(plan.Constituents.Count).IsEqualTo(1);
    }
  }

  [Test]
  public async Task Create_SingleConstituentLargerThanBudget_StillPlansAloneAsync() {
    var factory = new CompositeFactory();

    var plans = factory.Create(_request(
      [new("stream-1", "oversized", 10_000)], maxBytes: 10, withSizes: true));

    await Assert.That(plans.Count).IsEqualTo(1)
      .Because("the budget bounds grouping — it never silently drops a constituent");
    await Assert.That(plans[0].Constituents.Count).IsEqualTo(1);
  }

  [Test]
  public async Task Create_ByteBudgetWithoutSizeSelector_ThrowsAsync() {
    // A byte budget with no size accounting would silently disable the budget — fail loudly.
    var factory = new CompositeFactory();

    await Assert.That(() => factory.Create(_request(
        [new("k", "a", 1)], maxBytes: 100, withSizes: false)))
      .Throws<InvalidOperationException>();
  }

  [Test]
  public async Task Create_EmptyConstituents_ReturnsEmptyAsync() {
    var factory = new CompositeFactory();

    var plans = factory.Create(_request([]));

    await Assert.That(plans).IsEmpty();
  }

  [Test]
  public async Task Create_FromStrategy_GroupKeyIsTheStrategysCompositeGroupKeyAsync() {
    // The canonical key source (spec resolution: the splitter takes the strategy as its input from
    // day one): constituents whose types route to the same destination share a plan; different
    // destinations split — same key ⇔ same destination, delegated to phase 3's
    // IOutboxRoutingStrategy.GetCompositeGroupKey.
    var factory = new CompositeFactory();
    IOutboxRoutingStrategy strategy = new _typeNameOutboxStrategy();
    var ownedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var constituents = new List<(Type Type, string Name)> {
      (typeof(_alphaEvent), "alpha-1"),
      (typeof(_alphaEvent), "alpha-2"),
      (typeof(_betaEvent), "beta-1"),
    };
    var request = new CompositeMintRequest<(Type Type, string Name)> {
      Constituents = constituents,
      GroupKey = CompositeGroupKey.FromStrategy<(Type Type, string Name)>(
        strategy, ownedDomains, MessageKind.Event, c => c.Type),
      BuildComposite = batch => new ProbeComposite { Names = [.. batch.Constituents.Select(c => c.Name)] },
    };

    var plans = factory.Create(request);

    await Assert.That(plans.Count).IsEqualTo(2);
    await Assert.That(plans[0].GroupKey).IsEqualTo(
        strategy.GetCompositeGroupKey(typeof(_alphaEvent), ownedDomains, MessageKind.Event))
      .Because("the plan's key IS the strategy's composite group key — route, split, subscribe "
             + "and provision stay projections of GetDestination");
    await Assert.That(plans[0].Constituents.Select(c => c.Name).ToList()).IsEquivalentTo(["alpha-1", "alpha-2"]);
    await Assert.That(plans[1].Constituents.Select(c => c.Name).ToList()).IsEquivalentTo(["beta-1"]);
  }

  [Test]
  public async Task Create_NullBuiltComposite_ThrowsAsync() {
    // Plans are machine-built; a null composite is a producer bug, never data.
    var factory = new CompositeFactory();
    var request = new CompositeMintRequest<ProbeConstituent> {
      Constituents = [new("k", "a", 1)],
      GroupKey = CompositeGroupKey.FromKey<ProbeConstituent>(c => c.Key),
      BuildComposite = _ => null!,
    };

    await Assert.That(() => factory.Create(request)).Throws<InvalidOperationException>();
  }

  [Test]
  public async Task Create_NullRequest_ThrowsAsync() {
    var factory = new CompositeFactory();

    await Assert.That(() => factory.Create<ProbeConstituent>(null!)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Create_NonPositiveCountCap_ThrowsAsync() {
    var factory = new CompositeFactory();

    await Assert.That(() => factory.Create(_request([new("k", "a", 1)], maxCount: 0)))
      .Throws<ArgumentOutOfRangeException>();
  }

  // ── fakes ─────────────────────────────────────────────────────────────────

  private sealed record _alphaEvent : IEvent;

  private sealed record _betaEvent : IEvent;

  /// <summary>Routes each type to a destination named after the type — distinct types, distinct
  /// destinations — so the default GetCompositeGroupKey projection is observable.</summary>
  private sealed class _typeNameOutboxStrategy : IOutboxRoutingStrategy {
    public TransportDestination GetDestination(Type messageType, IReadOnlySet<string> ownedDomains, MessageKind kind)
      => new($"topic-{messageType.Name.ToLowerInvariant()}");
  }
}
