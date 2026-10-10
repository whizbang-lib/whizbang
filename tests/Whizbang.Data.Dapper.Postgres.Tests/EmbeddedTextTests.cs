// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// The one reader every embedded migration text goes through. It reads compiled-in resources by
/// reflection; no database is used in this file.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/EmbeddedText.cs</code-under-test>
public class EmbeddedTextTests {
  private static readonly System.Reflection.Assembly _package = typeof(MigrationConstants).Assembly;

  [Test]
  public async Task Read_EmbeddedResource_ReturnsItsWholeTextAsync() {
    var name = _package.GetManifestResourceNames().First(n => n.EndsWith(".sql", StringComparison.Ordinal));

    var text = EmbeddedText.Read(_package, name, "unused");

    await Assert.That(text).IsNotEmpty()
      .Because("a migration script that reads back empty would apply nothing and report success");
  }

  [Test]
  public async Task Read_MissingResource_ThrowsTheCallersMessageAsync() {
    // A missing resource is a broken package. The caller's message says which file it was, which is
    // the difference between a fix and an investigation; a null stream read as text would surface as a
    // NullReferenceException that names nothing.
    var thrown = Assert.Throws<InvalidOperationException>(
      () => EmbeddedText.Read(_package, "Whizbang.Data.Postgres.Migrations.not-a-script.sql", "constants file is missing"));

    await Assert.That(thrown.Message).IsEqualTo("constants file is missing");
  }
}
