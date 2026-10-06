// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.AutoPopulate;

namespace Whizbang.Core.Tests.AutoPopulate;

/// <summary>
/// Branch coverage for <see cref="JsonAutoPopulateHelper"/>'s registration filter: a registration
/// for the right message type but a different populate kind (here a context registration that
/// happens to carry the requested timestamp kind) must be ignored by both entry points, and the
/// name-keyed entry point must also ignore a timestamp registration of a different kind.
/// </summary>
/// <code-under-test>src/Whizbang.Core/AutoPopulate/JsonAutoPopulateHelper.cs</code-under-test>
[Category("Core")]
[Category("AutoPopulate")]
public class JsonAutoPopulateHelperBranchCoverageTests {

  private static readonly DateTimeOffset _timestamp = new(2026, 6, 1, 9, 30, 0, TimeSpan.Zero);

  private sealed record ContextOnlyByTypeMessage;
  private sealed record ContextOnlyByNameMessage;
  private sealed record MismatchedKindByNameMessage;

  private sealed class FixedRegistry(params AutoPopulateRegistration[] registrations) : IAutoPopulateRegistry {
    public IEnumerable<AutoPopulateRegistration> GetRegistrationsFor(Type messageType) =>
      registrations.Where(r => r.MessageType == messageType);
    public IEnumerable<AutoPopulateRegistration> GetAllRegistrations() => registrations;
  }

  private static JsonElement _payload() {
    using var document = JsonDocument.Parse("""{"existing":"value"}""");
    return document.RootElement.Clone();
  }

  private static AutoPopulateRegistration _contextRegistrationCarryingQueuedAt(Type messageType) => new() {
    MessageType = messageType,
    PropertyName = "QueuedAt",
    PropertyType = typeof(DateTimeOffset?),
    // Not a timestamp registration, even though it carries the requested TimestampKind: only the
    // populate kind decides whether the timestamp helper may write it.
    PopulateKind = PopulateKind.Context,
    TimestampKind = TimestampKind.QueuedAt,
    ContextKind = ContextKind.UserId
  };

  [Test]
  public async Task PopulateTimestamp_NonTimestampRegistrationWithMatchingKind_LeavesPayloadUntouchedAsync() {
    AutoPopulateRegistry.Register(new FixedRegistry(_contextRegistrationCarryingQueuedAt(typeof(ContextOnlyByTypeMessage))), priority: 51);
    var payload = _payload();

    var result = JsonAutoPopulateHelper.PopulateTimestamp(
      payload, typeof(ContextOnlyByTypeMessage), TimestampKind.QueuedAt, _timestamp);

    await Assert.That(result.GetRawText()).IsEqualTo(payload.GetRawText())
      .Because("a context registration must never be stamped with a timestamp just because its TimestampKind matches");
  }

  [Test]
  public async Task PopulateTimestampByName_NonTimestampRegistrationWithMatchingKind_LeavesPayloadUntouchedAsync() {
    AutoPopulateRegistry.Register(new FixedRegistry(_contextRegistrationCarryingQueuedAt(typeof(ContextOnlyByNameMessage))), priority: 51);
    var payload = _payload();

    var result = JsonAutoPopulateHelper.PopulateTimestampByName(
      payload, typeof(ContextOnlyByNameMessage).FullName!, TimestampKind.QueuedAt, _timestamp);

    await Assert.That(result.GetRawText()).IsEqualTo(payload.GetRawText())
      .Because("the name-keyed entry point applies the same populate-kind filter as the type-keyed one");
  }

  [Test]
  public async Task PopulateTimestampByName_TimestampRegistrationOfAnotherKind_LeavesPayloadUntouchedAsync() {
    AutoPopulateRegistry.Register(new FixedRegistry(new AutoPopulateRegistration {
      MessageType = typeof(MismatchedKindByNameMessage),
      PropertyName = "DeliveredAt",
      PropertyType = typeof(DateTimeOffset?),
      PopulateKind = PopulateKind.Timestamp,
      TimestampKind = TimestampKind.DeliveredAt
    }), priority: 51);
    var payload = _payload();

    var result = JsonAutoPopulateHelper.PopulateTimestampByName(
      payload, typeof(MismatchedKindByNameMessage).FullName!, TimestampKind.QueuedAt, _timestamp);

    await Assert.That(result.GetRawText()).IsEqualTo(payload.GetRawText())
      .Because("a QueuedAt stamp must not land on a property declared as DeliveredAt");
  }
}
