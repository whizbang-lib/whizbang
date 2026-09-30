using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives.Sync;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// The Dapper coordinator reads the applied-event ledger (migration 176, #959) the same way the EF Core
/// one does: by event id, and by stream and local version.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Dapper.Postgres/DapperWorkCoordinator.cs</code-under-test>
public class DapperAppliedEventStatusTests : PostgresTestBase {
  private const string PERSPECTIVE = "Ledger.Tests.OrderViewPerspective";

  private DapperWorkCoordinator _build() =>
    new DapperWorkCoordinator(
      ConnectionString,
      Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions(),
      NullLogger<DapperWorkCoordinator>.Instance);

  [Test]
  public async Task AnUnknownEvent_IsNotArrivedAsync() {
    var eventId = Guid.CreateVersion7();

    var status = await _build().GetAppliedEventStatusAsync(new AppliedEventInquiry(PERSPECTIVE, eventId));

    await Assert.That(status!.Value.State).IsEqualTo(AppliedEventState.NotArrived);
    await Assert.That(status.Value.EventId).IsEqualTo(eventId);
  }

  [Test]
  public async Task ARecordedApply_ResolvedByStreamAndPosition_IsAppliedAsync() {
    var eventId = Guid.CreateVersion7();
    var streamId = Guid.CreateVersion7();
    await using (var conn = new NpgsqlConnection(ConnectionString)) {
      await conn.OpenAsync();
      await conn.ExecuteAsync(@"
        INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, version, created_at)
        VALUES (@e, @s, @s, 'Ledger.Tests.Order', 'Ledger.Tests.OrderShipped', 1, NOW());
        INSERT INTO wh_perspective_applied (event_id, perspective_name, stream_id, applied_at)
        VALUES (@e, @p, @s, NOW());", new { e = eventId, s = streamId, p = PERSPECTIVE });
    }

    var status = await _build().GetAppliedEventStatusAsync(new AppliedEventInquiry(PERSPECTIVE, null, streamId, 1));

    await Assert.That(status!.Value.State).IsEqualTo(AppliedEventState.Applied);
    await Assert.That(status.Value.EventId).IsEqualTo(eventId);
  }

  [Test]
  public async Task WithANullInquiry_ThrowsAsync() {
    await Assert.That(async () => await _build().GetAppliedEventStatusAsync(null!))
      .Throws<ArgumentNullException>();
  }
}
