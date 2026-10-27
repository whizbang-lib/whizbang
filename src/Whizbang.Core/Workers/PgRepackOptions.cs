// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Workers;

/// <summary>Who runs the pg_repack client when <see cref="TableRewriteStrategy.PgRepack"/> is selected.</summary>
/// <docs>operations/infrastructure/storage-maintenance#pg-repack</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/PgRepackTests.cs</tests>
public enum PgRepackMode {
  /// <summary>
  /// The default. The instance holding the maintainer duty runs the client for each queued table in the post-ready
  /// <c>Rewrite</c> step, so the client program must be on this host.
  /// </summary>
  InProcess,

  /// <summary>
  /// Whizbang never starts a process: it keeps the queue of tables that need a rewrite and reports it, and a job the
  /// operator runs (a container or a scheduled task) runs the client against those tables.
  /// </summary>
  External,
}

/// <summary>The table rewrite settings below <c>Whizbang:Workers:Maintenance:TableRewrite</c>.</summary>
/// <docs>operations/infrastructure/storage-maintenance#pg-repack</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/PgRepackTests.cs</tests>
public sealed class TableRewriteOptions {
  /// <summary>The pg_repack settings (<c>Whizbang:Workers:Maintenance:TableRewrite:PgRepack</c>).</summary>
  public PgRepackOptions PgRepack { get; } = new();
}

/// <summary>
/// How <see cref="TableRewriteStrategy.PgRepack"/> runs (<c>Whizbang:Workers:Maintenance:TableRewrite:PgRepack</c>).
/// </summary>
/// <docs>operations/infrastructure/storage-maintenance#pg-repack</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/PgRepackTests.cs</tests>
public sealed class PgRepackOptions {
  /// <summary>
  /// Who runs the client (<c>Whizbang:Workers:Maintenance:TableRewrite:PgRepack:Mode</c>). Default
  /// <see cref="PgRepackMode.InProcess"/>. Set explicitly to <c>InProcess</c>, a host without the client fails start;
  /// left at the default, it degrades with a warning and the tables stay queued.
  /// </summary>
  public PgRepackMode Mode { get; set; } = PgRepackMode.InProcess;

  /// <summary>
  /// The client program for <see cref="PgRepackMode.InProcess"/>
  /// (<c>Whizbang:Workers:Maintenance:TableRewrite:PgRepack:ClientPath</c>): a path, or a name looked up on
  /// <c>PATH</c>. Default <c>pg_repack</c>. It must be the same version as the pg_repack extension on the server.
  /// </summary>
  public string ClientPath { get; set; } = "pg_repack";
}
