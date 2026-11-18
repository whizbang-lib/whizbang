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
/// (#1738), <c>FinalizeGrace</c> and <c>SweepBatchSize</c> (#1734), and <c>Enabled</c> with each type's end-of-life policy
/// (#1740): bound by hand, so a value the framework cannot use is refused at startup naming the key rather than read as a
/// default.
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
  public async Task AppendToArchived_NewGeneration_IsAPolicy_AndIsStoredByItsNameAsync() {
    var options = new StreamLifecycleOptions();

    ArchiveOptionsBinder.Bind(_config(("Whizbang:StreamLifecycle:Types:Shop.Order:AppendToArchived", "newgeneration")), options);

    await Assert.That(options.Types["Shop.Order"].AppendToArchived).IsEqualTo(AppendToArchivedPolicy.NewGeneration)
      .Because("#1734 ships the final snapshot NewGeneration starts from, so the policy is no longer refused");
  }

  [Test]
  public async Task FinalizeGraceAndSweepBatchSize_DefaultToSevenDaysAnd500_AndBindAsync() {
    var unset = new StreamLifecycleOptions();
    ArchiveOptionsBinder.Bind(_config(), unset);
    var set = new StreamLifecycleOptions();
    ArchiveOptionsBinder.Bind(_config(
      ("Whizbang:StreamLifecycle:FinalizeGrace", "1.12:00:00"),
      ("Whizbang:StreamLifecycle:SweepBatchSize", "40")), set);
    var edges = new StreamLifecycleOptions();
    ArchiveOptionsBinder.Bind(_config(
      ("Whizbang:StreamLifecycle:FinalizeGrace", "00:00:00"),
      ("Whizbang:StreamLifecycle:SweepBatchSize", "1")), edges);

    await Assert.That(unset.FinalizeGrace).IsEqualTo(TimeSpan.FromDays(7));
    await Assert.That(unset.SweepBatchSize).IsEqualTo(500);
    await Assert.That(set.FinalizeGrace).IsEqualTo(TimeSpan.FromHours(36));
    await Assert.That(set.SweepBatchSize).IsEqualTo(40);
    await Assert.That(edges.FinalizeGrace).IsEqualTo(TimeSpan.Zero).Because("no grace is a choice: dispose on the next cycle");
    await Assert.That(edges.SweepBatchSize).IsEqualTo(1);
  }

  [Test]
  [Arguments("Whizbang:StreamLifecycle:FinalizeGrace", "soon")]
  [Arguments("Whizbang:StreamLifecycle:FinalizeGrace", "-00:00:01")]
  [Arguments("Whizbang:StreamLifecycle:SweepBatchSize", "0")]
  [Arguments("Whizbang:StreamLifecycle:SweepBatchSize", "many")]
  public async Task FinalizeGraceOrSweepBatchSize_ThatCannotBeUsed_IsRefusedNamingTheKeyAsync(string key, string value) {
    var thrown = await Assert.That(() => ArchiveOptionsBinder.Bind(_config((key, value)), new StreamLifecycleOptions()))
      .Throws<InvalidOperationException>();

    await Assert.That(thrown!.Message).Contains(key);
    await Assert.That(thrown.Message).Contains(value);
  }

  [Test]
  public async Task AppendToArchived_AnUnknownValueOrANumber_IsRefusedNamingTheKeyAndTheValuesAsync() {
    foreach (var value in new[] { "Restore", "1", "" }) {
      var thrown = await Assert.That(() => ArchiveOptionsBinder.Bind(
          _config(("Whizbang:StreamLifecycle:Types:Shop.Order:AppendToArchived", value)), new StreamLifecycleOptions()))
        .Throws<InvalidOperationException>();

      await Assert.That(thrown!.Message).Contains("Whizbang:StreamLifecycle:Types:Shop.Order:AppendToArchived");
      await Assert.That(thrown.Message).Contains("Refuse, AutoRestore, NewGeneration")
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
    services.AddSingleton(_config(("Whizbang:StreamLifecycle:Types:Shop.Order:AppendToArchived", "Restore")));
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
    await Assert.That(failure!.Message).Contains("Whizbang:StreamLifecycle:Types:Shop.Order:AppendToArchived");
  }
  // ===== #1740 (WS-3 S11): the end-of-life policies =====

  [Test]
  public async Task Enabled_DefaultsToFalse_AndBindsTrue_AndAValueThatIsNotABoolean_IsRefusedNamingTheKeyAsync() {
    var unset = new StreamLifecycleOptions();
    ArchiveOptionsBinder.Bind(_config(), unset);
    var on = new StreamLifecycleOptions();
    ArchiveOptionsBinder.Bind(_config(("Whizbang:StreamLifecycle:Enabled", "TRUE")), on);

    var thrown = await Assert.That(() => ArchiveOptionsBinder.Bind(_config(("Whizbang:StreamLifecycle:Enabled", "yes")), new StreamLifecycleOptions()))
      .Throws<InvalidOperationException>();

    await Assert.That(unset.Enabled).IsFalse()
      .Because("a policy archives or purges streams nobody sealed, so nothing is evaluated until a host turns it on");
    await Assert.That(on.Enabled).IsTrue();
    await Assert.That(thrown!.Message).Contains("Whizbang:StreamLifecycle:Enabled");
    await Assert.That(thrown.Message).Contains("yes");
  }

  [Test]
  public async Task EachTypesPolicy_BindsItsTriggersAndAction_AndATypeWithNoTrigger_HasNoPolicyAsync() {
    var options = new StreamLifecycleOptions();

    ArchiveOptionsBinder.Bind(_config(
      ("Whizbang:StreamLifecycle:Types:Shop.Order:IdleTtl", "90.00:00:00"),
      ("Whizbang:StreamLifecycle:Types:Shop.Order:MaxAge", "365.00:00:00"),
      ("Whizbang:StreamLifecycle:Types:Shop.Order:Action", "purge"),
      ("Whizbang:StreamLifecycle:Types:Shop.Ledger:KeepTailVersions", "1000"),
      ("Whizbang:StreamLifecycle:Types:Shop.Ledger:TenantByteCap", "1073741824"),
      ("Whizbang:StreamLifecycle:Types:Shop.Aged:MaxAge", "30.00:00:00"),
      ("Whizbang:StreamLifecycle:Types:Shop.Capped:TenantByteCap", "2048"),
      ("Whizbang:StreamLifecycle:Types:Shop.Cart:AppendToArchived", "AutoRestore")), options);

    await Assert.That(options.Types["Shop.Order"].IdleTtl).IsEqualTo(TimeSpan.FromDays(90));
    await Assert.That(options.Types["Shop.Order"].MaxAge).IsEqualTo(TimeSpan.FromDays(365));
    await Assert.That(options.Types["Shop.Order"].Action).IsEqualTo(StreamLifecycleAction.Purge);
    await Assert.That(options.Types["Shop.Ledger"].KeepTailVersions).IsEqualTo(1000L);
    await Assert.That(options.Types["Shop.Ledger"].TenantByteCap).IsEqualTo(1073741824L);
    await Assert.That(options.Types["Shop.Ledger"].Action).IsEqualTo(StreamLifecycleAction.Archive)
      .Because("the default action archives: it is reversible and keeps the stream readable, as for a sealed stream");
    await Assert.That(options.Types["Shop.Cart"].IdleTtl).IsNull();
    await Assert.That(options.EndOfLifePolicies()).IsEquivalentTo([
      new StreamEndOfLifePolicy("Shop.Aged", null, TimeSpan.FromDays(30), null, null, StreamLifecycleAction.Archive),
      new StreamEndOfLifePolicy("Shop.Capped", null, null, null, 2048, StreamLifecycleAction.Archive),
      new StreamEndOfLifePolicy("Shop.Ledger", null, null, 1000, 1073741824, StreamLifecycleAction.Archive),
      new StreamEndOfLifePolicy("Shop.Order", TimeSpan.FromDays(90), TimeSpan.FromDays(365), null, null, StreamLifecycleAction.Purge),
    ], TUnit.Assertions.Enums.CollectionOrdering.Matching);
  }

  [Test]
  public async Task ActionFor_IsTheTypesAction_OrArchive_AndEachActionMapsToItsDispositionAsync() {
    var options = new StreamLifecycleOptions();
    options.Types["Shop.Order"] = new StreamTypeLifecycleOptions { Action = StreamLifecycleAction.Compact };

    await Assert.That(options.ActionFor("Shop.Order")).IsEqualTo(StreamLifecycleAction.Compact);
    await Assert.That(options.ActionFor("Shop.Cart")).IsEqualTo(StreamLifecycleAction.Archive);
    await Assert.That(options.ActionFor(null)).IsEqualTo(StreamLifecycleAction.Archive);
    await Assert.That(StreamLifecycleAction.Archive.ToDisposition()).IsEqualTo(Whizbang.Core.Lifecycle.Disposition.Archive);
    await Assert.That(StreamLifecycleAction.Compact.ToDisposition()).IsEqualTo(Whizbang.Core.Lifecycle.Disposition.Compact);
    await Assert.That(StreamLifecycleAction.Purge.ToDisposition()).IsEqualTo(Whizbang.Core.Lifecycle.Disposition.Delete);
  }

  [Test]
  [Arguments("IdleTtl", "00:00:00")]
  [Arguments("IdleTtl", "-1.00:00:00")]
  [Arguments("IdleTtl", "a while")]
  [Arguments("MaxAge", "00:00:00")]
  [Arguments("MaxAge", "old")]
  [Arguments("KeepTailVersions", "0")]
  [Arguments("KeepTailVersions", "-5")]
  [Arguments("KeepTailVersions", "lots")]
  [Arguments("TenantByteCap", "0")]
  [Arguments("TenantByteCap", "1GB")]
  [Arguments("Action", "Delete")]
  [Arguments("Action", "2")]
  [Arguments("Action", "")]
  [Arguments("Action", "-1")]
  [Arguments("Action", " 5")]
  public async Task APolicyValueThatCannotBeUsed_IsRefusedNamingTheKeyAndTheValueAsync(string setting, string value) {
    var key = $"Whizbang:StreamLifecycle:Types:Shop.Order:{setting}";

    var thrown = await Assert.That(() => ArchiveOptionsBinder.Bind(_config((key, value)), new StreamLifecycleOptions()))
      .Throws<InvalidOperationException>();

    await Assert.That(thrown!.Message).Contains(key);
    await Assert.That(thrown.Message).Contains($"'{value}'");
  }

  [Test]
  public async Task Action_CloseBooks_IsAnAction_AndItsSealedPredecessorIsArchivedAsync() {
    var options = new StreamLifecycleOptions();

    ArchiveOptionsBinder.Bind(_config(("Whizbang:StreamLifecycle:Types:Shop.Order:Action", "closebooks")), options);

    await Assert.That(options.Types["Shop.Order"].Action).IsEqualTo(StreamLifecycleAction.CloseBooks)
      .Because("#1741 ships closing the books, so the action is no longer refused");
    await Assert.That(StreamLifecycleAction.CloseBooks.ToDisposition()).IsEqualTo(Whizbang.Core.Lifecycle.Disposition.Archive)
      .Because("closing the books seals the predecessor, which is then disposed of like any sealed stream: archived, so it stays readable");
  }

  [Test]
  public async Task Registration_CloseBooksWithNoBookClosingHandler_FailsTheStart_NamingTheTypes_AndWithOneStartsAsync() {
    static ServiceProvider build(bool withHandler) {
      var services = new ServiceCollection();
      services.AddSingleton(_config(
        ("Whizbang:StreamLifecycle:Types:Shop.Ledger:Action", "CloseBooks"),
        ("Whizbang:StreamLifecycle:Types:Shop.Order:Action", "Purge")));
      services.AddWhizbangWorkers();
      if (withHandler) {
        services.AddSingleton<Whizbang.Core.Lifecycle.IBookClosingHandler>(new NoBooks());
      }
      return services.BuildServiceProvider();
    }
    await using var without = build(withHandler: false);
    await using var with = build(withHandler: true);
    await using var none = new ServiceCollection().AddWhizbangWorkers().BuildServiceProvider();
    Exception? failure = null;

    try {
      without.GetRequiredService<IStartupValidator>().Validate();
    } catch (Exception ex) when (ex is OptionsValidationException or AggregateException) {
      failure = ex;
    }
    with.GetRequiredService<IStartupValidator>().Validate();
    none.GetRequiredService<IStartupValidator>().Validate();

    await Assert.That(failure).IsNotNull()
      .Because("only the domain can write a closing summary and an opening balance, so CloseBooks with no handler could never run");
    await Assert.That(failure!.Message).Contains("Shop.Ledger");
    await Assert.That(failure.Message).Contains(nameof(Whizbang.Core.Lifecycle.IBookClosingHandler));
    await Assert.That(failure.Message).DoesNotContain("Shop.Order");
  }

  private sealed class NoBooks : Whizbang.Core.Lifecycle.IBookClosingHandler {
    public ValueTask<Whizbang.Core.Lifecycle.BooksToClose?> PrepareAsync(Whizbang.Core.Lifecycle.BookClosingContext context, CancellationToken cancellationToken = default) =>
      ValueTask.FromResult<Whizbang.Core.Lifecycle.BooksToClose?>(null);
  }

  [Test]
  public async Task ThePolicyBoundaries_TheSmallestUsableValues_AreAcceptedAsync() {
    var options = new StreamLifecycleOptions();

    ArchiveOptionsBinder.Bind(_config(
      ("Whizbang:StreamLifecycle:Types:Shop.Order:IdleTtl", "00:00:01"),
      ("Whizbang:StreamLifecycle:Types:Shop.Order:MaxAge", "00:00:01"),
      ("Whizbang:StreamLifecycle:Types:Shop.Order:KeepTailVersions", "1"),
      ("Whizbang:StreamLifecycle:Types:Shop.Order:TenantByteCap", "1")), options);

    await Assert.That(options.Types["Shop.Order"].IdleTtl).IsEqualTo(TimeSpan.FromSeconds(1));
    await Assert.That(options.Types["Shop.Order"].MaxAge).IsEqualTo(TimeSpan.FromSeconds(1));
    await Assert.That(options.Types["Shop.Order"].KeepTailVersions).IsEqualTo(1L);
    await Assert.That(options.Types["Shop.Order"].TenantByteCap).IsEqualTo(1L);
  }
}
