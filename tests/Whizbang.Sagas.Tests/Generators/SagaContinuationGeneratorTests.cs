using TUnit.Assertions.Extensions;
using Whizbang.Sagas;

namespace Whizbang.Sagas.Tests.Generators;

/// <summary>
/// That a declared chain actually reaches the runtime registry.
/// </summary>
/// <remarks>
/// <para>
/// End-to-end rather than by inspecting generated text. The declarations below are compiled by the
/// generator in this test project and registered by the emitted module initializer at assembly load,
/// so asking the registry what it holds exercises the whole path: reading the attribute, rendering
/// the call, and running it. A generator that emitted nothing, emitted an initializer that never ran,
/// or wrote the wrong trigger all fail here, and none of them would fail a test that matched strings
/// in the generated source.
/// </para>
/// <para>
/// The generated registration class is deliberately never named here. Test source that names a
/// generated type does not compile where the generators are absent.
/// </para>
/// </remarks>
/// <docs>fundamentals/sagas/continuations</docs>
[Category("Unit")]
[Category("Saga")]
[Category("Generator")]
public class SagaContinuationGeneratorTests {

  /// <summary>A declared continuation is in the registry without anyone calling Register.</summary>
  [Test]
  public async Task ADeclaredChainIsRegisteredAtLoadAsync() {
    var continuations = SagaContinuationRegistry.For("GeneratorTestChained");

    await Assert.That(continuations.Select(c => c.SagaName)).Contains("GeneratorTestFollowOn")
      .Because("the emitted module initializer registers the chain at assembly load, so no host "
        + "wiring and no Add method call is required for it to be known");
  }

  /// <summary>The declaration's default trigger survives the round trip.</summary>
  [Test]
  public async Task TheDefaultTriggerSurvivesGenerationAsync() {
    var continuation = SagaContinuationRegistry.For("GeneratorTestChained")
      .Single(c => c.SagaName == "GeneratorTestFollowOn");

    await Assert.That(continuation.Trigger).IsEqualTo(SagaContinuationTrigger.RanToTheEnd);
    await Assert.That(continuation.StartsAfter(SagaStatus.CompletedWithFailures)).IsTrue();
  }

  /// <summary>An explicit trigger is rendered as what was written, not as the default.</summary>
  /// <remarks>
  /// The failure this catches is a generator that drops the second constructor argument and falls
  /// back to the default, which would silently widen every narrowed chain.
  /// </remarks>
  [Test]
  public async Task AnExplicitTriggerSurvivesGenerationAsync() {
    var continuation = SagaContinuationRegistry.For("GeneratorTestChained")
      .Single(c => c.SagaName == "GeneratorTestCleanup");

    await Assert.That(continuation.Trigger).IsEqualTo(SagaContinuationTrigger.Failed);
    await Assert.That(continuation.StartsAfter(SagaStatus.Completed)).IsFalse()
      .Because("a cleanup declared for the abandoned case must not run after a successful one");
  }

  /// <summary>Several declarations on one saga all arrive.</summary>
  [Test]
  public async Task EveryDeclarationOnASagaArrivesAsync() {
    await Assert.That(SagaContinuationRegistry.For("GeneratorTestChained").Count).IsEqualTo(2);
  }

  /// <summary>A saga with no declaration registers nothing for itself.</summary>
  /// <remarks>
  /// Confirms the emission is conditional. An initializer emitted for every saga would put an empty
  /// entry in the registry and make the "is this saga chained" question always answer yes.
  /// </remarks>
  [Test]
  public async Task AnUndeclaredSagaRegistersNothingAsync() {
    await Assert.That(SagaContinuationRegistry.For("GeneratorTestDefault")).IsEmpty();
  }
}

// ── Saga declarations the generator will pick up ───────────────────────

/// <summary>A saga followed by enrichment over what it wrote, and by cleanup if it was abandoned.</summary>
[Saga("GeneratorTestChained")]
[ContinuesWith("GeneratorTestFollowOn")]
[ContinuesWith("GeneratorTestCleanup", SagaContinuationTrigger.Failed)]
public partial class GeneratorTestChainedSaga;
