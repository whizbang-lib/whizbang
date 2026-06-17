using System.Text.Json;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Serialization;

namespace Whizbang.Core.Tests.Serialization;

/// <summary>
/// Tests for the general, reusable <see cref="VersionedJsonEnvelope"/> — the framework-wide
/// serialization-version stamp that any persisted-JSON path can use.
/// </summary>
public class VersionedJsonEnvelopeTests {
  [Test]
  public async Task Wrap_ThenTryRead_RoundTripsVersionAndPayloadAsync() {
    using var payload = JsonDocument.Parse("""{"Name":"x"}""");
    using var stored = VersionedJsonEnvelope.Wrap(payload.RootElement, serializationVersion: 5);

    var ok = VersionedJsonEnvelope.TryRead(stored, out var version, out var inner);

    await Assert.That(ok).IsTrue();
    await Assert.That(version).IsEqualTo(5);
    await Assert.That(inner.GetProperty("Name").GetString()).IsEqualTo("x");
  }

  [Test]
  public async Task TryRead_LegacyUnversionedBlob_ReturnsFalseWithLegacyVersionAsync() {
    using var legacyRaw = JsonDocument.Parse("""{"Name":"x"}""");

    var ok = VersionedJsonEnvelope.TryRead(legacyRaw, out var version, out var inner);

    await Assert.That(ok).IsFalse();
    await Assert.That(version).IsEqualTo(VersionedJsonEnvelope.LEGACY);
    // Payload falls back to the whole root for a legacy blob.
    await Assert.That(inner.GetProperty("Name").GetString()).IsEqualTo("x");
  }
}
