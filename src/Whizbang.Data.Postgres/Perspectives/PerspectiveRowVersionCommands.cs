// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Whizbang.Core.Generated;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.Postgres.Perspectives;

/// <summary>
/// The statements and the parameter shape a checked perspective write is built from, shared by both
/// PostgreSQL drivers.
/// </summary>
/// <remarks>
/// <para>
/// A perspective row's version is its <c>xmin</c>: the id of the transaction that wrote the row's
/// current version. Every <c>UPDATE</c> produces a new one, whichever path issued it, so a write
/// conditioned on <c>xmin</c> lands only on the row the apply actually read. A locking read does not
/// move it and a rolled-back update does not keep it, which is what makes it usable as a version
/// rather than merely as a counter.
/// </para>
/// <para>
/// Shared rather than written twice. The two drivers reach a connection differently -- one through a
/// DbContext, one through a connection string -- but the SQL, the reader mapping and the ordering
/// guard are the semantics themselves, and a second copy of those is a second thing to be wrong.
/// The Dapper store served the interface defaults and did no checking at all, so a per-stream apply
/// computed from a stale read could overwrite a concurrent collective write there while the same
/// apply was refused under Entity Framework.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/perspectives#concurrent-writers</docs>
public static class PerspectiveRowVersionCommands {
  /// <summary>
  /// The read a per-stream apply makes before it loads the model: the row's version together with
  /// the metadata the runner's idempotency filter reads and the row's scope, in one statement.
  /// </summary>
  /// <param name="qualifiedTable">The quoted, schema-qualified perspective table.</param>
  /// <returns>The statement, taking a single <c>@id</c> parameter.</returns>
  public static string ReadForApplySql(string qualifiedTable) {
    ArgumentException.ThrowIfNullOrWhiteSpace(qualifiedTable);
    return "SELECT xmin, metadata->>'EventId', metadata->>'EventType', metadata->>'CommitSequence', scope::text FROM "
      + qualifiedTable + " WHERE id = @id";
  }

  /// <summary>The read of a row's current version alone.</summary>
  /// <param name="qualifiedTable">The quoted, schema-qualified perspective table.</param>
  /// <param name="lockRow">
  /// Hold the row for the rest of the caller's transaction (<c>FOR UPDATE</c>), so no writer can
  /// move it between this check and the write that follows.
  /// </param>
  /// <returns>The statement, taking a single <c>@id</c> parameter.</returns>
  public static string ReadVersionSql(string qualifiedTable, bool lockRow) {
    ArgumentException.ThrowIfNullOrWhiteSpace(qualifiedTable);
    return "SELECT xmin FROM " + qualifiedTable + " WHERE id = @id" + (lockRow ? " FOR UPDATE" : string.Empty);
  }

  /// <summary>
  /// Whether a write orders on the event id: a model marked <see cref="IVersionedApplyTarget"/>, written by
  /// anything but a rebuild.
  /// </summary>
  /// <remarks>
  /// A rebuild re-derives the row from every event of the stream, folded from an empty model in stream-version
  /// order, so its write carries the event id of the stream's last event by version. Where events were appended out
  /// of event-id order, that id sorts below the one the stored row records although the rebuild folded both, and
  /// ordering on it refused the only write that could repair the row (#1819). The question the event-id guard asks,
  /// "has the row already moved past this event?", has no meaning for a write that re-derived the row from every
  /// event. A rebuild's write therefore takes the commit-sequence guard every other model's rebuild takes, on the
  /// position a rebuild records as the highest among the events it folded (#1352). A stale read is not what either
  /// guard stops: the checked write still refuses it on the row version, in every mode. The live apply and the
  /// rewind keep ordering on the event id.
  /// </remarks>
  /// <param name="isVersionedTarget">Whether the model opts into event-id ordering.</param>
  /// <param name="mode">The ambient processing mode of the write, null for live processing.</param>
  /// <returns>True when the write is ordered on the event id rather than the commit sequence.</returns>
  /// <docs>fundamentals/perspectives/versioned-apply</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/VersionedRebuildOrderingTests.cs:RebuildStreamsAsync_VersionedModelWithEventsAppendedOutOfIdOrder_WritesTheFoldedRowAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/PerspectiveRowVersionCommandsTests.cs</tests>
  public static bool OrdersOnEventId(bool isVersionedTarget, ProcessingMode? mode) =>
    isVersionedTarget && mode is not ProcessingMode.Rebuild;

  /// <summary>
  /// The ordering guard a write is conditioned on, so an event the row has already moved past is
  /// skipped rather than applied backwards.
  /// </summary>
  /// <remarks>
  /// A write that orders on the event id (see <see cref="OrdersOnEventId"/>: a model marked
  /// <see cref="IVersionedApplyTarget"/>, outside a rebuild) is guarded on the event id; everything else
  /// orders on the commit sequence and treats an absent sequence on either side as no opinion. The
  /// guard is written against whatever expression carries the incoming metadata, which differs
  /// between the two statement shapes: <c>EXCLUDED.metadata</c> in an upsert, the bound parameter in
  /// a conditional update.
  /// <para>
  /// Both forms admit a write AT the stored position, not only past it (#1608). A rewind or a rebuild re-folds the
  /// stream and records the stream's frontier, which is the position the row already holds whenever the late event
  /// sits below the newest one. Refusing it on the event id refused every correction of a row that had applied its
  /// newest event first, and the late event was then reported applied while the row never received it. A stale
  /// read is not what this guard stops: the checked write refuses it on the row version.
  /// </para>
  /// <para>
  /// A rebuild's write is never guarded on the event id (#1819): it ends on the stream's last event by version,
  /// whose id can sort below the stored one when events were appended out of event-id order.
  /// </para>
  /// </remarks>
  /// <param name="qualifiedTable">The quoted, schema-qualified perspective table.</param>
  /// <param name="incoming">The SQL expression for the incoming metadata.</param>
  /// <param name="isVersionedTarget">
  /// Whether the write orders on the event id; callers pass <see cref="OrdersOnEventId"/>.
  /// </param>
  /// <returns>A boolean SQL expression, true when the write may proceed.</returns>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/PerspectiveLateEventApplyTests.cs:TheLaterEventCommitsFirst_EveryRowStillHoldsBothEventsAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/VersionedApplyTargetTests.cs:RebuildWriteWithAnEarlierEventId_OnVersionedTarget_LandsAsync</tests>
  public static string OrderingGuard(string qualifiedTable, string incoming, bool isVersionedTarget) {
    ArgumentException.ThrowIfNullOrWhiteSpace(qualifiedTable);
    ArgumentException.ThrowIfNullOrWhiteSpace(incoming);
    return isVersionedTarget
      ? $"{qualifiedTable}.metadata->>'EventId' IS NULL "
        + $"OR {incoming}->>'EventId' >= {qualifiedTable}.metadata->>'EventId'"
      : $"{qualifiedTable}.metadata->>'CommitSequence' IS NULL "
        + $"OR {incoming}->>'CommitSequence' IS NULL "
        + $"OR ({incoming}->>'CommitSequence')::bigint >= ({qualifiedTable}.metadata->>'CommitSequence')::bigint";
  }

  /// <summary>
  /// The apply read, mapped from a reader positioned on <see cref="ReadForApplySql"/>'s result.
  /// </summary>
  /// <param name="reader">The reader, not yet advanced.</param>
  /// <param name="cancellationToken">Cancels the read.</param>
  /// <returns>
  /// <see cref="PerspectiveRowVersion.Absent"/> with no metadata when there is no row; otherwise the
  /// row's version, the metadata the idempotency filter reads, and the row's scope (null when it has none).
  /// </returns>
  public static async Task<PerspectiveApplyRead> ReadApplyAsync(
      DbDataReader reader, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(reader);
    if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      return new PerspectiveApplyRead(PerspectiveRowVersion.Absent, null);
    }
    var metadata = new PerspectiveMetadata {
      EventId = await _textAsync(reader, 1, cancellationToken).ConfigureAwait(false) ?? string.Empty,
      EventType = await _textAsync(reader, 2, cancellationToken).ConfigureAwait(false) ?? string.Empty,
      CommitSequence = await _textAsync(reader, 3, cancellationToken).ConfigureAwait(false) is { } sequence
        ? long.Parse(sequence, NumberStyles.Integer, CultureInfo.InvariantCulture)
        : null,
    };
    var xmin = await reader.GetFieldValueAsync<uint>(0, cancellationToken).ConfigureAwait(false);
    // A perspective row's scope column is NOT NULL and always holds an object, in both drivers.
    var scope = JsonSerializer.Deserialize(reader.GetString(4), InfrastructureJsonContext.Default.PerspectiveScope);
    return new PerspectiveApplyRead(PerspectiveRowVersion.Of(xmin), metadata, scope);
  }

  /// <summary>
  /// The parameter a conditional write binds its expected version to. <c>xmin</c> is an <c>xid</c>,
  /// and the version stored its unsigned value.
  /// </summary>
  /// <param name="name">The parameter name, without the marker.</param>
  /// <param name="expected">The version the write must land on.</param>
  /// <returns>The bound parameter.</returns>
  public static NpgsqlParameter ExpectedVersionParameter(string name, PerspectiveRowVersion expected) =>
    new(name, NpgsqlDbType.Xid) { Value = (uint)expected.Value };

  private static async ValueTask<string?> _textAsync(
      DbDataReader reader, int ordinal, CancellationToken cancellationToken) =>
    await reader.IsDBNullAsync(ordinal, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(ordinal);
}
