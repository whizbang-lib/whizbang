using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Generators.Sagas;

namespace Whizbang.Generators.Tests.Sagas;

/// <summary>
/// Tests for the <see cref="SagaRecoveryReceptorShapes"/> table that drives saga recovery
/// receptor emission. The shapes are a contract with the generated code: a wrong stage or a
/// renamed handler silently changes what every saga emits, so they are pinned here.
/// </summary>
public class SagaRecoveryReceptorShapesTests {
  private const string POST_ALL_PERSPECTIVES_INLINE =
      "global::Whizbang.Core.Messaging.LifecycleStage.PostAllPerspectivesInline";

  [Test]
  public async Task All_ContainsTheThreeRecoveryShapesAsync() {
    await Assert.That(SagaRecoveryReceptorShapes.All.Length).IsEqualTo(3);
  }

  [Test]
  public async Task All_HasDistinctClassNamesAsync() {
    var names = SagaRecoveryReceptorShapes.All.Select(s => s.ClassName).ToList();

    await Assert.That(names.Distinct().Count()).IsEqualTo(names.Count);
  }

  [Test]
  [Arguments("SagaItemCompletedRecoveryHandler", "ItemCompletedEvent")]
  [Arguments("SagaItemFailedRecoveryHandler", "ItemFailedEvent")]
  public async Task PerItemTerminals_FireInlineAfterAllPerspectivesAsync(
      string className, string sagaEventClassName) {
    var shape = SagaRecoveryReceptorShapes.All.Single(s => s.ClassName == className);

    await Assert.That(shape.SagaEventClassName).IsEqualTo(sagaEventClassName);
    await Assert.That(shape.FrameworkMessageType).IsNull();
    await Assert.That(shape.LifecycleStage).IsEqualTo(POST_ALL_PERSPECTIVES_INLINE);
  }

  [Test]
  public async Task WatchdogTickHandler_UsesTheFrameworkTickTypeAsync() {
    var shape = SagaRecoveryReceptorShapes.All
        .Single(s => s.ClassName == "SagaCompletionWatchdogTickHandler");

    await Assert.That(shape.SagaEventClassName).IsNull();
    await Assert.That(shape.FrameworkMessageType)
        .IsEqualTo("Whizbang.Sagas.SagaCompletionWatchdogTickEvent");
  }

  [Test]
  public async Task WatchdogTickHandler_TakesTheDefaultLifecycleStageAsync() {
    var shape = SagaRecoveryReceptorShapes.All
        .Single(s => s.ClassName == "SagaCompletionWatchdogTickHandler");

    await Assert.That(shape.LifecycleStage).IsNull();
  }

  [Test]
  public async Task EveryShape_CarriesExactlyOneMessageSourceAsync() {
    foreach (var shape in SagaRecoveryReceptorShapes.All) {
      var hasSagaEvent = shape.SagaEventClassName is not null;
      var hasFrameworkType = shape.FrameworkMessageType is not null;

      await Assert.That(hasSagaEvent != hasFrameworkType).IsTrue();
    }
  }

  [Test]
  public async Task Shape_HasValueEqualityAsync() {
    var a = new SagaRecoveryReceptorShape("H", "E", null, POST_ALL_PERSPECTIVES_INLINE);
    var b = new SagaRecoveryReceptorShape("H", "E", null, POST_ALL_PERSPECTIVES_INLINE);

    await Assert.That(a).IsEqualTo(b);
  }
}
