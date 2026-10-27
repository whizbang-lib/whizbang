// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Whizbang.Data.EFCore.Custom;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// A context that owns a schema of its own, the shape of a service that shares one database with
/// others. Every other context in this project migrates <c>public</c>, where a schema-qualified and
/// a search-path-resolved reference are the same thing, so only this one can tell them apart.
/// </summary>
[WhizbangDbContext("named-schema", Schema = "named_svc")]
public partial class NamedSchemaDbContext(DbContextOptions<NamedSchemaDbContext> options) : DbContext(options) {
  /// <summary>The schema this context migrates.</summary>
  public const string SCHEMA = "named_svc";
}
