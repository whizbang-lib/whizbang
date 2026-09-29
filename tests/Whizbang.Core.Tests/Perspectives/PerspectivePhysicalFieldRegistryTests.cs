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
