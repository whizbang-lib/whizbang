using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// The declaration that a JSON-only field carries its own index.
/// </summary>
/// <remarks>
/// <para>
/// A perspective stores its model as a document, and the GIN index on that document answers
/// containment and nothing else, so a field filtered by a range or used as a sort key is read by
/// scanning. Promoting it to a physical column fixes that and costs a column, a hydration path and a
/// schema change. An index on the extraction fixes it and costs only an index, which is why it is
/// worth declaring separately.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/physical-fields</docs>
public class IndexedAttributeTests {
  /// <summary>
  /// A range and an ordering need a btree, which is the common case and so the default.
  /// </summary>
  [Test]
  public async Task DefaultKind_IsBtreeAsync() {
    var attribute = new IndexedAttribute();

    await Assert.That(attribute.Kind).IsEqualTo(IndexKinds.Btree);
  }

  /// <summary>The kinds combine, because a field can be filtered by range and by substring both.</summary>
  [Test]
  public async Task KindsCombineAsync() {
    var attribute = new IndexedAttribute(IndexKinds.Btree | IndexKinds.Trigram);

    await Assert.That(attribute.Kind.HasFlag(IndexKinds.Btree)).IsTrue();
    await Assert.That(attribute.Kind.HasFlag(IndexKinds.Trigram)).IsTrue();
  }

  /// <summary>
  /// Repeatable, so a reader can see one reason per line where that is clearer than a combination.
  /// </summary>
  [Test]
  public async Task IsRepeatableOnAPropertyAsync() {
    var usage = typeof(IndexedAttribute)
      .GetCustomAttributes(typeof(AttributeUsageAttribute), inherit: false)
      .Cast<AttributeUsageAttribute>()
      .Single();

    await Assert.That(usage.AllowMultiple).IsTrue();
    await Assert.That(usage.ValidOn).IsEqualTo(AttributeTargets.Property);
  }

  /// <summary>
  /// Nothing is indexed by an empty declaration. A kind of none would otherwise read as "indexed"
  /// while emitting no index, which is the kind of silence this whole area exists to remove.
  /// </summary>
  [Test]
  public async Task NoneIsNotAnIndexAsync() {
    await Assert.That(new IndexedAttribute(IndexKinds.None).Kind).IsEqualTo(IndexKinds.None);
    await Assert.That(IndexKinds.None.HasFlag(IndexKinds.Btree)).IsFalse();
  }

  /// <summary>
  /// The perspective-level form, for a read model queried every way, so that every eligible field is
  /// indexed without a decoration per property.
  /// </summary>
  [Test]
  public async Task IndexAllFields_AppliesToAModelAndCarriesAKindAsync() {
    var attribute = new IndexAllFieldsAttribute();

    await Assert.That(attribute.Kind).IsEqualTo(IndexKinds.Btree);

    var usage = typeof(IndexAllFieldsAttribute)
      .GetCustomAttributes(typeof(AttributeUsageAttribute), inherit: false)
      .Cast<AttributeUsageAttribute>()
      .Single();

    await Assert.That(usage.AllowMultiple).IsFalse();
    await Assert.That(usage.ValidOn.HasFlag(AttributeTargets.Class)).IsTrue();
  }
}
