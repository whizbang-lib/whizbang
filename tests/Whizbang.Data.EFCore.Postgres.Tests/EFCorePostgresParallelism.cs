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
/// The budget: 20 connections per running test (most hold 2 to 6; a few hosts with every worker running reach 65), two
/// processes sharing a container at most, and 100 left for the server and other projects' setup, so
/// 8 x 20 x 2 + 100 = 420 of 500. <c>ParallelismPolicyGuardTests</c> checks that sum against the live container. The
/// measured peak across a whole build server, two shards at once, was 98.
/// </para>
/// <para>
/// Eight rather than more because the tests wait on PostgreSQL, which runs on the same machine: a CI runner has four
/// processors, a build-server slot six, and two tests per processor keeps the database busy without queuing every
/// statement behind a lock or a checkpoint. TUnit's default is eight tests per processor, 96 on a twelve-core build
/// server.
/// </para>
/// </remarks>
public sealed class EFCorePostgresParallelism : IParallelLimit {
  /// <summary>The most tests of this project that run at the same time in one process.</summary>
  public const int MAX_CONCURRENT_TESTS = 8;

  /// <inheritdoc />
  public int Limit => MAX_CONCURRENT_TESTS;
}
