using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Whizbang.Core;
using Whizbang.Core.Data;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Policies;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.Dapper.Custom;
using Whizbang.Data.Dapper.Postgres;
using Whizbang.Testing.Contracts;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// Integration tests for DapperPostgresEventStore using PostgreSQL.
/// Inherits all contract tests from EventStoreContractTests.
/// Each test gets its own isolated PostgreSQL container for parallel execution.
/// </summary>
[InheritsTests]
public class DapperPostgresEventStoreTests : EventStoreContractTests, IDisposable {
  private PostgresTestBase _testBase = null!;

  [Before(Test)]
  public async Task SetupAsync() {
    _testBase = new TestFixture();
    await _testBase.SetupAsync();
  }

  [After(Test)]
  public async Task CleanupAsync() {
    if (_testBase != null) {
      await _testBase.DisposeAsync();
    }
  }

  public void Dispose() {
    _testBase?.DisposeAsync().AsTask().Wait();
    GC.SuppressFinalize(this);
  }

  protected override Task<IEventStore> CreateEventStoreAsync() {
    var jsonOptions = JsonOptionsHelper.CreateOptions();
    var adapter = new EventEnvelopeJsonbAdapter(jsonOptions);
    var sizeValidator = new JsonbSizeValidator(NullLogger<JsonbSizeValidator>.Instance);
    var policyEngine = new PolicyEngine();
    var logger = NullLogger<DapperPostgresEventStore>.Instance;

    var eventStore = new DapperPostgresEventStore(
      _testBase.ConnectionFactory,
      _testBase.Executor,
      jsonOptions,
      adapter,
      sizeValidator,
      policyEngine,
      null, // perspectiveInvoker
      logger
    );
    return Task.FromResult<IEventStore>(eventStore);
  }

  [Test]
  public async Task AppendAsync_SetsAggregateTypeToClrFullName_NotSimpleNameAsync() {
    // aggregate_type must be the CLR full type name (namespace + '+'-nested), IDENTICAL to what
    // EFCoreEventStore writes and to the no-assembly clr_type_name family the rename tool keys on.
    // The Dapper store historically wrote typeof(TMessage).Name (bare simple name, e.g. "TestEvent"),
    // which (a) diverged from EFCore's full-name form and (b) could never be matched by the rename
    // tool's clr_type_name-based UPDATE. Lock the full-name form.
    var store = await CreateEventStoreAsync();
    var streamId = Guid.NewGuid();

    var envelope = new MessageEnvelope<TestEvent> {
      MessageId = MessageId.New(),
      Payload = new TestEvent { StreamId = streamId, Payload = "agg-type" },
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
    envelope.AddHop(new MessageHop {
      Type = HopType.Current,
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "DapperPostgresEventStoreTests",
        InstanceId = Guid.NewGuid(),
        HostName = "test-host",
        ProcessId = 12345
      }
    });

    await store.AppendAsync(streamId, envelope);

    using var connection = await _testBase.ConnectionFactory.CreateConnectionAsync();
    var aggregateType = await connection.ExecuteScalarAsync<string>(
      "SELECT aggregate_type FROM wh_event_store WHERE stream_id = @StreamId",
      new { StreamId = streamId });

    await Assert.That(aggregateType).IsEqualTo(typeof(TestEvent).FullName);
  }

  private sealed class TestFixture : PostgresTestBase;
}
