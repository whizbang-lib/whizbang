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
/// Coverage for <see cref="DapperPostgresEventStore"/>'s retry-exhaustion path (the throw after
/// <c>MAX_RETRIES</c> attempts). <see cref="DapperPostgresEventStoreRetryTests.AppendAsync_ExtremeContention_ShouldEventuallyThrowMaxRetriesAsync"/>
/// aims at the same lines but only PROBABILISTICALLY reaches them — its own comment says "success
/// rate varies widely" under load — so this file drives the same throw deterministically instead.
/// <para>
/// <c>event_id</c> is <c>wh_event_store</c>'s primary key, independent of <c>stream_id</c>/
/// <c>version</c>. Re-appending the SAME envelope reuses the SAME <c>event_id</c> on every retry
/// attempt: a genuine concurrent writer's conflict clears once it commits, but this one never
/// clears no matter how many times the loop recomputes the next sequence number — guaranteeing
/// exhaustion instead of racing for it.
/// </para>
/// <para>
/// A caller stuck retrying forever behind a conflict that can never resolve would look
/// indistinguishable from a hang. The retry budget exists so that failure surfaces loudly
/// instead — losing this path turns a diagnosable error into a silent stall.
/// </para>
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
  public async Task AppendAsync_ReusingTheSameEnvelope_ThrowsAfterMaxRetriesAsync() {
    var store = _newStore();
    var streamId = Guid.NewGuid();
    var envelope = _buildEnvelope(streamId, MessageId.New());

    // First append lands normally and claims this envelope's event_id.
    await store.AppendAsync(streamId, envelope);

    // Re-appending the IDENTICAL envelope object collides on the same event_id on every single
    // retry attempt, regardless of the sequence number each attempt recomputes — deterministic
    // exhaustion instead of a race that a real concurrent writer would eventually clear.
    var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
      async () => await store.AppendAsync(streamId, envelope));

    await Assert.That(thrown).IsNotNull()
      .Because("a conflict that can never clear must surface as a real failure, not hang or vanish silently");
    await Assert.That(thrown!.Message).Contains("after 10 attempts")
      .Because("the message must name the exhausted retry budget so an operator reading logs knows this isn't a one-off timeout");
    await Assert.That(thrown.InnerException).IsNotNull()
      .Because("the underlying Postgres conflict must be preserved as the inner exception, not swallowed");
  }
}
