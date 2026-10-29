// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;

namespace Whizbang.Data.Postgres;

/// <summary>
/// How an archive store reaches the service's own database: a connection opener and the schema the framework's tables
/// live in. Both Postgres drivers register one, and build the built-in <see cref="PostgresTableArchiveStore"/> from it,
/// so a store that keeps part of its state in the database (the Azure Blob store keeps its manifest there and moves
/// events through <c>wh_event_archive</c>) reaches the same database the same way on either driver.
/// </summary>
/// <param name="Open">Opens a connection to the service's database. The caller owns and disposes it.</param>
/// <param name="Schema">The schema, unquoted; null resolves the tables through the connection's search path (the Dapper
/// driver), as the driver's other stores do.</param>
/// <docs>fundamentals/events/archive-store#the-postgres-table-store</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveStoreSqlTests.cs:PrefixTruncate_ThroughTheDriversRegistration_ArchivesThroughTheStoreAsync</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperArchiveStoreTests.cs:AddWhizbangPostgres_RegistersTheTableArchiveStore_AndTheTruncateWritesThroughItAsync</tests>
public sealed record PostgresArchiveConnection(Func<CancellationToken, ValueTask<NpgsqlConnection>> Open, string? Schema);
