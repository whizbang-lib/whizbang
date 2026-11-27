// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Messaging;

namespace Whizbang.Data.Postgres;

/// <summary>
/// What both Postgres work coordinators send and read to keep a retried inbox row on the derivation scheme its first run
/// ran under (#2633): the fetch that records and returns it (migration 740), the scheme this build stamps, and the
/// mapping of the stored number back to a scheme.
/// </summary>
/// <docs>fundamentals/identity/whizbang-ids#a-receptor-invoked-locally</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/FetchInboxBatchDerivationSchemeSqlTests.cs</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperWorkCoordinatorWithDataTests.cs:FetchInboxBatchAsync_ReturnsTheSchemeTheRowFirstRanUnderAsync</tests>
public static class InboxDerivationSchemeSql {
  /// <summary>The inbox fetch that records a row's first-run scheme and returns each row's (migration 740).</summary>
  public const string FETCH_FUNCTION = "fetch_inbox_batch_with_derivation_scheme";

  /// <summary>The number of the scheme this build runs a handling under, which the fetch stamps on a row's first run.</summary>
  public static short CurrentScheme => (short)ReceptorHandling.CurrentScheme;

  /// <summary>The scheme a fetched row records.</summary>
  /// <param name="stored">The row's <c>derivation_scheme</c>; the fetch returns 0 for a row a build before it ran first.</param>
  /// <returns>The scheme.</returns>
  public static HandlingDerivationScheme FromStored(short stored) => (HandlingDerivationScheme)stored;
}
