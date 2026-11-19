// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2145: the startup associations step writes the collective event types this service handles, read from the consumer
/// registry, as <c>collective</c> associations, plus the <c>collective_gate</c> marker that tells the store this service
/// gates. The step's hash covers the handled set, so a start whose handlers changed writes them again; a start that
/// cannot tell what it handles writes neither, which turns the gate off.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres.Generators/Templates/DbContextSchemaExtensionTemplate.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres.Generators/Templates/EFCorePerspectiveAssociationsTemplate.cs</code-under-test>
/// <docs>fundamentals/messaging/collective-events#only-handled-types-are-queued</docs>
[Category("Integration")]
[Category("Shard2")]
public class CollectiveAssociationRegistrationTests : EFCoreTestBase {
  private const string SERVICE = "Whizbang.Data.EFCore.Postgres.Tests";
  private const string ARCHIVE = "Contracts.ArchiveJobsCollective, Contracts";
  private const string PURGE = "Contracts.PurgeJobsCollective, Contracts";

  [Test]
  [Timeout(120000)]
  public async Task AKnownHandledSet_IsWrittenAsCollectiveAssociations_WithTheGateMarkerAsync(CancellationToken cancellationToken) {
    await _initializeAsync([ARCHIVE, PURGE], cancellationToken);

    await Assert.That(await _collectiveRowsAsync()).IsEquivalentTo([
      ("collective", ARCHIVE), ("collective", PURGE), ("collective_gate", "__collective__")]);
  }

  [Test]
  [Timeout(120000)]
  public async Task AChangedHandlerSet_ReWritesTheAssociations_OnTheNextStartAsync(CancellationToken cancellationToken) {
    // Nothing but the runtime handler set differs between the two starts: no table, migration or perspective changed,
    // so only a hash that covers the set sends the second start through the associations step.
    await _initializeAsync([ARCHIVE], cancellationToken);
    await _initializeAsync([PURGE], cancellationToken);

    await Assert.That(await _collectiveRowsAsync()).IsEquivalentTo([
      ("collective", PURGE), ("collective_gate", "__collective__")])
      .Because("a handler removed since the last start must stop being queued, and one added must start");
  }

  [Test]
  [Timeout(120000)]
  public async Task AStartThatCannotTellWhatItHandles_RemovesTheGate_SoEveryRowIsCreatedAgainAsync(CancellationToken cancellationToken) {
    await _initializeAsync([ARCHIVE], cancellationToken);
    await _initializeAsync(null, cancellationToken);

    await Assert.That(await _collectiveRowsAsync()).IsEmpty()
      .Because("an unknown handled set must never leave an earlier answer gating the store");
  }

  [Test]
  [Timeout(120000)]
  public async Task AnUnchangedHandlerSet_SkipsTheAssociationsStepAsync(CancellationToken cancellationToken) {
    await _initializeAsync([ARCHIVE], cancellationToken);
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);
    await conn.ExecuteAsync("UPDATE wh_message_associations SET updated_at = '2001-01-01' WHERE association_type = 'collective'");

    await _initializeAsync([ARCHIVE], cancellationToken);

    var touched = await conn.QueryFirstAsync<DateTime>(
      "SELECT updated_at FROM wh_message_associations WHERE association_type = 'collective'");
    await Assert.That(touched.Year).IsEqualTo(2001).Because("the same set hashes the same, so the step is not run again");
  }

  private async Task _initializeAsync(IReadOnlyList<string>? handled, CancellationToken cancellationToken) {
    var services = new ServiceCollection();
    services.AddSingleton<IConsumerRegistry>(new FixedCollectiveRegistry(handled));
    await using var provider = services.BuildServiceProvider();
    await using var context = CreateDbContext();
    await context.EnsureWhizbangDatabaseInitializedAsync(serviceProvider: provider, cancellationToken: cancellationToken);
  }

  private async Task<List<(string Kind, string Type)>> _collectiveRowsAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      SELECT association_type, message_type FROM wh_message_associations
      WHERE service_name = @service AND association_type IN ('collective', 'collective_gate') AND target_name = '__collective__'
      """;
    cmd.Parameters.AddWithValue("service", SERVICE);
    var rows = new List<(string, string)>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      rows.Add((reader.GetString(0), reader.GetString(1)));
    }
    return rows;
  }

  private sealed class FixedCollectiveRegistry(IReadOnlyList<string>? handled) : IConsumerRegistry {
    public bool HasAnyConsumer(string messageType) => false;
    public IReadOnlyList<string>? GetHandledCollectiveEventTypes() => handled;
  }
}
