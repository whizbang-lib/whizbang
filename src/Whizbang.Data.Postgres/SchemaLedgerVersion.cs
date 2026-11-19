// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using Whizbang.Core.Versioning;

namespace Whizbang.Data.Postgres;

/// <summary>
/// What the migration ledger (<c>wh_schema_versions.application_version</c>) records for the application (#2027): the
/// application version as the instance rows record it, so both versions of whoever migrated can be compared with the
/// fleet's. Both drivers read it here.
/// </summary>
/// <docs>operations/infrastructure/migrations</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SchemaLedgerApplicationVersionTests.cs</tests>
public static class SchemaLedgerVersion {
  /// <summary>What the ledger records for an application version nobody knows. Never NULL: the ledger's unique key
  /// treats NULLs as distinct, so a NULL would add a row on every pass.</summary>
  public const string UNKNOWN = "unknown";

  /// <summary>The application version the host's services resolve, as recorded; null when none is known.</summary>
  /// <param name="services">The host's services, or null.</param>
  /// <returns>The recorded text, or null.</returns>
  public static string? ApplicationVersionOf(IServiceProvider? services) =>
    (services?.GetService(typeof(IApplicationVersionProvider)) as IApplicationVersionProvider)?.ApplicationVersion.Recorded;
}
