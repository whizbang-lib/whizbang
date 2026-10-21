// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Data;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Policies;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// Coverage for <see cref="DapperPostgresEventStore"/>'s duplicate-event path. Since #1555 the database
/// assigns the version under the stream's lock, so a unique violation can only be the event id
/// (<c>wh_event_store</c>'s primary key): re-appending the SAME envelope must fail at once, naming the
/// duplicate, with the Postgres conflict preserved. It used to be retried ten times as if it were a
/// concurrent writer, and then reported as an exhausted retry budget.
/// </summary>
public class DapperPostgresEventStoreCoverageTests : PostgresTestBase {

  private DapperPostgresEventStore _newStore() {
    var jsonOptions = Whizbang.Data.Dapper.Postgres.Tests.Generated.WhizbangJsonContext.CreateOptions();
    var adapter = new EventEnvelopeJsonbAdapter(jsonOptions);
    var sizeValidator = new JsonbSizeValidator(NullLogger<JsonbSizeValidator>.Instance);
    var policyEngine = new PolicyEngine();
    return new DapperPostgresEventStore(
      ConnectionFactory,
      Executor,
      jsonOptions,
      adapter,
      sizeValidator,
      policyEngine,
      null, // perspectiveInvoker
      NullLogger<DapperPostgresEventStore>.Instance);
  }

  private static MessageEnvelope<PostgresRetryTestEvent> _buildEnvelope(Guid streamId, MessageId messageId) {
    var envelope = new MessageEnvelope<PostgresRetryTestEvent> {
      MessageId = messageId,
      Payload = new PostgresRetryTestEvent {
        StreamId = streamId,
        Payload = "coverage-conflict-payload"
      },
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
    envelope.AddHop(new MessageHop {
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "DapperPostgresEventStoreCoverageTests",
        InstanceId = Guid.NewGuid(),
        HostName = "test-host",
        ProcessId = 12345
      },
      Type = HopType.Current
    });
    return envelope;
  }

  [Test]
  public async Task AppendAsync_ReusingTheSameEnvelope_FailsAtOnceAsADuplicateAsync() {
    var store = _newStore();
    var streamId = Guid.NewGuid();
    var envelope = _buildEnvelope(streamId, MessageId.New());

    // First append lands normally and claims this envelope's event_id.
    await store.AppendAsync(streamId, envelope);

    var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
      async () => await store.AppendAsync(streamId, envelope));

    await Assert.That(thrown!.Message).Contains("an event with this id is already stored")
      .Because("the message must name the duplicate so an operator does not chase a race that cannot happen");
    await Assert.That(thrown.InnerException).IsNotNull()
      .Because("the underlying Postgres conflict must be preserved as the inner exception, not swallowed");
  }
}
