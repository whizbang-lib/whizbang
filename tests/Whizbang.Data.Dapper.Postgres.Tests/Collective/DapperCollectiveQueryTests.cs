// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Dapper.Postgres.Collective;

namespace Whizbang.Data.Dapper.Postgres.Tests.Collective;

/// <summary>
/// The sibling-table lookup a cross-perspective cohort is projected through. No database is used in
/// this file.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Dapper.Postgres/Collective/DapperCollectiveQuery.cs</code-under-test>
public class DapperCollectiveQueryTests {
  private sealed class OrderModel;

  private sealed class UnregisteredModel;

  [Test]
  public async Task TableFor_RegisteredModel_ReturnsItsTableAsync() {
    var query = new DapperCollectiveQuery(new Dictionary<Type, string> { [typeof(OrderModel)] = "wh_per_order" });

    await Assert.That(query.TableFor(typeof(OrderModel))).IsEqualTo("wh_per_order");
  }

  [Test]
  public async Task TableFor_UnregisteredModel_FailsNamingTheModelAndTheFixAsync() {
    // A handler's Where that reads q.Of<T>() for a model with no table cannot be projected at all.
    // Guessing a table would scope the cohort by the wrong rows, so it fails, and the message names
    // the model and how to register it.
    var query = new DapperCollectiveQuery(new Dictionary<Type, string> { [typeof(OrderModel)] = "wh_per_order" });

    await Assert.That(() => query.TableFor(typeof(UnregisteredModel)))
      .Throws<InvalidOperationException>()
      .WithMessageContaining("UnregisteredModel");
  }
}
