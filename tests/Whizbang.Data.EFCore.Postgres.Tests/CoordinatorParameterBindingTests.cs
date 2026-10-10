// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// How the coordinator binds its shared parameters. The digest filter: null means "do not filter",
/// and the SQL reads a NULL parameter that way, so a null filter must bind DBNull rather than an
/// empty array (which would match nothing). Id and name lists: an array parameter needs an array,
/// whatever collection the caller passed.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
[Category("Shard2")]
public class CoordinatorParameterBindingTests {
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

  [Test]
  public async Task AsArray_WithAnArray_BindsTheCallersArrayWithoutCopyingAsync() {
    Guid[] ids = [(Guid)TrackedGuid.New(), (Guid)TrackedGuid.New()];

    var bound = EFCoreWorkCoordinator<WorkCoordinationDbContext>.AsArray(ids);

    await Assert.That(ReferenceEquals(bound, ids)).IsTrue()
      .Because("an array is already what the parameter binds, so copying it is wasted work on a hot path");
  }

  [Test]
  public async Task AsArray_WithAList_CopiesItInOrderAsync() {
    var names = new List<string> { "Orders.OrderPlaced", "Orders.OrderShipped" };

    var bound = EFCoreWorkCoordinator<WorkCoordinationDbContext>.AsArray(names);

    await Assert.That(bound).IsEquivalentTo(names, TUnit.Assertions.Enums.CollectionOrdering.Matching);
  }
}
