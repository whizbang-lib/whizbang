using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Sagas.Helpers;

namespace Whizbang.Sagas.Tests;

/// <summary>
/// The claim key that makes saga completion fire exactly once.
/// <para>
/// Several receptors can observe the same saga reaching its terminal state at the same moment, and
/// each will try to emit the completion event. The dispatcher's publish-once path settles that race
/// by claim key, so the key itself is what defines "the same completion": it has to be stable for a
/// given saga and distinct across sagas. A key that varied per call would let every racing receptor
/// win its own claim and emit a duplicate; a key that collided across sagas would let one saga's
/// completion suppress another's entirely.
/// </para>
/// </summary>
/// <code-under-test>src/Whizbang.Sagas/Helpers/SagaCompletionGuard.cs</code-under-test>
public class SagaCompletionGuardTests {

  [Test]
  public async Task TheSameSaga_ProducesTheSameKeyEveryTimeAsync() {
    var sagaId = Guid.CreateVersion7();

    var first = SagaCompletionGuard.ClaimKey("OrderSaga", sagaId);
    var second = SagaCompletionGuard.ClaimKey("OrderSaga", sagaId);

    await Assert.That(first).IsEqualTo(second)
      .Because("racing receptors settle on this key; if it varied per call each would win its own "
             + "claim and emit a duplicate completion");
  }

  [Test]
  public async Task DifferentSagas_ProduceDistinctKeysAsync() {
    var a = Guid.CreateVersion7();
    var b = Guid.CreateVersion7();

    await Assert.That(SagaCompletionGuard.ClaimKey("OrderSaga", a))
      .IsNotEqualTo(SagaCompletionGuard.ClaimKey("OrderSaga", b))
      .Because("a key that collided across instances would let one saga's completion suppress "
             + "another's entirely");
    await Assert.That(SagaCompletionGuard.ClaimKey("OrderSaga", a))
      .IsNotEqualTo(SagaCompletionGuard.ClaimKey("ShipmentSaga", a))
      .Because("two saga types tracking the same correlation id are still different completions");
  }

  [Test]
  [Arguments("")]
  [Arguments("   ")]
  public async Task AnUnnamedSaga_IsRejectedRatherThanKeyedOnEmptinessAsync(string sagaName) {
    // An empty name collapses every saga sharing an id onto one key, which is the collision case
    // above with no way to notice it happened.
    await Assert.That(() => SagaCompletionGuard.ClaimKey(sagaName, Guid.CreateVersion7()))
      .Throws<ArgumentException>();
  }

  [Test]
  public async Task ANullSagaName_IsRejectedAsync() {
    await Assert.That(() => SagaCompletionGuard.ClaimKey(null!, Guid.CreateVersion7()))
      .Throws<ArgumentNullException>();
  }
}
