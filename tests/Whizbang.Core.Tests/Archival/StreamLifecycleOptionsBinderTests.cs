// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Archival;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Archival;

/// <summary>
/// <c>Whizbang:Archive:ReadThrough</c> and <c>Whizbang:StreamLifecycle:Types:&lt;StreamType&gt;:AppendToArchived</c>
/// (#1738): bound by hand, so a value the framework cannot use is refused at startup naming the key, and
/// <c>NewGeneration</c>, which ships with #1734, is refused with a message that says so rather than read as a default.
/// </summary>
/// <docs>fundamentals/events/archive-store#configuration</docs>
public class StreamLifecycleOptionsBinderTests {
  private static IConfiguration _config(params (string Key, string Value)[] values) =>
    new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

  [Test]
  public async Task ReadThrough_DefaultsToTrue_AndBindsFalseAsync() {
    var unset = new ArchiveOptions();
    ArchiveOptionsBinder.Bind(_config(), unset);
    var off = new ArchiveOptions();
    ArchiveOptionsBinder.Bind(_config(("Whizbang:Archive:ReadThrough", "false")), off);

    await Assert.That(unset.ReadThrough).IsTrue()
      .Because("an archived stream should load, rewind and rebuild to the same model as a hot one unless someone opts out");
    await Assert.That(off.ReadThrough).IsFalse();
  }

  [Test]
  public async Task ReadThrough_AValueThatIsNotABoolean_IsRefusedNamingTheKeyAsync() {
    var options = new ArchiveOptions();

    var thrown = await Assert.That(() => ArchiveOptionsBinder.Bind(_config(("Whizbang:Archive:ReadThrough", "sometimes")), options))
      .Throws<InvalidOperationException>();

    await Assert.That(thrown!.Message).Contains("Whizbang:Archive:ReadThrough");
    await Assert.That(thrown.Message).Contains("sometimes");
  }

  [Test]
  public async Task AppendToArchived_IsReadPerStreamType_IgnoringCase_AndAnUnlistedTypeRefusesAsync() {
    var options = new StreamLifecycleOptions();

    ArchiveOptionsBinder.Bind(_config(
      ("Whizbang:StreamLifecycle:Types:Shop.Order:AppendToArchived", "AutoRestore"),
      ("Whizbang:StreamLifecycle:Types:Shop.Cart:AppendToArchived", "refuse"),
      ("Whizbang:StreamLifecycle:Types:Shop.Invoice:SomethingElse", "x")), options);

    await Assert.That(options.Types["Shop.Order"].AppendToArchived).IsEqualTo(AppendToArchivedPolicy.AutoRestore);
    await Assert.That(options.Types["Shop.Cart"].AppendToArchived).IsEqualTo(AppendToArchivedPolicy.Refuse);
    await Assert.That(options.Types["Shop.Invoice"].AppendToArchived).IsEqualTo(AppendToArchivedPolicy.Refuse)
      .Because("a listed type that does not set the policy takes the default, Refuse");
    await Assert.That(options.AppendToArchivedPolicies()).IsEquivalentTo([
      new StreamTypePolicy("Shop.Cart", AppendToArchivedPolicy.Refuse),
      new StreamTypePolicy("Shop.Invoice", AppendToArchivedPolicy.Refuse),
      new StreamTypePolicy("Shop.Order", AppendToArchivedPolicy.AutoRestore),
    ]);
    await Assert.That(new StreamLifecycleOptions().AppendToArchivedPolicies()).IsEmpty();
  }

  [Test]
  public async Task AppendToArchived_NewGeneration_IsRefusedAtStartup_Naming1734Async() {
    var options = new StreamLifecycleOptions();

    var thrown = await Assert.That(() => ArchiveOptionsBinder.Bind(
        _config(("Whizbang:StreamLifecycle:Types:Shop.Order:AppendToArchived", "NewGeneration")), options))
      .Throws<InvalidOperationException>();

    await Assert.That(thrown!.Message).Contains("Whizbang:StreamLifecycle:Types:Shop.Order:AppendToArchived");
    await Assert.That(thrown.Message).Contains("#1734")
      .Because("NewGeneration starts a new generation from the final snapshot finalization takes, which ships with #1734");
  }

  [Test]
  public async Task AppendToArchived_AnUnknownValueOrANumber_IsRefusedNamingTheKeyAndTheValuesAsync() {
    foreach (var value in new[] { "Restore", "1", "" }) {
      var thrown = await Assert.That(() => ArchiveOptionsBinder.Bind(
          _config(("Whizbang:StreamLifecycle:Types:Shop.Order:AppendToArchived", value)), new StreamLifecycleOptions()))
        .Throws<InvalidOperationException>();

      await Assert.That(thrown!.Message).Contains("Whizbang:StreamLifecycle:Types:Shop.Order:AppendToArchived");
      await Assert.That(thrown.Message).Contains("Refuse, AutoRestore")
        .Because($"'{value}' names no policy; a number would bind a policy by position, which a reader cannot check");
    }
  }

  [Test]
  public async Task Registration_BindsBothOptionsFromTheHostsConfiguration_AndValidatesAtStartAsync() {
    var services = new ServiceCollection();
    services.AddSingleton(_config(
      ("Whizbang:Archive:ReadThrough", "false"),
      ("Whizbang:StreamLifecycle:Types:Shop.Order:AppendToArchived", "AutoRestore")));
    services.AddWhizbangWorkers();
    await using var provider = services.BuildServiceProvider();

    await Assert.That(provider.GetRequiredService<IOptions<ArchiveOptions>>().Value.ReadThrough).IsFalse();
    await Assert.That(provider.GetRequiredService<IOptions<StreamLifecycleOptions>>().Value.Types["Shop.Order"].AppendToArchived)
      .IsEqualTo(AppendToArchivedPolicy.AutoRestore);
  }

  [Test]
  public async Task Registration_APolicyThatCannotBeRead_FailsTheHostsStartupValidationAsync() {
    var services = new ServiceCollection();
    services.AddSingleton(_config(("Whizbang:StreamLifecycle:Types:Shop.Order:AppendToArchived", "NewGeneration")));
    services.AddWhizbangWorkers();
    await using var provider = services.BuildServiceProvider();
    Exception? failure = null;

    try {
      provider.GetRequiredService<IStartupValidator>().Validate();
    } catch (InvalidOperationException ex) {
      failure = ex;
    }

    await Assert.That(failure).IsNotNull()
      .Because("a policy that cannot be read fails the host's start, not the first archived write");
    await Assert.That(failure!.Message).Contains("#1734");
  }
}
