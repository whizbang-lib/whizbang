// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The digest queries' shared filter binding. Null means "do not filter", and the SQL reads a NULL
/// parameter that way, so a null filter must bind DBNull rather than an empty array (which would
/// match nothing).
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
[Category("Shard2")]
public class DigestFilterParamsTests {
  [Test]
  public async Task AddDigestFilterParams_WithNoFilter_BindsDbNullForOriginAndTypesAsync() {
    await using var cmd = new NpgsqlCommand();

    EFCoreWorkCoordinator<WorkCoordinationDbContext>.AddDigestFilterParams(cmd, originServiceId: null, eventTypes: null);

    await Assert.That(cmd.Parameters.Count).IsEqualTo(2);
    await Assert.That(cmd.Parameters["p_origin"].Value).IsEqualTo(DBNull.Value);
    await Assert.That(cmd.Parameters["p_types"].Value).IsEqualTo(DBNull.Value);
    await Assert.That(cmd.Parameters["p_types"].NpgsqlDbType).IsEqualTo(NpgsqlDbType.Array | NpgsqlDbType.Text);
  }

  [Test]
  public async Task AddDigestFilterParams_WithFilter_BindsOriginAndTypesAsync() {
    await using var cmd = new NpgsqlCommand();
    var origin = (Guid)TrackedGuid.New();
    string[] types = ["Orders.OrderPlaced", "Orders.OrderShipped"];

    EFCoreWorkCoordinator<WorkCoordinationDbContext>.AddDigestFilterParams(cmd, origin, types);

    await Assert.That(cmd.Parameters["p_origin"].Value).IsEqualTo(origin);
    await Assert.That(cmd.Parameters["p_origin"].NpgsqlDbType).IsEqualTo(NpgsqlDbType.Uuid);
    await Assert.That((string[])cmd.Parameters["p_types"].Value!).IsEquivalentTo(types);
  }
}
