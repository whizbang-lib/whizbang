// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Archival;

namespace Whizbang.Core.Tests.Archival;

/// <summary>
/// <c>Whizbang:Perspectives:ArchivedStreamRows:&lt;Perspective&gt;</c> (#1739): bound by hand, by name only, so a value the
/// framework cannot use is refused at startup naming the key rather than read as Keep.
/// </summary>
/// <docs>fundamentals/events/archive-store#perspective-rows</docs>
/// <code-under-test>src/Whizbang.Core/Archival/ArchiveOptionsBinder.cs</code-under-test>
public class ArchivedStreamRowsOptionsBinderTests {
  private const string SECTION = "Whizbang:Perspectives:ArchivedStreamRows";

  private static IConfiguration _config(params (string Key, string Value)[] values) =>
    new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

  [Test]
  public async Task Bind_ReadsEachPerspectivesPolicyByName_IgnoringCaseAsync() {
    var options = new ArchivedStreamRowsOptions();

    ArchiveOptionsBinder.Bind(_config(
      ($"{SECTION}:Shop.OrderPerspective", "Drop"),
      ($"{SECTION}:Shop.CartPerspective", "archive"),
      ($"{SECTION}:Shop.Outer+InvoicePerspective", "KEEP")), options);

    await Assert.That(options.Perspectives["Shop.OrderPerspective"]).IsEqualTo(ArchivedStreamRowPolicy.Drop);
    await Assert.That(options.Perspectives["Shop.CartPerspective"]).IsEqualTo(ArchivedStreamRowPolicy.Archive);
    await Assert.That(options.Perspectives["Shop.Outer+InvoicePerspective"]).IsEqualTo(ArchivedStreamRowPolicy.Keep);
    await Assert.That(options.Perspectives).Count().IsEqualTo(3);
  }

  [Test]
  public async Task Bind_NothingConfigured_LeavesNoOverrideAsync() {
    var options = new ArchivedStreamRowsOptions();

    ArchiveOptionsBinder.Bind(_config(("Whizbang:Perspectives:Rewind:Enabled", "true")), options);

    await Assert.That(options.Perspectives).IsEmpty();
  }

  [Test]
  [Arguments("Shred")]
  [Arguments("1")]
  [Arguments("-1")]
  [Arguments("")]
  [Arguments("Drop, Archive")]
  public async Task Bind_AValueThatNamesNoPolicy_IsRefusedNamingTheKeyAsync(string value) {
    var options = new ArchivedStreamRowsOptions();

    var thrown = await Assert.That(() => ArchiveOptionsBinder.Bind(_config(($"{SECTION}:Shop.OrderPerspective", value)), options))
      .Throws<InvalidOperationException>();

    await Assert.That(thrown!.Message).Contains($"{SECTION}:Shop.OrderPerspective")
      .Because("a policy is bound by its name only; a number would bind it by position, which nobody reading the configuration can check");
    await Assert.That(thrown.Message).Contains("Keep, Drop, Archive");
  }

  [Test]
  public async Task Bind_ASectionInPlaceOfAValue_IsRefusedNamingTheKeyAsync() {
    var options = new ArchivedStreamRowsOptions();

    var thrown = await Assert.That(() => ArchiveOptionsBinder.Bind(
        _config(($"{SECTION}:Shop.OrderPerspective:Policy", "Drop")), options))
      .Throws<InvalidOperationException>();

    await Assert.That(thrown!.Message).Contains($"{SECTION}:Shop.OrderPerspective")
      .Because("the perspective's key holds its policy directly; a nested key is a mistake, not a Keep");
  }

  [Test]
  public async Task Bind_ANameTheStoredSettingCannotCarry_IsRefusedNamingTheKeyAsync() {
    var options = new ArchivedStreamRowsOptions();

    var thrown = await Assert.That(() => ArchiveOptionsBinder.Bind(_config(($"{SECTION}:Shop=Order", "Drop")), options))
      .Throws<InvalidOperationException>();

    await Assert.That(thrown!.Message).Contains($"{SECTION}:Shop=Order");
  }

  [Test]
  public async Task AddWhizbangArchiveOptions_BindsTheOverrides_FromTheHostsConfigurationAsync() {
    var services = new ServiceCollection();
    services.AddSingleton(_config(($"{SECTION}:Shop.OrderPerspective", "Drop")));
    services.AddWhizbangArchiveOptions();
    services.AddWhizbangArchiveOptions();
    var bare = new ServiceCollection();
    bare.AddWhizbangArchiveOptions();

    await using var provider = services.BuildServiceProvider();
    await using var bareProvider = bare.BuildServiceProvider();

    await Assert.That(provider.GetRequiredService<IOptions<ArchivedStreamRowsOptions>>().Value.Perspectives["Shop.OrderPerspective"])
      .IsEqualTo(ArchivedStreamRowPolicy.Drop);
    await Assert.That(bareProvider.GetRequiredService<IOptions<ArchivedStreamRowsOptions>>().Value.Perspectives).IsEmpty()
      .Because("a host with no configuration registered binds nothing and keeps every row");
  }
}
