// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions.Extensions;
using Whizbang.Sagas;

namespace Whizbang.Sagas.Tests.Generators;

/// <summary>
/// That a saga declared with <c>[SignalsOutcome]</c> reaches the runtime registry with its policy.
/// </summary>
/// <remarks>
/// End-to-end for the reason <see cref="SagaContinuationGeneratorTests"/> is: the declarations in
/// <c>GeneratorTestSagas.cs</c> are compiled by the generator in this project and registered by the
/// emitted module initializer at assembly load, so a generator that emitted nothing, emitted an
/// initializer that never ran, or rendered the wrong policy all fail here.
/// </remarks>
/// <docs>fundamentals/sagas/commit-rollback#declaring-it</docs>
[Category("Unit")]
[Category("Saga")]
[Category("Generator")]
public class SagaOutcomeGeneratorTests {

  /// <summary>A declared saga is in the registry without anyone calling Register.</summary>
  [Test]
  public async Task ADeclaredSagaIsRegisteredAtLoadAsync() {
    var policy = SagaOutcomeRegistry.For("GeneratorTestSignalsOutcome");

    await Assert.That(policy).IsNotNull()
      .Because("the emitted module initializer registers the saga at assembly load");
    await Assert.That(policy!.OnCompletedWithFailures).IsEqualTo(SagaOutcome.Rollback);
  }

  /// <summary>An explicit policy survives generation, including for a saga with no generated service.</summary>
  /// <remarks>
  /// The failure this catches is a generator that drops the named argument and falls back to the
  /// default, which would silently roll back every partly failed run of a saga declared to keep them.
  /// </remarks>
  [Test]
  public async Task AnExplicitPolicySurvivesGenerationAsync() {
    var policy = SagaOutcomeRegistry.For("GeneratorTestTolerantOutcome");

    await Assert.That(policy).IsNotNull()
      .Because("registration lives in a module initializer, not in the generated service's DI extension");
    await Assert.That(policy!.OnCompletedWithFailures).IsEqualTo(SagaOutcome.Commit);
  }

  /// <summary>A saga with no declaration registers nothing.</summary>
  [Test]
  public async Task AnUndeclaredSagaRegistersNothingAsync() {
    await Assert.That(SagaOutcomeRegistry.For("GeneratorTestDefault")).IsNull();
    await Assert.That(SagaOutcomeRegistry.For("GeneratorTestChained")).IsNull();
  }
}
