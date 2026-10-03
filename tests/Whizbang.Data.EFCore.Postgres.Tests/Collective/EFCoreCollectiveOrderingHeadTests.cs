using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Serialization;

namespace Whizbang.Data.EFCore.Postgres.Tests.Collective;

/// <summary>
/// The EF Core outbox path links a keyed collective to the one stored before it on its key (#1003, migration 190),
/// through the same store function the Dapper path uses: two publisher instances, each with its own context and
/// coordinator, form one chain.
/// </summary>
/// <docs>fundamentals/messaging/collective-events#ordering-across-services</docs>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
[Category("CollectiveEvents")]
public class EFCoreCollectiveOrderingHeadTests : EFCoreTestBase {
  private const string LINK_TYPE = "Test.Collectives.Flip";

  private OutboxMessage _collective(Guid key, Guid id) {
    var message = CreateTestOutboxMessage(id, "collectives", key);
    return message with { CollectiveLinkType = LINK_TYPE };
  }

  [Test]
  public async Task StoreOutboxMessagesAsync_TwoCoordinators_LinkTheSecondToTheFirstAsync() {
    var key = Guid.CreateVersion7();
    var first = Guid.CreateVersion7();
    var second = Guid.CreateVersion7();

    await using (var contextA = CreateDbContext()) {
      var instanceA = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
        contextA, JsonContextRegistry.CreateCombinedOptions(), NullLogger<EFCoreWorkCoordinator<WorkCoordinationDbContext>>.Instance);
      await instanceA.StoreOutboxMessagesAsync([_collective(key, first)], partitionCount: 10000);
    }
    await using (var contextB = CreateDbContext()) {
      var instanceB = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
        contextB, JsonContextRegistry.CreateCombinedOptions(), NullLogger<EFCoreWorkCoordinator<WorkCoordinationDbContext>>.Instance);
      await instanceB.StoreOutboxMessagesAsync([_collective(key, second)], partitionCount: 10000);
    }

    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      SELECT message_id, event_data->'p'->>'predecessorId', event_data->'p'->>'predecessorType'
      FROM wh_outbox WHERE stream_id = @key
      """;
    cmd.Parameters.AddWithValue("key", key);
    var links = new Dictionary<Guid, (string? Id, string? Type)>();
    await using (var reader = await cmd.ExecuteReaderAsync()) {
      while (await reader.ReadAsync()) {
        links[reader.GetGuid(0)] = (await reader.IsDBNullAsync(1) ? null : reader.GetString(1), await reader.IsDBNullAsync(2) ? null : reader.GetString(2));
      }
    }

    await Assert.That(links[first].Id).IsNull();
    await Assert.That(links[second].Id).IsEqualTo(first.ToString());
    await Assert.That(links[second].Type).IsEqualTo(LINK_TYPE);
  }
}
