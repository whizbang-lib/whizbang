// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Sequencing;
using Whizbang.Data.Dapper.Postgres;
using Whizbang.Testing.Contracts;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// Integration tests for DapperPostgresSequenceProvider using PostgreSQL.
/// Inherits all contract tests from SequenceProviderContractTests.
/// Each test gets its own isolated PostgreSQL container for parallel execution.
/// </summary>
[InheritsTests]
public sealed class DapperPostgresSequenceProviderTests : SequenceProviderContractTests {
  private PostgresTestBase _testBase = null!;

  [Before(Test)]
  public async Task SetupAsync() {
    _testBase = new TestFixture();
    await _testBase.SetupAsync();
  }

  [After(Test)]
  public async Task CleanupAsync() {
    if (_testBase != null) {
      await _testBase.TeardownAsync();
    }
  }

  protected override ISequenceProvider CreateProvider() {
    return new DapperPostgresSequenceProvider(_testBase.ConnectionFactory, _testBase.Executor);
  }

  private sealed class TestFixture : PostgresTestBase;
}
