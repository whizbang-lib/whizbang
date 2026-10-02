using TUnit.Core;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Tests.Helpers;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// The declarations an app makes about how its stored perspective documents change when a model changes shape:
/// <see cref="StoredFormAttribute"/> on the property as it is now, <see cref="StoredFormRemovedAttribute"/> on the
/// model for a property that no longer exists, and <see cref="IStoredFormMigration{TModel}"/> for raw SQL.
/// </summary>
/// <docs>fundamentals/perspectives/stored-form-migrations</docs>
[Category("Core")]
[Category("Attributes")]
public class StoredFormAttributeTests {
  [Test]
  public async Task StoredForm_CarriesEachDeclaration_AndNothingByDefaultAsync() {
    var empty = new StoredFormAttribute();
    await Assert.That(empty.Previously).IsNull();
    await Assert.That(empty.PreviousName).IsNull();
    await Assert.That(empty.DefaultWhenMissing).IsNull();

    var declared = new StoredFormAttribute {
      Previously = typeof(int),
      PreviousName = "OldStatus",
      DefaultWhenMissing = 3,
    };
    await Assert.That(declared.Previously).IsEqualTo(typeof(int));
    await Assert.That(declared.PreviousName).IsEqualTo("OldStatus");
    await Assert.That(declared.DefaultWhenMissing).IsEqualTo(3);
  }

  [Test]
  public async Task StoredForm_GoesOnAPropertyOnceAsync() {
    var usage = AttributeTestHelpers.GetAttributeUsage<StoredFormAttribute>();
    await Assert.That(usage).IsNotNull();
    await Assert.That(usage!.ValidOn).IsEqualTo(AttributeTargets.Property);
    await Assert.That(usage.AllowMultiple).IsFalse();
    await Assert.That(typeof(StoredFormAttribute).IsSealed).IsTrue();
  }

  [Test]
  public async Task StoredFormRemoved_CarriesThePath_AndRepeatsOnAModelAsync() {
    var removed = new StoredFormRemovedAttribute("Shipping.Instructions");
    await Assert.That(removed.Path).IsEqualTo("Shipping.Instructions");

    var usage = AttributeTestHelpers.GetAttributeUsage<StoredFormRemovedAttribute>();
    await Assert.That(usage).IsNotNull();
    await Assert.That(usage!.ValidOn.HasFlag(AttributeTargets.Class)).IsTrue();
    await Assert.That(usage.ValidOn.HasFlag(AttributeTargets.Struct)).IsTrue();
    await Assert.That(usage.AllowMultiple).IsTrue()
      .Because("A model declares one removal per property it lost.");
    await Assert.That(typeof(StoredFormRemovedAttribute).IsSealed).IsTrue();
  }

  [Test]
  public async Task Target_QuotesTheQualifiedTableAsync() {
    var target = new StoredFormMigrationTarget("tenant \"a\"", "wh_per_order");

    await Assert.That(target.Schema).IsEqualTo("tenant \"a\"");
    await Assert.That(target.Table).IsEqualTo("wh_per_order");
    await Assert.That(target.QualifiedTable).IsEqualTo("\"tenant \"\"a\"\"\".\"wh_per_order\"")
      .Because("The qualified name is quoted, with embedded quotes doubled, so it is safe to splice into SQL.");
  }

  [Test]
  public async Task Migration_IsImplementedPerModel_AndReadThroughItsNonGenericFaceAsync() {
    IStoredFormMigration migration = new SampleMigration();

    await Assert.That(migration.Name).IsEqualTo("2026-10-sample");
    await Assert.That(migration.Order).IsEqualTo(10);
    await Assert.That(migration.BuildSql(new StoredFormMigrationTarget("public", "wh_per_sample")))
      .IsEqualTo("UPDATE \"public\".\"wh_per_sample\" SET data = data");
    await Assert.That(typeof(IStoredFormMigration).IsAssignableFrom(typeof(IStoredFormMigration<SampleModel>))).IsTrue();
  }

  private sealed record SampleModel(string Name);

  private sealed class SampleMigration : IStoredFormMigration<SampleModel> {
    public string Name => "2026-10-sample";

    public int Order => 10;

    public string BuildSql(StoredFormMigrationTarget target) => $"UPDATE {target.QualifiedTable} SET data = data";
  }
}
