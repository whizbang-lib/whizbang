// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Sequencing;
using Whizbang.Data.Dapper.Sqlite;
using Whizbang.Testing.Contracts;

namespace Whizbang.Data.Tests;

/// <summary>
/// Integration tests for DapperSequenceProvider using SQLite.
/// Inherits all contract tests from SequenceProviderContractTests.
/// </summary>
[InheritsTests]
public sealed class DapperSequenceProviderTests : SequenceProviderContractTests {
  private DapperTestBase _testBase = null!;

  [Before(Test)]
  public async Task SetupAsync() {
    _testBase = new TestFixture();
    await _testBase.SetupAsync();
  }

  [After(Test)]
  public void Cleanup() {
    _testBase?.Cleanup();
  }

  protected override ISequenceProvider CreateProvider() {
    return new DapperSqliteSequenceProvider(_testBase.ConnectionFactory, _testBase.Executor);
  }

  private sealed class TestFixture : DapperTestBase;
}
