using OpenTelemetry.Metrics;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Observability.DependencyInjection;

namespace Whizbang.Observability.Tests.DependencyInjection;

/// <summary>
/// Locks the turnkey meter subscription to <see cref="WhizbangMeters.All"/>.
/// </summary>
/// <remarks>
/// A consumer that hand-lists meter names silently loses every meter the framework adds after the
/// list was written. Observed in a real deployment: sixteen of twenty-one declared meters emitted
/// nothing for the entire life of the environment, including the dead-letter, maintenance, poison
/// and startup meters — exactly the signals an operator reaches for first when something is wrong.
/// The instruments were recording in-process the whole time; the values were discarded at the
/// subscription boundary, so the gap is invisible from inside the application.
/// </remarks>
/// <code-under-test>src/Whizbang.Observability/DependencyInjection/WhizbangMeterRegistration.cs</code-under-test>
public class WhizbangMeterRegistrationTests {

  private sealed class RecordingMeterProviderBuilder : MeterProviderBuilder {
    public List<string> Names { get; } = [];

    public override MeterProviderBuilder AddInstrumentation<TInstrumentation>(
        Func<TInstrumentation> instrumentationFactory) => this;

    public override MeterProviderBuilder AddMeter(params string[] names) {
      Names.AddRange(names);
      return this;
    }
  }

  [Test]
  public async Task AddWhizbangInstrumentation_SubscribesEveryDeclaredMeterAsync() {
    var builder = new RecordingMeterProviderBuilder();

    builder.AddWhizbangInstrumentation();

    // Every declared meter, not a curated subset: the whole point is that adding a meter to the
    // framework cannot require a consumer edit to become visible.
    foreach (var expected in WhizbangMeters.All) {
      await Assert.That(builder.Names).Contains(expected);
    }
  }

  [Test]
  public async Task AddWhizbangInstrumentation_SubscribesNothingUndeclaredAsync() {
    // The subscription is exactly the declared set. A stray name here would mean the helper had
    // its own hardcoded list, which is the defect this exists to remove.
    var builder = new RecordingMeterProviderBuilder();

    builder.AddWhizbangInstrumentation();

    await Assert.That(builder.Names.Count).IsEqualTo(WhizbangMeters.All.Count);
  }

  [Test]
  public async Task AddWhizbangInstrumentation_ReturnsSameBuilder_ForChainingAsync() {
    var builder = new RecordingMeterProviderBuilder();

    var returned = builder.AddWhizbangInstrumentation();

    await Assert.That(returned).IsSameReferenceAs(builder);
  }

  [Test]
  public void AddWhizbangInstrumentation_NullBuilder_ThrowsAsync() {
    MeterProviderBuilder? builder = null;

    Assert.Throws<ArgumentNullException>(() => builder!.AddWhizbangInstrumentation());
  }
}
