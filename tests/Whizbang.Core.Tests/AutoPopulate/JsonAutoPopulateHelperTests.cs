using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.AutoPopulate;

namespace Whizbang.Core.Tests.AutoPopulate;

/// <summary>
/// Covers <see cref="JsonAutoPopulateHelper"/> — the AOT-safe wrapper that
/// stamps timestamp properties onto a <see cref="JsonElement"/> payload
/// without typed deserialization. Drives both the <c>Type</c>-keyed
/// (<see cref="JsonAutoPopulateHelper.PopulateTimestamp"/>) and string-keyed
/// (<see cref="JsonAutoPopulateHelper.PopulateTimestampByName"/>) entry points,
/// the no-registration short-circuit, the non-object payload short-circuit,
/// the partial-kind filter that should ignore mismatched registrations, and
/// the multi-registration cascade that writes every matching property.
/// </summary>
/// <docs>extending/attributes/auto-populate</docs>
[Category("Core")]
[Category("AutoPopulate")]
public class JsonAutoPopulateHelperTests {

  private static JsonElement _parseObject(string json) =>
    JsonDocument.Parse(json).RootElement.Clone();

  [Test]
  public async Task PopulateTimestamp_NoRegistrations_ReturnsOriginalPayloadAsync() {
    // _NoRegistrationsMessage has no [PopulateTimestamp] attribute and no fake
    // registry registers anything for it.
    var payload = _parseObject("""{"foo":"bar"}""");
    var ts = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    var result = JsonAutoPopulateHelper.PopulateTimestamp(
      payload, typeof(_NoRegistrationsMessage), TimestampKind.QueuedAt, ts);

    await Assert.That(result.GetRawText()).IsEqualTo(payload.GetRawText());
  }

  [Test]
  public async Task PopulateTimestampByName_NoRegistrations_ReturnsOriginalPayloadAsync() {
    var payload = _parseObject("""{"foo":"bar"}""");
    var ts = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    var result = JsonAutoPopulateHelper.PopulateTimestampByName(
      payload, "ThisTypeDoesNotExist", TimestampKind.QueuedAt, ts);

    await Assert.That(result.GetRawText()).IsEqualTo(payload.GetRawText());
  }

  [Test]
  public async Task PopulateTimestamp_RegistrationMatches_StampsPropertyOnObjectAsync() {
    using var _ = _FakeRegistry.Install(new AutoPopulateRegistration {
      MessageType = typeof(_QueuedAtMessage),
      PropertyName = "QueuedAt",
      PropertyType = typeof(DateTimeOffset?),
      PopulateKind = PopulateKind.Timestamp,
      TimestampKind = TimestampKind.QueuedAt,
    });

    var payload = _parseObject("""{"existing":"value"}""");
    var ts = new DateTimeOffset(2026, 5, 28, 12, 0, 0, TimeSpan.Zero);

    var result = JsonAutoPopulateHelper.PopulateTimestamp(
      payload, typeof(_QueuedAtMessage), TimestampKind.QueuedAt, ts);

    var doc = JsonDocument.Parse(result.GetRawText());
    await Assert.That(doc.RootElement.GetProperty("existing").GetString()).IsEqualTo("value");
    await Assert.That(doc.RootElement.GetProperty("QueuedAt").GetDateTimeOffset()).IsEqualTo(ts);
  }

  [Test]
  public async Task PopulateTimestampByName_MatchesByFullName_StampsPropertyAsync() {
    using var _ = _FakeRegistry.Install(new AutoPopulateRegistration {
      MessageType = typeof(_DeliveredAtMessage),
      PropertyName = "DeliveredAt",
      PropertyType = typeof(DateTimeOffset?),
      PopulateKind = PopulateKind.Timestamp,
      TimestampKind = TimestampKind.DeliveredAt,
    });

    var payload = _parseObject("""{}""");
    var ts = new DateTimeOffset(2026, 5, 28, 13, 0, 0, TimeSpan.Zero);

    var result = JsonAutoPopulateHelper.PopulateTimestampByName(
      payload, typeof(_DeliveredAtMessage).FullName!, TimestampKind.DeliveredAt, ts);

    var doc = JsonDocument.Parse(result.GetRawText());
    await Assert.That(doc.RootElement.GetProperty("DeliveredAt").GetDateTimeOffset()).IsEqualTo(ts);
  }

  [Test]
  public async Task PopulateTimestamp_WithMismatchedKind_LeavesPayloadUntouchedAsync() {
    // Registry has a QueuedAt registration; caller asks for DeliveredAt → empty
    // post-filter result → fast-return original payload.
    using var _ = _FakeRegistry.Install(new AutoPopulateRegistration {
      MessageType = typeof(_QueuedAtMessage),
      PropertyName = "QueuedAt",
      PropertyType = typeof(DateTimeOffset?),
      PopulateKind = PopulateKind.Timestamp,
      TimestampKind = TimestampKind.QueuedAt,
    });

    var payload = _parseObject("""{"x":1}""");
    var ts = new DateTimeOffset(2026, 5, 28, 14, 0, 0, TimeSpan.Zero);

    var result = JsonAutoPopulateHelper.PopulateTimestamp(
      payload, typeof(_QueuedAtMessage), TimestampKind.DeliveredAt, ts);

    await Assert.That(result.GetRawText()).IsEqualTo(payload.GetRawText());
  }

  [Test]
  public async Task PopulateTimestamp_NonObjectPayload_ReturnsOriginalAsync() {
    // Helper internally guards `if (node is not JsonObject obj)` and returns the
    // input untouched. Easiest non-object: a top-level JSON array.
    using var _ = _FakeRegistry.Install(new AutoPopulateRegistration {
      MessageType = typeof(_QueuedAtMessage),
      PropertyName = "QueuedAt",
      PropertyType = typeof(DateTimeOffset?),
      PopulateKind = PopulateKind.Timestamp,
      TimestampKind = TimestampKind.QueuedAt,
    });

    var payload = JsonDocument.Parse("""[1,2,3]""").RootElement.Clone();
    var ts = new DateTimeOffset(2026, 5, 28, 15, 0, 0, TimeSpan.Zero);

    var result = JsonAutoPopulateHelper.PopulateTimestamp(
      payload, typeof(_QueuedAtMessage), TimestampKind.QueuedAt, ts);

    await Assert.That(result.GetRawText()).IsEqualTo(payload.GetRawText());
  }

  [Test]
  public async Task PopulateTimestamp_MultipleRegistrationsForSameKind_StampsAllPropertiesAsync() {
    // Two properties, same TimestampKind → both should be written in one call.
    using var _ = _FakeRegistry.Install(
      new AutoPopulateRegistration {
        MessageType = typeof(_DualStampMessage),
        PropertyName = "QueuedAt",
        PropertyType = typeof(DateTimeOffset?),
        PopulateKind = PopulateKind.Timestamp,
        TimestampKind = TimestampKind.QueuedAt,
      },
      new AutoPopulateRegistration {
        MessageType = typeof(_DualStampMessage),
        PropertyName = "QueuedAtAlias",
        PropertyType = typeof(DateTimeOffset?),
        PopulateKind = PopulateKind.Timestamp,
        TimestampKind = TimestampKind.QueuedAt,
      });

    var payload = _parseObject("""{}""");
    var ts = new DateTimeOffset(2026, 5, 28, 16, 0, 0, TimeSpan.Zero);

    var result = JsonAutoPopulateHelper.PopulateTimestamp(
      payload, typeof(_DualStampMessage), TimestampKind.QueuedAt, ts);

    var doc = JsonDocument.Parse(result.GetRawText());
    await Assert.That(doc.RootElement.GetProperty("QueuedAt").GetDateTimeOffset()).IsEqualTo(ts);
    await Assert.That(doc.RootElement.GetProperty("QueuedAtAlias").GetDateTimeOffset()).IsEqualTo(ts);
  }

  // --- Test message marker types ---
  private sealed record _NoRegistrationsMessage;
  private sealed record _QueuedAtMessage;
  private sealed record _DeliveredAtMessage;
  private sealed record _DualStampMessage;

  /// <summary>
  /// Test-only <see cref="IAutoPopulateRegistry"/> that surfaces a fixed list
  /// of registrations. Each test uses a fresh marker type, so accumulating
  /// fake registries across the suite is harmless — a query for marker type
  /// X only sees the fake that was installed for X.
  /// </summary>
  private sealed class _FakeRegistry : IAutoPopulateRegistry, IDisposable {
    private readonly AutoPopulateRegistration[] _registrations;

    private _FakeRegistry(AutoPopulateRegistration[] registrations) {
      _registrations = registrations;
    }

    public IEnumerable<AutoPopulateRegistration> GetRegistrationsFor(Type messageType) {
      foreach (var r in _registrations) {
        if (r.MessageType == messageType) {
          yield return r;
        }
      }
    }

    public IEnumerable<AutoPopulateRegistration> GetAllRegistrations() => _registrations;

    public void Dispose() { /* no-op — registrations are bound to private marker types */ }

    public static _FakeRegistry Install(params AutoPopulateRegistration[] registrations) {
      var reg = new _FakeRegistry(registrations);
      // Priority 50 (lower than the 100 contracts convention) so this fake is
      // tried before any real registries that may be ambient at test time.
      AutoPopulateRegistry.Register(reg, priority: 50);
      return reg;
    }
  }
}
