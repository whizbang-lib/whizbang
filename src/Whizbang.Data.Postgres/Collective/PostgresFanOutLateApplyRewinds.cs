// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using NpgsqlTypes;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.Postgres.Collective;

/// <summary>
/// The Postgres <see cref="IFanOutLateApplyRewinds"/> (#2622): one call of <c>wh_rewind_fan_out_rows_applied_late</c>
/// (migration 760) per fan-out, which finds the rows the event targets that folded a newer own event, by the key column's
/// index, and rewinds each from its first own event committed after the foreign event.
/// </summary>
/// <param name="database">The service's database.</param>
/// <param name="time">The clock the rewind flags are stamped with; null for the system clock.</param>
/// <docs>fundamentals/perspectives/multi-stream#live-comes-out-as-replay</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/FanOutLateApplyRewindSqlTests.cs:ARowPastTheFanOut_IsRewoundFromItsFirstEventAfterIt_AndNoOtherRowIsAsync</tests>
public sealed class PostgresFanOutLateApplyRewinds(PostgresArchiveConnection database, TimeProvider? time) : IFanOutLateApplyRewinds {
  private readonly PostgresArchiveConnection _database = database ?? throw new ArgumentNullException(nameof(database));
  private readonly TimeProvider _time = time ?? TimeProvider.System;

  /// <inheritdoc/>
  public async Task<int> RewindRowsAppliedLateAsync(
      PerspectiveFanOutEntry fanOut, Guid streamId, string tenantId, Guid eventId, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(fanOut);
    ArgumentNullException.ThrowIfNull(tenantId);
    await using var connection = await _database.Open(cancellationToken).ConfigureAwait(false);
    await using var command = new NpgsqlCommand(
      "SELECT " + PgIdentifier.Qualify(_database.Schema, "wh_rewind_fan_out_rows_applied_late")
      + "(@table, @key, @perspective, @stream, @tenant, @event, @now)", connection);
    command.Parameters.AddWithValue("table", fanOut.TableName);
    command.Parameters.AddWithValue("key", fanOut.KeyColumn);
    command.Parameters.AddWithValue("perspective", fanOut.PerspectiveName);
    command.Parameters.AddWithValue("stream", streamId);
    command.Parameters.AddWithValue("tenant", tenantId);
    command.Parameters.AddWithValue("event", eventId);
    command.Parameters.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = _time.GetUtcNow() });
    return (int)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
  }
}
