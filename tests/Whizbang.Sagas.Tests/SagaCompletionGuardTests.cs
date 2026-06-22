using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Sagas.Helpers;

namespace Whizbang.Sagas.Tests;

/// <summary>
/// Locks the saga-claim-key convention applied by
/// <see cref="SagaCompletionGuard"/>. Concentrates the "what's the
/// claim key for a saga completion?" decision in one place so every
/// emitter routes through the same string. The actual dispatch path
/// (PublishOnceAsync against a real Postgres claim table) is locked
/// in DispatcherPublishOnceTests + ClaimedEmissionStoreTests; this
/// suite only verifies the claim-key shape.
/// </summary>
[Category("Unit")]
[Category("Saga")]
public class SagaCompletionGuardTests {

  private static readonly Guid _sagaId = Guid.Parse("11111111-1111-1111-1111-111111111111");

  [Test]
  public async Task ClaimKey_PrefixesWithSagaCompletedAndSagaNameAsync() {
    var key = SagaCompletionGuard.ClaimKey("BulkImport", _sagaId);

    await Assert.That(key).IsEqualTo("saga-completed:BulkImport:11111111-1111-1111-1111-111111111111")
      .Because("Prefix + saga name + saga id is unique across saga types — without the saga-name segment, two different saga types with the same sagaId would collide on the same claim.");
  }

  [Test]
  public async Task ClaimKey_DifferentSagaNames_ProduceDifferentKeysAsync() {
    var a = SagaCompletionGuard.ClaimKey("BulkImport", _sagaId);
    var b = SagaCompletionGuard.ClaimKey("EmployeeImport", _sagaId);

    await Assert.That(a).IsNotEqualTo(b)
      .Because("Two different sagas happening to share an id (unlikely but possible) must NOT collide — the saga-name segment in the claim key prevents this.");
  }

  [Test]
  public async Task ClaimKey_DifferentSagaIds_ProduceDifferentKeysAsync() {
    var a = SagaCompletionGuard.ClaimKey("BulkImport", _sagaId);
    var b = SagaCompletionGuard.ClaimKey("BulkImport", Guid.Parse("22222222-2222-2222-2222-222222222222"));

    await Assert.That(a).IsNotEqualTo(b);
  }

  [Test]
  public async Task ClaimKey_NullSagaName_ThrowsAsync() {
    await Assert.That(() => SagaCompletionGuard.ClaimKey(null!, _sagaId))
      .Throws<ArgumentException>();
  }

  [Test]
  public async Task ClaimKey_EmptySagaName_ThrowsAsync() {
    await Assert.That(() => SagaCompletionGuard.ClaimKey(string.Empty, _sagaId))
      .Throws<ArgumentException>();
  }

  [Test]
  public async Task ClaimKey_WhitespaceSagaName_ThrowsAsync() {
    await Assert.That(() => SagaCompletionGuard.ClaimKey("   ", _sagaId))
      .Throws<ArgumentException>();
  }

  [Test]
  public async Task ClaimKey_PrefixIsStableAsync() {
    var key = SagaCompletionGuard.ClaimKey("AnySaga", _sagaId);

    await Assert.That(key.StartsWith("saga-completed:", StringComparison.Ordinal)).IsTrue()
      .Because("The 'saga-completed:' prefix is the contract operators search for in the claim table; renaming it silently breaks every ops dashboard that filters by it.");
  }
}
