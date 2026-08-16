using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Startup;

namespace Whizbang.Core.Tests.Startup;

/// <summary>
/// The resolver is the point of the pipeline: startup order stops being an emergent property of DI
/// registration position and becomes a function of declared dependencies. These tests hold it to
/// that — the same set of steps must resolve to the same order however it was registered, and an
/// order that cannot be satisfied must fail loudly at resolve time rather than silently pick one.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Startup/StartupStepOrderResolver.cs</code-under-test>
[Category("Startup")]
public class StartupStepOrderResolverTests {

  private static StartupStepDescriptor _step(string name, params string[] dependsOn) =>
    new() { Name = name, DependsOn = dependsOn };

  private static List<string> _names(IReadOnlyList<StartupStepDescriptor> steps) =>
    [.. steps.Select(s => s.Name)];

  /// <summary>The resolved order as one string — IsEquivalentTo compares membership, not sequence,
  /// and sequence is the entire point here.</summary>
  private static string _sequence(IReadOnlyList<StartupStepDescriptor> steps) =>
    string.Join(" → ", steps.Select(s => s.Name));

  // ── ordering ────────────────────────────────────────────────────────────

  [Test]
  public async Task Resolve_PlacesDependencyBeforeDependentAsync() {
    var order = _names(StartupStepOrderResolver.Resolve([
      _step("Reconcile", "Migrate"),
      _step("Migrate"),
    ]));

    await Assert.That(order.IndexOf("Migrate")).IsLessThan(order.IndexOf("Reconcile"));
  }

  [Test]
  public async Task Resolve_ResolvesTransitiveChainAsync() {
    var order = _names(StartupStepOrderResolver.Resolve([
      _step("Ready", "Provision"),
      _step("Provision", "Repair"),
      _step("Repair", "Migrate"),
      _step("Migrate"),
    ]));

    await Assert.That(string.Join(" → ", order)).IsEqualTo("Migrate → Repair → Provision → Ready");
  }

  [Test]
  public async Task Resolve_OrdersStepWithMultipleDependenciesAfterAllOfThemAsync() {
    var order = _names(StartupStepOrderResolver.Resolve([
      _step("Ready", "Repair", "Provision"),
      _step("Repair", "Migrate"),
      _step("Provision", "Migrate"),
      _step("Migrate"),
    ]));

    await Assert.That(order.IndexOf("Ready")).IsGreaterThan(order.IndexOf("Repair"));
    await Assert.That(order.IndexOf("Ready")).IsGreaterThan(order.IndexOf("Provision"));
    await Assert.That(order.IndexOf("Migrate")).IsEqualTo(0);
  }

  // ── determinism ─────────────────────────────────────────────────────────

  // The load-bearing property. If registration order can still influence the result, the pipeline
  // has reproduced the defect it exists to remove — just one layer further down.

  [Test]
  public async Task Resolve_IsIndependentOfRegistrationOrderAsync() {
    StartupStepDescriptor[] a = [
      _step("Migrate"), _step("Repair", "Migrate"), _step("Provision", "Migrate"), _step("Ready", "Repair", "Provision"),
    ];
    StartupStepDescriptor[] b = [
      _step("Ready", "Repair", "Provision"), _step("Provision", "Migrate"), _step("Repair", "Migrate"), _step("Migrate"),
    ];
    StartupStepDescriptor[] c = [
      _step("Provision", "Migrate"), _step("Ready", "Repair", "Provision"), _step("Migrate"), _step("Repair", "Migrate"),
    ];

    var first = _sequence(StartupStepOrderResolver.Resolve(a));
    await Assert.That(_sequence(StartupStepOrderResolver.Resolve(b))).IsEqualTo(first)
      .Because("the resolved SEQUENCE must not depend on the order the steps were registered in");
    await Assert.That(_sequence(StartupStepOrderResolver.Resolve(c))).IsEqualTo(first)
      .Because("the resolved SEQUENCE must not depend on the order the steps were registered in");
  }

  [Test]
  public async Task Resolve_BreaksTiesDeterministicallyForIndependentStepsAsync() {
    // Nothing constrains these relative to each other, so the resolver must still pick one stable
    // answer rather than echoing the order it happened to receive.
    var forward = _sequence(StartupStepOrderResolver.Resolve([_step("alpha"), _step("beta"), _step("gamma")]));
    var reversed = _sequence(StartupStepOrderResolver.Resolve([_step("gamma"), _step("beta"), _step("alpha")]));

    await Assert.That(forward).IsEqualTo(reversed)
      .Because("with nothing constraining them, the resolver must still pick one stable sequence");
  }

  // ── failures that must be loud ──────────────────────────────────────────

  [Test]
  public async Task Resolve_WithCycle_ThrowsNamingTheStepsInvolvedAsync() {
    var ex = await Assert.That(() => StartupStepOrderResolver.Resolve([
      _step("Repair", "Provision"),
      _step("Provision", "Repair"),
    ])).Throws<StartupPipelineConfigurationException>();

    await Assert.That(ex!.Message).Contains("Repair");
    await Assert.That(ex.Message).Contains("Provision");
  }

  [Test]
  public async Task Resolve_WithSelfDependency_ThrowsAsync() {
    await Assert.That(() => StartupStepOrderResolver.Resolve([_step("Migrate", "Migrate")]))
      .Throws<StartupPipelineConfigurationException>();
  }

  [Test]
  public async Task Resolve_WithUnknownDependency_ThrowsNamingBothAsync() {
    var ex = await Assert.That(() => StartupStepOrderResolver.Resolve([_step("Repair", "Nonexistent")]))
      .Throws<StartupPipelineConfigurationException>();

    await Assert.That(ex!.Message).Contains("Repair");
    await Assert.That(ex.Message).Contains("Nonexistent");
  }

  [Test]
  public async Task Resolve_WithDuplicateStepNames_ThrowsAsync() {
    // Two steps answering to one name makes "depends on Migrate" ambiguous, and silently keeping
    // one of them is how a step goes missing without anything reporting it.
    await Assert.That(() => StartupStepOrderResolver.Resolve([_step("Migrate"), _step("Migrate")]))
      .Throws<StartupPipelineConfigurationException>();
  }

  // ── enablement ──────────────────────────────────────────────────────────

  [Test]
  public async Task Resolve_OmitsDisabledStepsAsync() {
    var order = _names(StartupStepOrderResolver.Resolve([
      _step("Migrate"),
      new StartupStepDescriptor { Name = "Repair", DependsOn = ["Migrate"], Enabled = false },
    ]));

    await Assert.That(string.Join(" → ", order)).IsEqualTo("Migrate");
  }

  [Test]
  public async Task Resolve_WhenEnabledStepDependsOnDisabledStep_ThrowsAsync() {
    // Disabling a step must not silently weaken another step's declared ordering. Either the
    // dependent is disabled too, or the operator is told the combination is unsatisfiable.
    var ex = await Assert.That(() => StartupStepOrderResolver.Resolve([
      new StartupStepDescriptor { Name = "Migrate", Enabled = false },
      _step("Repair", "Migrate"),
    ])).Throws<StartupPipelineConfigurationException>();

    await Assert.That(ex!.Message).Contains("Repair");
    await Assert.That(ex.Message).Contains("Migrate");
  }

  [Test]
  public async Task Resolve_WhenDisabledStepDependsOnDisabledStep_SucceedsAsync() {
    var order = _names(StartupStepOrderResolver.Resolve([
      new StartupStepDescriptor { Name = "Migrate", Enabled = false },
      new StartupStepDescriptor { Name = "Repair", DependsOn = ["Migrate"], Enabled = false },
    ]));

    await Assert.That(order).IsEmpty();
  }

  // ── edges ───────────────────────────────────────────────────────────────

  [Test]
  public async Task Resolve_WithNoSteps_ReturnsEmptyAsync() {
    await Assert.That(StartupStepOrderResolver.Resolve([])).IsEmpty();
  }

  [Test]
  public async Task Resolve_WithNullSteps_ThrowsArgumentNullAsync() {
    await Assert.That(() => StartupStepOrderResolver.Resolve(null!)).Throws<ArgumentNullException>();
  }
}
