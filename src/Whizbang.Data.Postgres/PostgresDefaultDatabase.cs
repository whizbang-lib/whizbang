// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Data.Postgres;

/// <summary>The database whose section the unnamed <see cref="PostgresOptions"/> bind from.</summary>
internal sealed record PostgresDefaultDatabase(string Name);
