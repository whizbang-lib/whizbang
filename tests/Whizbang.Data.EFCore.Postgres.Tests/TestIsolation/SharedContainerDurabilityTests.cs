// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests.TestIsolation;

/// <summary>
/// The shared test container does not wait on its disk: it runs without <c>fsync</c>, synchronous commit or full-page
/// writes (#2069).
/// </summary>
/// <remarks>
/// <para>
/// Tests run in parallel, and each creates and drops a database. Since PostgreSQL 15 a <c>DROP DATABASE</c> forces an
/// immediate checkpoint and waits for it, so a parallel shard asks for one every second or so, and with durability on
/// each one flushes and syncs everything every other test has written. On a build server whose disk is a shared
/// volume those checkpoints took up to 36 seconds (median 0.8), and three tests with a 60-second bound on a host's
/// startup ran out of time while the same shards passed on a server whose checkpoints took milliseconds.
/// </para>
/// <para>
/// A test database is thrown away with its container, so durability buys nothing here. The settings are applied each
/// time a process initializes the container, so a container created before this rule (the build servers keep theirs
/// between runs) gets them too.
/// </para>
/// </remarks>
[Category("Shard1")]
public class SharedContainerDurabilityTests {
  [Test]
  [Arguments("fsync")]
  [Arguments("synchronous_commit")]
  [Arguments("full_page_writes")]
  public async Task TheSharedContainer_DoesNotWaitOnItsDiskAsync(string setting, CancellationToken cancellationToken) {
    await SharedPostgresContainer.InitializeAsync(cancellationToken);
    await using var connection = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
    await connection.OpenAsync(cancellationToken);
    await using var show = new NpgsqlCommand($"SHOW {setting}", connection);

    await Assert.That((string?)await show.ExecuteScalarAsync(cancellationToken)).IsEqualTo("off")
      .Because($"{setting} makes every forced checkpoint wait on the disk, and a parallel shard forces one per dropped database");
  }
}
