using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// A composite's children get ids derived from the composite's own id, the child's ordinal and its type,
/// so expanding the same composite twice (a re-offer whose first expansion is still committing, or a
/// second instance after a lease lapsed) produces the same rows and the inbox primary key absorbs the
/// repeat (#737). Fresh ids per expansion were how one composite landed fifteen copies of its inner events.
/// </summary>
/// <docs>fundamentals/messaging/composite-events#deterministic-child-ids</docs>
[Category("Unit")]
public class CompositeChildIdentityTests {
  private static readonly Guid _composite = Guid.CreateVersion7();
  private const string TYPE = "Contracts.Job.RowAddedEvent, Contracts";

  [Test]
  public async Task Derive_SameInputs_ProduceTheSameIdAsync() {
    var first = CompositeChildIdentity.Derive(_composite, 3, TYPE);
    var second = CompositeChildIdentity.Derive(_composite, 3, TYPE);

    await Assert.That(second).IsEqualTo(first)
      .Because("a second expansion of the same composite must mint the same child id, or the inbox cannot recognize the repeat");
  }

  [Test]
  public async Task Derive_DifferentOrdinals_ProduceDifferentIdsAsync() {
    await Assert.That(CompositeChildIdentity.Derive(_composite, 0, TYPE))
      .IsNotEqualTo(CompositeChildIdentity.Derive(_composite, 1, TYPE))
      .Because("two children of one composite are two rows");
  }

  [Test]
  public async Task Derive_DifferentTypes_ProduceDifferentIdsAsync() {
    await Assert.That(CompositeChildIdentity.Derive(_composite, 0, TYPE))
      .IsNotEqualTo(CompositeChildIdentity.Derive(_composite, 0, "Contracts.Job.RowRemovedEvent, Contracts"));
  }

  [Test]
  public async Task Derive_DifferentComposites_ProduceDifferentIdsAsync() {
    await Assert.That(CompositeChildIdentity.Derive(_composite, 0, TYPE))
      .IsNotEqualTo(CompositeChildIdentity.Derive(Guid.CreateVersion7(), 0, TYPE))
      .Because("the same inner event delivered by two different composites is two deliveries; only a repeat of one composite is a repeat");
  }

  [Test]
  public async Task Derive_IsVersion7WithTheRfcVariant_AndKeepsTheCompositesTimePrefixAsync() {
    var id = CompositeChildIdentity.Derive(_composite, 7, TYPE);
    var bytes = id.ToByteArray(bigEndian: true);
    var source = _composite.ToByteArray(bigEndian: true);
    var version = bytes[6] >> 4;
    var variant = bytes[8] >> 6;
    var sameTimePrefix = bytes.AsSpan(0, 10).SequenceEqual(source.AsSpan(0, 10));

    await Assert.That(version).IsEqualTo(7).Because("consumers that require time-ordered ids accept a derived id like a minted one");
    await Assert.That(variant).IsEqualTo(0b10).Because("RFC 9562 variant");
    await Assert.That(sameTimePrefix).IsTrue()
      .Because("the child keeps its composite's millisecond and counter, so it sorts where its composite sorts");
  }

  [Test]
  public async Task Derive_ChildrenSortInTheirOrderWithinTheCompositeAsync() {
    // Children of one composite that land on one stream must apply in the order the composite lists them.
    var ids = Enumerable.Range(0, 100).Select(i => CompositeChildIdentity.Derive(_composite, i, i % 3 == 0 ? "Z.Type" : TYPE)).ToArray();

    var sorted = ids.OrderBy(id => id.ToString("N"), StringComparer.Ordinal).ToArray();

    await Assert.That(sorted.SequenceEqual(ids)).IsTrue();
  }

  [Test]
  public async Task Derive_ChildrenOfConsecutiveComposites_SortInCompositeOrderAsync() {
    var first = (Guid)TrackedGuid.New();
    var second = (Guid)TrackedGuid.New();

    var lastChildOfFirst = CompositeChildIdentity.Derive(first, 99, TYPE);
    var firstChildOfSecond = CompositeChildIdentity.Derive(second, 0, TYPE);

    await Assert.That(string.CompareOrdinal(firstChildOfSecond.ToString("N"), lastChildOfFirst.ToString("N"))).IsGreaterThan(0);
  }

  [Test]
  public async Task Derive_RejectsAnEmptyCompositeIdAsync() {
    await Assert.That(() => CompositeChildIdentity.Derive(Guid.Empty, 0, TYPE)).Throws<ArgumentException>();
  }

  [Test]
  public async Task Derive_RejectsANegativeOrdinalAsync() {
    await Assert.That(() => CompositeChildIdentity.Derive(_composite, -1, TYPE)).Throws<ArgumentOutOfRangeException>();
  }

  [Test]
  public async Task Derive_RejectsAMissingTypeNameAsync() {
    await Assert.That(() => CompositeChildIdentity.Derive(_composite, 0, null!)).Throws<ArgumentNullException>();
  }
}
