// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.EFCore.Postgres.Functions;

namespace Whizbang.Data.EFCore.Postgres.Tests.BranchCoverage.EFCore;

/// <summary>
/// Branch coverage for <c>WhizbangOptionsExtensionInfo.ShouldUseSameServiceProvider</c>: two
/// extensions with the same translator plugins share EF Core's internal service provider; a
/// different plugin set, or another provider's extension entirely, does not.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/Functions/WhizbangDbContextOptionsExtensions.cs</code-under-test>
[Category("Shard3")]
public class OptionsExtensionInfoTests {

  [Test]
  public async Task ShouldUseSameServiceProvider_OnlyForTheSamePluginSetAsync() {
    var plain = new WhizbangOptionsExtension();
    var withPlugin = plain.WithConsumerPlugin(typeof(OptionsExtensionInfoTests), _ => { });
    var npgsqlExtension = new DbContextOptionsBuilder().UseNpgsql("Host=localhost;Database=never_opened").Options
      .Extensions.First(e => e is not WhizbangOptionsExtension);

    await Assert.That(plain.Info.ShouldUseSameServiceProvider(new WhizbangOptionsExtension().Info)).IsTrue()
      .Because("identical plugin sets build identical internal service providers");
    await Assert.That(plain.Info.ShouldUseSameServiceProvider(withPlugin.Info)).IsFalse()
      .Because("a different plugin set registers different translators");
    await Assert.That(plain.Info.ShouldUseSameServiceProvider(npgsqlExtension.Info)).IsFalse()
      .Because("another extension's info is never the same Whizbang configuration");
  }
}
