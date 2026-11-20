// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres;

namespace Whizbang.Core.Tests.Data;

/// <summary>
/// A query whose SQL returns a boolean by construction is read as that boolean, and anything else is an error rather
/// than a silent <see langword="false"/>.
/// </summary>
public class BooleanScalarTests {
  [Test]
  [Arguments(true)]
  [Arguments(false)]
  public async Task Read_ABoolean_ReturnsItAsync(bool value) {
    await Assert.That(BooleanScalar.Read(value, "probe")).IsEqualTo(value);
  }

  [Test]
  public async Task Read_NoValue_ThrowsNamingTheQueryAsync() {
    await Assert.That(() => BooleanScalar.Read(null, "advisory lock probe"))
      .Throws<InvalidOperationException>()
      .WithMessageContaining("advisory lock probe");
  }

  [Test]
  public async Task Read_ASqlNull_ThrowsAsync() {
    await Assert.That(() => BooleanScalar.Read(DBNull.Value, "probe"))
      .Throws<InvalidOperationException>();
  }
}
