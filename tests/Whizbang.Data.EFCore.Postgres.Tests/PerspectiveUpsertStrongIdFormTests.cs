// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The perspective upsert writes a strongly-typed id in the form the gate of the schema it writes to allows, not the
/// process's: a process that also writes to a schema still held (not yet migrated, or migrated by an earlier release)
/// writes the UUID string into an open schema and the object form into the held one (#2518).
/// </summary>
/// <docs>fundamentals/identity/whizbang-ids#stored-form-switch</docs>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/BaseUpsertStrategy.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Perspectives/WhizbangIdDocumentForm.cs</code-under-test>
[Category("Integration")]
[Category("Shard1")]
public class PerspectiveUpsertStrongIdFormTests : EFCoreTestBase {
  [Test]
  public async Task AnUpsert_WritesTheFormItsSchemasGateAllows_WhileAnotherSchemaIsHeldAsync() {
    var database = new NpgsqlConnectionStringBuilder(ConnectionString).Database;
    var (whileHeld, onceOpen) = ((Guid)TrackedGuid.New(), (Guid)TrackedGuid.New());

    StrongIdDocumentForms.Hold(database, "public");
    await _upsertAsync(whileHeld);
    StrongIdDocumentForms.Open(database, "public");
    StrongIdDocumentForms.Hold(database, "unmigrated");
    await _upsertAsync(onceOpen);

    await Assert.That(await _storedAsync(whileHeld)).Contains($"\"OrderId\": {{\"Value\": \"{whileHeld}\"}}");
    await Assert.That(await _storedAsync(onceOpen)).Contains($"\"OrderId\": \"{onceOpen}\"")
      .Because("the schema written to is open, whatever the process's other schemas say");
  }

  private async Task _upsertAsync(Guid streamId) {
    await using var context = CreateDbContext();
    var store = new EFCorePostgresPerspectiveStore<Order>(context, "wh_per_order", new PostgresUpsertStrategy());
    await store.UpsertAsync(streamId, new Order { OrderId = new TestOrderId(streamId), Amount = 5m, Status = "Created" });
  }

  private async Task<string> _storedAsync(Guid streamId) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT data::text FROM wh_per_order WHERE id = @id";
    cmd.Parameters.AddWithValue("id", streamId);
    return (string)(await cmd.ExecuteScalarAsync())!;
  }
}
