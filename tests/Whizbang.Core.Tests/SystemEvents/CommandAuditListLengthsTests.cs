// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Core;
using Whizbang.Core.Audit;
using Whizbang.Core.Commands.System;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.SystemEvents;

namespace Whizbang.Core.Tests.SystemEvents;

/// <summary>
/// #1525: a command that carries id lists of unbounded length is audited with the length of each list, not the list,
/// so a rebuild of a hundred thousand streams writes a few hundred bytes of audit per receiving service, not megabytes.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Audit/IBoundedCommandAudit.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/SystemEvents/SystemEventEmitter.cs</code-under-test>
/// <docs>fundamentals/security/audit-logging#command-auditing</docs>
public class CommandAuditListLengthsTests {
  [Test]
  public async Task ARebuildNamingStreams_IsAuditedWithTheListLengths_AndKeepsTheRestOfTheCommandAsync() {
    var streams = Enumerable.Range(0, 1000).Select(_ => Guid.CreateVersion7()).ToArray();
    var command = new RebuildPerspectiveCommand(["OrderSummary"], RebuildMode.SelectedStreams, IncludeStreamIds: streams,
      ExcludeStreamIds: [Guid.CreateVersion7(), Guid.CreateVersion7()], RequestedBy: "operator-1");

    var body = await _auditedBodyAsync(command);

    await Assert.That(_property(body, nameof(RebuildPerspectiveCommand.IncludeStreamIds)).GetInt32()).IsEqualTo(1000)
      .Because("the audit records how many streams were named, never the ids themselves");
    await Assert.That(_property(body, nameof(RebuildPerspectiveCommand.ExcludeStreamIds)).GetInt32()).IsEqualTo(2);
    var names = _property(body, nameof(RebuildPerspectiveCommand.PerspectiveNames));
    await Assert.That(names.ValueKind).IsEqualTo(JsonValueKind.Array)
      .Because("only the lists the command names as id lists are reduced; the perspective names are kept");
    await Assert.That(names[0].GetString()).IsEqualTo("OrderSummary");
    await Assert.That(_property(body, nameof(RebuildPerspectiveCommand.RequestedBy)).GetString()).IsEqualTo("operator-1");
    await Assert.That(body.GetRawText().Contains(streams[0].ToString(), StringComparison.OrdinalIgnoreCase)).IsFalse();
  }

  [Test]
  public async Task ARebuildOfEveryStream_KeepsItsAbsentListsAbsentAsync() {
    var body = await _auditedBodyAsync(new RebuildPerspectiveCommand(["OrderSummary"]));

    await Assert.That(_tryProperty(body, nameof(RebuildPerspectiveCommand.IncludeStreamIds), out var include) ? include.ValueKind : JsonValueKind.Null)
      .IsEqualTo(JsonValueKind.Null).Because("a list that is not there has no length to record");
  }

  [Test]
  public async Task ARedeliveryRequest_IsAuditedWithItsStreamCountAsync() {
    var command = new RequestRedeliveryCommand {
      RequesterService = "service-a",
      Topic = "topic-a",
      StreamIds = [Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7()],
      EventTypes = ["OrderPlaced"],
    };

    var body = await _auditedBodyAsync(command);

    await Assert.That(_property(body, nameof(RequestRedeliveryCommand.StreamIds)).GetInt32()).IsEqualTo(3);
    await Assert.That(_property(body, nameof(RequestRedeliveryCommand.EventTypes)).ValueKind).IsEqualTo(JsonValueKind.Array);
  }

  [Test]
  public async Task ListLengths_ABodyThatIsNotAnObject_IsKeptAsItIsAsync() {
    using var doc = JsonDocument.Parse("[1,2,3]");

    var projected = AuditBodyProjection.ListLengths(doc.RootElement, ["Anything"]);

    await Assert.That(projected.GetRawText()).IsEqualTo("[1,2,3]");
  }

  [Test]
  public async Task ListLengths_MatchesNamesWithoutRegardToCase_AndKeepsANamedValueThatIsNotAListAsync() {
    using var doc = JsonDocument.Parse("""{"streamIds":["a","b"],"Count":5,"Other":["x"]}""");

    var projected = AuditBodyProjection.ListLengths(doc.RootElement, ["StreamIds", "Count"]);

    await Assert.That(projected.GetProperty("streamIds").GetInt32()).IsEqualTo(2)
      .Because("a naming policy may camel-case the property; the command names it by its C# name");
    await Assert.That(projected.GetProperty("Count").GetInt32()).IsEqualTo(5);
    await Assert.That(projected.GetProperty("Other").GetArrayLength()).IsEqualTo(1)
      .Because("a list the command does not name is kept whole");
  }

  private static async Task<JsonElement> _auditedBodyAsync<TCommand>(TCommand command) where TCommand : notnull {
    var store = new RecordingEventStore();
    var emitter = new SystemEventEmitter(Options.Create(new SystemEventOptions().EnableCommandAudit()), store,
      new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()),
      logger: NullLogger<SystemEventEmitter>.Instance, auditDecisionHook: null);

    await emitter.EmitCommandAuditedAsync(command, "result", "TestReceptor", null);

    return ((MessageEnvelope<CommandAudited>)store.Appended.Single()).Payload.CommandBody;
  }

  private static JsonElement _property(JsonElement body, string name) =>
    _tryProperty(body, name, out var value) ? value : throw new InvalidOperationException($"The audit body has no {name}: {body.GetRawText()}");

  private static bool _tryProperty(JsonElement body, string name, out JsonElement value) {
    foreach (var property in body.EnumerateObject()) {
      if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) {
        value = property.Value;
        return true;
      }
    }
    value = default;
    return false;
  }

  private sealed class RecordingEventStore : IEventStore {
    public List<object> Appended { get; } = [];

    public Task AppendAsync<TMessage>(Guid streamId, MessageEnvelope<TMessage> envelope, CancellationToken cancellationToken = default) {
      Appended.Add(envelope!);
      return Task.CompletedTask;
    }

    public Task AppendAsync<TMessage>(Guid streamId, TMessage message, CancellationToken cancellationToken = default) where TMessage : notnull =>
      throw new NotSupportedException();
    public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, long fromSequence, CancellationToken cancellationToken = default) =>
      throw new NotSupportedException();
    public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, Guid? fromEventId, CancellationToken cancellationToken = default) =>
      throw new NotSupportedException();
    public IAsyncEnumerable<MessageEnvelope<IEvent>> ReadPolymorphicAsync(Guid streamId, Guid? fromEventId, IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default) =>
      throw new NotSupportedException();
    public Task<List<MessageEnvelope<TMessage>>> GetEventsBetweenAsync<TMessage>(Guid streamId, Guid? afterEventId, Guid upToEventId, CancellationToken cancellationToken = default) =>
      throw new NotSupportedException();
    public Task<List<MessageEnvelope<IEvent>>> GetEventsBetweenPolymorphicAsync(Guid streamId, Guid? afterEventId, Guid upToEventId, IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default) =>
      throw new NotSupportedException();
    public Task<long> GetLastSequenceAsync(Guid streamId, CancellationToken cancellationToken = default) =>
      throw new NotSupportedException();
    public List<MessageEnvelope<IEvent>> DeserializeStreamEvents(IReadOnlyList<StreamEventData> streamEvents, IReadOnlyList<Type> eventTypes) =>
      throw new NotSupportedException();
  }
}
