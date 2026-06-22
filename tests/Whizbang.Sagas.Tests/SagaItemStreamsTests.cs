using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Sagas;

namespace Whizbang.Sagas.Tests;

/// <summary>
/// Locks the per-item stream id derivation. The RFC 4122 v5 output for
/// any given <c>(namespace, sagaId, itemIdentifier)</c> triple is a
/// production-data invariant — changing it re-routes every persisted
/// per-item event and orphans projection rows.
/// </summary>
[Category("Unit")]
[Category("Saga")]
public class SagaItemStreamsTests {

  // ── Determinism: same inputs, same outputs ───────────────────────────

  [Test]
  public async Task Of_SameSagaIdAndItemIdentifier_ProducesSameStreamIdAsync() {
    var sagaId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    var a = SagaItemStreams.Of(sagaId, "item-42");
    var b = SagaItemStreams.Of(sagaId, "item-42");

    await Assert.That(a).IsEqualTo(b)
      .Because("Derivation is deterministic — every event for a given (saga, item) must resolve to the same stream.");
  }

  [Test]
  public async Task Of_DifferentItemIdentifiers_ProduceDifferentStreamIdsAsync() {
    var sagaId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    var a = SagaItemStreams.Of(sagaId, "item-42");
    var b = SagaItemStreams.Of(sagaId, "item-43");

    await Assert.That(a).IsNotEqualTo(b)
      .Because("Two items in the same saga must route to distinct streams — otherwise their events would interleave on one projection row.");
  }

  [Test]
  public async Task Of_DifferentSagaIds_ProduceDifferentStreamIdsAsync() {
    var item = "item-42";

    var a = SagaItemStreams.Of(Guid.Parse("11111111-1111-1111-1111-111111111111"), item);
    var b = SagaItemStreams.Of(Guid.Parse("22222222-2222-2222-2222-222222222222"), item);

    await Assert.That(a).IsNotEqualTo(b)
      .Because("Two different sagas with the same item name must produce different streams — otherwise unrelated sagas would collide.");
  }

  // ── RFC 4122 v5 compliance: version + variant bits ───────────────────

  [Test]
  public async Task Of_ResultIsRfc4122Version5UuidAsync() {
    var streamId = SagaItemStreams.Of(Guid.NewGuid(), "anything");
    var bytes = streamId.ToByteArray(bigEndian: true);

    // RFC 4122 v5: high nibble of byte 6 is 5
    var version = (bytes[6] & 0xF0) >> 4;
    await Assert.That(version).IsEqualTo(5)
      .Because("RFC 4122 v5 stamps version 5 in the top nibble of byte 6; SQL parity depends on it.");

    // Variant 10xx in the top two bits of byte 8
    var variant = (bytes[8] & 0xC0) >> 6;
    await Assert.That(variant).IsEqualTo(2)
      .Because("RFC 4122 variant 10xx is required for parity with PostgreSQL's saga_item_stream_id() function.");
  }

  // ── DefaultNamespace stability lock ──────────────────────────────────

  [Test]
  public async Task DefaultNamespace_IsTheWhizbangCanonicalValue_DoNotChangeAsync() {
    await Assert.That(SagaItemStreams.DefaultNamespace)
      .IsEqualTo(Guid.Parse("acb2e0cf-92d5-4f1b-8c5e-2d7f4a5e8b3a"))
      .Because("Every fresh consumer's persisted per-item stream ids derive from this exact namespace UUID. Changing this value orphans every existing per-item projection row in every consumer that ever used the default. Lock-in test.");
  }

  // ── Configurable AppDefaultNamespace flow ────────────────────────────

  [Test]
  public async Task AppDefaultNamespace_WhenChanged_IsUsedByDefaultOverloadAsync() {
    // Capture and restore so subsequent tests see the canonical default.
    var prior = SagaItemStreams.AppDefaultNamespace;
    try {
      var custom = Guid.Parse("0b36f8d4-3884-4c3c-b92b-fc6ec74775ea");
      SagaItemStreams.AppDefaultNamespace = custom;

      var sagaId = Guid.Parse("11111111-1111-1111-1111-111111111111");
      var resolved = SagaItemStreams.Of(sagaId, "item-42");
      var withCustomExplicit = SagaItemStreams.Of(custom, sagaId, "item-42");

      await Assert.That(resolved).IsEqualTo(withCustomExplicit)
        .Because("The no-namespace overload must route through the current AppDefaultNamespace; that's the only way consumer-level config (e.g. a consumer setting its historical namespace via AddWhizbangSagas) reaches every call site without per-saga attribute pollution.");
    } finally {
      SagaItemStreams.AppDefaultNamespace = prior;
    }
  }

  [Test]
  public async Task AppDefaultNamespace_DefaultsToDefaultNamespaceAsync() {
    await Assert.That(SagaItemStreams.AppDefaultNamespace)
      .IsEqualTo(SagaItemStreams.DefaultNamespace)
      .Because("Without an explicit AddWhizbangSagas call, the runtime-current namespace must equal the factory default — otherwise fresh consumers would silently derive against an unintended value.");
  }

  // ── Validation ───────────────────────────────────────────────────────

  [Test]
  public async Task Of_EmptyItemIdentifier_ThrowsAsync() {
    await Assert.That(() => SagaItemStreams.Of(Guid.NewGuid(), string.Empty))
      .ThrowsExactly<ArgumentException>()
      .Because("An empty item identifier silently collapses to a single global per-saga stream; reject at the boundary.");
  }

  [Test]
  public async Task Of_WhitespaceItemIdentifier_ThrowsAsync() {
    await Assert.That(() => SagaItemStreams.Of(Guid.NewGuid(), "   "))
      .ThrowsExactly<ArgumentException>();
  }

  // ── Namespace override produces different stream than default ─────────

  [Test]
  public async Task Of_DifferentNamespaces_ProduceDifferentStreamIdsAsync() {
    var sagaId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    var ns1 = Guid.Parse("acb2e0cf-92d5-4f1b-8c5e-2d7f4a5e8b3a");
    var ns2 = Guid.Parse("0b36f8d4-3884-4c3c-b92b-fc6ec74775ea");

    var a = SagaItemStreams.Of(ns1, sagaId, "item-42");
    var b = SagaItemStreams.Of(ns2, sagaId, "item-42");

    await Assert.That(a).IsNotEqualTo(b)
      .Because("The namespace participates in the SHA-1 input; consumers migrating from one namespace to another get distinct stream ids — which is exactly the property that makes the configuration knob useful.");
  }
}
