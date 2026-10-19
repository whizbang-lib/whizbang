// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Data.Postgres;

/// <summary>
/// Settings for the Postgres extension probe, bound from <c>Whizbang:PostgresExtensions</c>.
/// </summary>
/// <remarks>
/// Not under <c>Whizbang:Postgres</c>, whose children are database names. A key present in
/// configuration overrides what code set; an absent key leaves it.
/// </remarks>
/// <docs>operations/infrastructure/postgres-extensions#configuration</docs>
/// <tests>tests/Whizbang.Core.Tests/PostgresExtensions/PostgresExtensionServiceCollectionExtensionsTests.cs</tests>
public sealed class PostgresExtensionsOptions {
  /// <summary>
  /// Whether the startup step probes each database (<c>Whizbang:PostgresExtensions:ProbeOnStartup</c>).
  /// Default <see langword="true"/>. When <see langword="false"/> the step is skipped and every
  /// extension reads <see cref="PostgresExtensionState.Unknown"/> until something calls
  /// <see cref="IPostgresCapabilities.RefreshAsync"/>.
  /// </summary>
  public bool ProbeOnStartup { get; set; } = true;
}
