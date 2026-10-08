// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Data.Postgres;

/// <summary>The database whose section the unnamed <see cref="PostgresOptions"/> bind from.</summary>
internal sealed record PostgresDefaultDatabase(string Name);

/// <summary>
/// The name an earlier release derived for <paramref name="Name"/>'s database, read while only that
/// name is configured.
/// </summary>
internal sealed record PostgresLegacyDatabaseName(string Name, string LegacyName);
