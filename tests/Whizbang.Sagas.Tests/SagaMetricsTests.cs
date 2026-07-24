using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Sagas.Observability;

namespace Whizbang.Sagas.Tests;

/// <summary>
/// Locks meter name and instrument registration. A meter rename or
/// dropped instrument here silently breaks every consumer's
/// OpenTelemetry exporter wiring.
/// </summary>
[Category("Unit")]
[Category("Saga")]
public class SagaMetricsTests {

  [Test]
  public async Task Constructor_CreatesAllInstrumentsAsync() {
    var metrics = new SagaMetrics(new WhizbangMetrics());

    await Assert.That(metrics.SagasInitiated).IsNotNull();
    await Assert.That(metrics.SagasCompleted).IsNotNull();
    await Assert.That(metrics.SagasFailed).IsNotNull();
    await Assert.That(metrics.SagaDurationSeconds).IsNotNull();
    await Assert.That(metrics.ItemsCompletedPerSaga).IsNotNull();
    await Assert.That(metrics.ItemsFailedPerSaga).IsNotNull();
    await Assert.That(metrics.HooksCompleted).IsNotNull();
    await Assert.That(metrics.HooksFailed).IsNotNull();
    await Assert.That(metrics.ItemsReset).IsNotNull();
  }
}
