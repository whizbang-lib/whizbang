// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;
using Whizbang.Data.EFCore.Postgres.Perspectives;
using Whizbang.Data.Postgres.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// Where the declared names come from (#1874): what reads a document says what it is called. A document Entity
/// Framework maps is described by its model, with required members taken from the serializer's metadata for the same
/// type; a document stored as one value is described by the serializer's metadata alone. Neither is reflection.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/Perspectives/DeclaredKeyShapes.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Perspectives/DeclaredKeyShape.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Perspectives/DeclaredKeyTable.cs</code-under-test>
public class DeclaredKeyShapesTests {
  private const string POINT = "{\"x\"!,\"y\"!}";

  private static DeclaredNamesDbContext _context() =>
    new(new DbContextOptionsBuilder<DeclaredNamesDbContext>()
      .UseNpgsql("Host=localhost;Database=probe;Username=u;Password=p")
      .Options);

  [Test]
  public async Task AMappedDocument_IsDescribedByTheModel_WithRequiredMembersFromTheSerializerAsync() {
    await using var context = _context();

    var table = DeclaredKeyShapes.ForModel(context.Model, PerspectiveDocumentSerialization.Options)
      .Single(t => t.Table == "wh_per_declared_picture");

    var data = table.Columns.Single(c => c.Column == "data").Shape.Describe();
    await Assert.That(data).Contains("\"title\"").And.Contains("\"Objects\":[{")
      .And.Contains("\"color\"").And.Contains($"\"Points\":[{POINT}]");
    await Assert.That(table.Columns.Select(c => c.Column)).Contains("scope")
      .Because("the framework's own documents are documents like any other");
    await Assert.That(table.JournalName).StartsWith("declared-names:wh_per_declared_picture:");
  }

  [Test]
  public async Task AnOpaqueDocument_IsDescribedByTheSerializerAsync() {
    await using var context = _context();

    var table = DeclaredKeyShapes.ForModel(context.Model, PerspectiveDocumentSerialization.Options)
      .Single(t => t.Table == "wh_per_declared_sketch");

    await Assert.That(table.Columns.Single(c => c.Column == "data").Shape.Describe())
      .Contains("\"Points\":[{").And.Contains("\"x\"!").And.Contains("\"y\"!").And.Contains("\"title\"");
  }

  [Test]
  public async Task TheSerializerShape_SkipsNumbersAndDictionariesAsync() {
    var shape = DeclaredKeyShape.FromSerializer(PerspectiveDocumentSerialization.Options, typeof(PerspectiveScope))!;

    await Assert.That(shape.Describe()).Contains("\"t\"")
      .Because("the scope declares its short names, which is what the rewrite compares against");
    await Assert.That(DeclaredKeyShape.FromSerializer(PerspectiveDocumentSerialization.Options, typeof(int))).IsNull()
      .Because("a number has no keys");
    await Assert.That(DeclaredKeyShape.FromSerializer(PerspectiveDocumentSerialization.Options, typeof(Dictionary<string, DeclaredPoint>))).IsNull()
      .Because("a dictionary's keys are data, not declared names");
  }

  [Test]
  public async Task APromotedJsonbColumn_IsADocument_UnlessItIsADictionaryAsync() {
    await using var context = new JsonbColumnsDbContext(new DbContextOptionsBuilder<JsonbColumnsDbContext>()
      .UseNpgsql("Host=localhost;Database=probe;Username=u;Password=p")
      .Options);

    var table = DeclaredKeyShapes.ForModel(context.Model, PerspectiveDocumentSerialization.Options)
      .Single(t => t.Table == "wh_per_jsonb_extracted_item");

    await Assert.That(table.Columns.Select(c => c.Column)).Contains("labels").And.Contains("place")
      .Because("a column holding objects is read by the serializer, so its keys are declared ones");
    await Assert.That(table.Columns.Select(c => c.Column)).DoesNotContain("grid_filter")
      .Because("a dictionary's keys are data");
  }

  [Test]
  public async Task ASelfReferencingType_IsDescribedToTheCycleAsync() {
    OpaqueNodeFixture.EnsureRegistered();

    var shape = DeclaredKeyShape.FromSerializer(PerspectiveDocumentSerialization.Options, typeof(OpaqueNode))!;

    await Assert.That(shape.Describe()).IsEqualTo("{\"Id\",\"At\",\"Next\"}")
      .Because("the walk stops where the type contains itself, rather than recursing forever");
  }

  [Test]
  public async Task AModelNothingDescribes_HasNoTableAsync() {
    var tables = DeclaredKeyTable.ForSerializedModels(
      [new PerspectiveDocumentModel("wh_per_unknown", typeof(DeclaredKeyShapesTests))], PerspectiveDocumentSerialization.Options);

    await Assert.That(tables.Single().Columns.Select(c => c.Column)).DoesNotContain("data")
      .Because("a type the serializer cannot describe contributes no document");
  }

  [Test]
  public async Task TheJournalName_ChangesWithTheShapeAsync() {
    var a = new DeclaredKeyTable("wh_per_a", [new DeclaredKeyColumn("data", DeclaredKeyShape.ForObject(new DeclaredMember("x")))]);
    var b = new DeclaredKeyTable("wh_per_a", [new DeclaredKeyColumn("data", DeclaredKeyShape.ForObject(new DeclaredMember("x", IsRequired: true)))]);
    var c = new DeclaredKeyTable("wh_per_a", [new DeclaredKeyColumn("data", DeclaredKeyShape.ForObject(new DeclaredMember("x")))]);

    await Assert.That(a.JournalName).IsNotEqualTo(b.JournalName)
      .Because("a model that declares something new is checked again, once");
    await Assert.That(a.JournalName).IsEqualTo(c.JournalName);
  }
}
