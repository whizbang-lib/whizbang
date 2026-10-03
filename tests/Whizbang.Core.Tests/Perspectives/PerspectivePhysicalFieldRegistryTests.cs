using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// Unit tests for <see cref="PerspectivePhysicalFieldRegistry"/>: the model-type and property to physical-column
/// map the perspective-runner generator populates at module load, and which the collective apply path reads to
/// send a setter or a condition on a <c>[PhysicalField]</c> property to its column.
/// </summary>
/// <docs>fundamentals/messaging/collective-events</docs>
/// <tests>Whizbang.Core/Perspectives/PerspectivePhysicalFieldRegistry.cs</tests>
public class PerspectivePhysicalFieldRegistryTests {
  private sealed class ExtractedModel;
  private sealed class SplitModel;
  private sealed class JsonOnlyModel;
  private sealed class VectorModel;
  private sealed class UnregisteredModel;
  private sealed class ReRegisteredModel;

  [Test]
  public async Task Register_Extracted_ResolvesTheColumn_KeptInTheDocumentAsync() {
    PerspectivePhysicalFieldRegistry.Register(typeof(ExtractedModel), "Priority", "priority", FieldStorageMode.Extracted);

    var found = PerspectivePhysicalFieldRegistry.TryResolve(typeof(ExtractedModel), "Priority", out var field);

    await Assert.That(found).IsTrue();
    await Assert.That(field.PropertyName).IsEqualTo("Priority");
    await Assert.That(field.ColumnName).IsEqualTo("priority");
    await Assert.That(field.InDocument).IsTrue()
      .Because("Extracted keeps the full model in the document, so the physical column is a copy.");
    await Assert.That(field.IsVector).IsFalse();
  }

  [Test]
  public async Task Register_Split_ResolvesTheColumn_NotInTheDocumentAsync() {
    PerspectivePhysicalFieldRegistry.Register(typeof(SplitModel), "Lane", "lane_col", FieldStorageMode.Split);

    var found = PerspectivePhysicalFieldRegistry.TryResolve(typeof(SplitModel), "Lane", out var field);

    await Assert.That(found).IsTrue();
    await Assert.That(field.ColumnName).IsEqualTo("lane_col");
    await Assert.That(field.InDocument).IsFalse()
      .Because("Split stores a physical field only in its column; the document holds the remainder.");
  }

  [Test]
  public async Task Register_JsonOnlyWithPhysicalField_IsKeptInTheDocumentAsync() {
    PerspectivePhysicalFieldRegistry.Register(typeof(JsonOnlyModel), "Code", "code", FieldStorageMode.JsonOnly);

    PerspectivePhysicalFieldRegistry.TryResolve(typeof(JsonOnlyModel), "Code", out var field);

    await Assert.That(field.InDocument).IsTrue()
      .Because("Only Split strips a physical field from the document; every other mode keeps it there too.");
  }

  [Test]
  public async Task Register_Vector_IsFlaggedAsync() {
    PerspectivePhysicalFieldRegistry.Register(typeof(VectorModel), "Embedding", "embedding", FieldStorageMode.Split, isVector: true);

    PerspectivePhysicalFieldRegistry.TryResolve(typeof(VectorModel), "Embedding", out var field);

    await Assert.That(field.IsVector).IsTrue();
  }

  private sealed class ScalarModel;

  [Test]
  public async Task Register_ScalarTypeAndColumnType_AreCarriedAsync() {
    PerspectivePhysicalFieldRegistry.Register(typeof(ScalarModel), "Kind", "kind", FieldStorageMode.Split, scalarType: typeof(int));
    PerspectivePhysicalFieldRegistry.Register(typeof(ScalarModel), "Tags", "tags", FieldStorageMode.Split, columnType: "jsonb");

    PerspectivePhysicalFieldRegistry.TryResolve(typeof(ScalarModel), "Kind", out var kind);
    PerspectivePhysicalFieldRegistry.TryResolve(typeof(ScalarModel), "Tags", out var tags);

    await Assert.That(kind.ScalarType).IsEqualTo(typeof(int))
      .Because("An enum field carries the scalar its column holds, so every writer binds the same number.");
    await Assert.That(kind.ColumnType).IsNull();
    await Assert.That(tags.ColumnType).IsEqualTo("jsonb");
    await Assert.That(tags.IsJsonbColumn).IsTrue();
    await Assert.That(kind.IsJsonbColumn).IsFalse();
  }

  private sealed class JsonbColumnModel;

  /// <summary>A writer keyed by column name asks whether that column is jsonb.</summary>
  [Test]
  public async Task IsJsonbColumn_AnswersByModelAndColumnNameAsync() {
    PerspectivePhysicalFieldRegistry.Register(typeof(JsonbColumnModel), "Filters", "filters", FieldStorageMode.Extracted, columnType: "jsonb");
    PerspectivePhysicalFieldRegistry.Register(typeof(JsonbColumnModel), "Lane", "lane", FieldStorageMode.Extracted);

    await Assert.That(PerspectivePhysicalFieldRegistry.IsJsonbColumn(typeof(JsonbColumnModel), "filters")).IsTrue();
    await Assert.That(PerspectivePhysicalFieldRegistry.IsJsonbColumn(typeof(JsonbColumnModel), "lane")).IsFalse();
    await Assert.That(PerspectivePhysicalFieldRegistry.IsJsonbColumn(typeof(JsonbColumnModel), "Filters")).IsFalse()
      .Because("the runner keys its values by column name, not property name.");
    await Assert.That(PerspectivePhysicalFieldRegistry.IsJsonbColumn(typeof(UnregisteredModel), "filters")).IsFalse();
  }

  private sealed class JsonbDocumentModel;

  /// <summary>
  /// The jsonb columns whose value the document holds too: not a Split one, not a scalar column, ordered by
  /// property name, and none for a model without any.
  /// </summary>
  [Test]
  public async Task JsonbDocumentFields_AreTheJsonbColumnsTheDocumentCopies_InPropertyOrderAsync() {
    PerspectivePhysicalFieldRegistry.Register(typeof(JsonbDocumentModel), "Tags", "tags", FieldStorageMode.Extracted, columnType: "jsonb");
    PerspectivePhysicalFieldRegistry.Register(typeof(JsonbDocumentModel), "Filters", "filters", FieldStorageMode.JsonOnly, columnType: "jsonb");
    PerspectivePhysicalFieldRegistry.Register(typeof(JsonbDocumentModel), "Labels", "labels", FieldStorageMode.Split, columnType: "jsonb");
    PerspectivePhysicalFieldRegistry.Register(typeof(JsonbDocumentModel), "Lane", "lane", FieldStorageMode.Extracted);

    var fields = PerspectivePhysicalFieldRegistry.JsonbDocumentFields(typeof(JsonbDocumentModel));

    await Assert.That(string.Join(",", fields.Select(f => f.PropertyName))).IsEqualTo("Filters,Tags");
    await Assert.That(PerspectivePhysicalFieldRegistry.JsonbDocumentFields(typeof(UnregisteredModel))).IsEmpty();
  }

  [Test]
  public async Task TryResolve_Unregistered_ReturnsFalseAsync() {
    PerspectivePhysicalFieldRegistry.Register(typeof(ExtractedModel), "Priority", "priority", FieldStorageMode.Extracted);

    await Assert.That(PerspectivePhysicalFieldRegistry.TryResolve(typeof(UnregisteredModel), "Priority", out _)).IsFalse()
      .Because("A model with no generated registration has no physical columns.");
    await Assert.That(PerspectivePhysicalFieldRegistry.TryResolve(typeof(ExtractedModel), "Title", out _)).IsFalse()
      .Because("A property that is not a physical field stays a document path.");
  }

  [Test]
  public async Task Register_Twice_LastRegistrationWinsAsync() {
    PerspectivePhysicalFieldRegistry.Register(typeof(ReRegisteredModel), "Lane", "lane", FieldStorageMode.Extracted);
    PerspectivePhysicalFieldRegistry.Register(typeof(ReRegisteredModel), "Lane", "lane", FieldStorageMode.Split);

    PerspectivePhysicalFieldRegistry.TryResolve(typeof(ReRegisteredModel), "Lane", out var field);

    await Assert.That(field.InDocument).IsFalse()
      .Because("Two perspectives over one model register the same fields; registration is idempotent and the last one wins.");
  }

  [Test]
  public async Task Register_NullOrBlankArguments_ThrowAsync() {
    await Assert.That(() => PerspectivePhysicalFieldRegistry.Register(null!, "P", "p", FieldStorageMode.Extracted))
      .Throws<ArgumentNullException>();
    await Assert.That(() => PerspectivePhysicalFieldRegistry.Register(typeof(ExtractedModel), " ", "p", FieldStorageMode.Extracted))
      .Throws<ArgumentException>();
    await Assert.That(() => PerspectivePhysicalFieldRegistry.Register(typeof(ExtractedModel), "P", "", FieldStorageMode.Extracted))
      .Throws<ArgumentException>();
  }

  [Test]
  public async Task TryResolve_NullModelType_ReturnsFalseAsync() {
    await Assert.That(PerspectivePhysicalFieldRegistry.TryResolve(null!, "P", out _)).IsFalse();
  }
}
