// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Core.Interfaces;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// How many tests of this project run at once (#2069), applied to every test by the assembly attribute in
/// <c>AssemblyInfo.cs</c>.
/// </summary>
/// <remarks>
/// <para>
/// Each test owns a database, so what tests share is the container's capacity, and this is sized to it. The
/// container allows <c>max_connections=500</c> (<see cref="Whizbang.Testing.Containers.SharedPostgresContainer"/>),
/// and every test process on a machine shares it: two build-server slots, or a CI shard plus nothing else.
/// </para>
/// <para>
/// TUnit's default is eight tests per processor, 96 on a twelve-core build server, which would ask the one
/// container for more connections than it has and spend most of the run in lock and checkpoint waits.
/// </para>
/// </remarks>
public sealed class EFCorePostgresParallelism : IParallelLimit {
  /// <summary>The most tests of this project that run at the same time in one process.</summary>
  public const int MAX_CONCURRENT_TESTS = 8;

  /// <inheritdoc />
  public int Limit => MAX_CONCURRENT_TESTS;
}
