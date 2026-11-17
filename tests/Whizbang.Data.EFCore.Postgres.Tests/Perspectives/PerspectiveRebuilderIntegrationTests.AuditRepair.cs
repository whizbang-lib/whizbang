// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Commands.System;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Serialization;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// #2344: the integrity audit's coverage-gap auto-repair. Service A's audit finds a stream its own perspective never
/// folded and rebuilds it in service A, through the same handling the rebuild command's receptor runs. Service B,
/// reached over the wire from A, receives no rebuild command: a command sent through the dispatcher is a system command,
/// broadcast to every service, and B would rebuild whatever of the perspective it hosts for a gap that was never its own.
/// </summary>
/// <docs>fundamentals/perspectives/rebuild#auto-repair-is-local</docs>
/// <code-under-test>src/Whizbang.Core/Workers/IntegrityAuditWorker.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/PerspectiveRebuildRequestHandler.cs</code-under-test>
public partial class PerspectiveRebuilderIntegrationTests {
  [Test]
  public async Task TheAuditsRepair_RebuildsItsOwnGap_AndPutsNothingOnTheWireAsync() {
    var gapStream = Guid.NewGuid();
    var wireToServiceB = new ServiceWire();
    await using var serviceA = _buildRebuildServices(services => {
      services.AddScoped<IDispatcher>(sp => new RecordingRebuildDispatcher(sp.GetRequiredService<PublishedEventLog>(), wireToServiceB));
      services.AddScoped<IWorkCoordinator>(sp => new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
        sp.GetRequiredService<WorkCoordinationDbContext>(), JsonContextRegistry.CreateCombinedOptions()));
      services.AddRebuildCommandHandling();
    });

    // Settled history service A's perspective never folded: no cursor, no pending work.
    await using (var appendScope = serviceA.CreateAsyncScope()) {
      await _appendEventAsync(appendScope.ServiceProvider.GetRequiredService<IEventStore>(), gapStream,
        new RebuildCreditedEvent { StreamId = gapStream, Amount = 10m });
    }
    await _makeACoverageGapAsync(gapStream, RebuildBalancePerspectiveName);

    var worker = new IntegrityAuditWorker(
      serviceA.GetRequiredService<IServiceScopeFactory>(),
      Options.Create(new StreamIntegrityOptions { RepairMode = IntegrityRepairMode.AutoRepairCapped, MaxAutoRebuildsPerAudit = 1 }),
      NullLogger<IntegrityAuditWorker>.Instance);
    await worker.RunAuditOnceAsync(CancellationToken.None);

    await using var verifyContext = CreateDbContext();
    var cursor = await verifyContext.Set<PerspectiveCursorRecord>().AsNoTracking()
      .FirstOrDefaultAsync(c => c.StreamId == gapStream && c.PerspectiveName == RebuildBalancePerspectiveName);
    await Assert.That(cursor).IsNotNull()
      .Because("the audit repairs the gap in the service that found it, through the rebuild handling registered there");
    await Assert.That(cursor!.Status).IsEqualTo(PerspectiveProcessingStatus.Completed);
    await Assert.That(wireToServiceB.Inbox.OfType<RebuildPerspectiveCommand>()).IsEmpty()
      .Because("the gap is service A's own: a rebuild command on the wire would make every service hosting the " +
               "perspective rebuild a stream it never found missing (#2344)");
    await Assert.That(wireToServiceB.Inbox.OfType<PerspectiveCoverageGapDetected>().Single().AutoRebuildRequested).IsTrue()
      .Because("the gap is still reported, and the report says a repair was made");
  }

  /// <summary>Ages and sequences the stream's events past the settle window and associates their type with the perspective.</summary>
  private async Task _makeACoverageGapAsync(Guid stream, string perspective) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      UPDATE wh_event_store
         SET created_at = NOW() - INTERVAL '2 hours',
             commit_sequence = COALESCE(commit_sequence, nextval('wh_commit_seq'))
       WHERE stream_id = @stream;
      INSERT INTO wh_message_associations (message_type, normalized_message_type, association_type, target_name, service_name)
      SELECT DISTINCT event_type, event_type, 'perspective', @perspective, 'service-a'
        FROM wh_event_store WHERE stream_id = @stream
      ON CONFLICT DO NOTHING;
      """;
    cmd.Parameters.AddWithValue(nameof(stream), stream);
    cmd.Parameters.AddWithValue(nameof(perspective), perspective);
    await cmd.ExecuteNonQueryAsync();
  }
}
