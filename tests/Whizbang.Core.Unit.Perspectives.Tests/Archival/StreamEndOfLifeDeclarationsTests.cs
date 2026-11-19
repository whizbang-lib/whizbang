// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Archival;
using Whizbang.Core.Attributes;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Archival;

/// <summary>
/// <see cref="StreamEndOfLifeAttribute"/> (#1740): the end-of-life policy declared on the event type that opens a stream,
/// registered by the catalog generator, applied before configuration so a configured key wins, and refused at start when a
/// declared value cannot be used.
/// </summary>
/// <docs>fundamentals/events/stream-end-of-life#declaring-a-policy-on-the-event-type</docs>
/// <code-under-test>src/Whizbang.Core/Archival/StreamEndOfLifeDeclarations.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Attributes/StreamEndOfLifeAttribute.cs</code-under-test>
public class StreamEndOfLifeDeclarationsTests {
  [StreamEndOfLife(IdleTtl = "30.00:00:00", KeepTailVersions = 50, Action = StreamLifecycleAction.Purge)]
  public record EolProbeOpened([property: StreamId] Guid StreamId = default) : IEvent;

  public sealed record EolProbeReopened : EolProbeOpened;

  public sealed record EolPlainOpened([property: StreamId] Guid StreamId = default) : IEvent;

  private static string _type<T>() => TypeNameFormatter.FormatClrTypeName(typeof(T));

  private static IConfiguration _config(params (string Key, string Value)[] values) =>
    new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

  [Test]
  public async Task TheAttribute_DefaultsToNoTriggerAndArchive_AndKeepsEachValueSetAsync() {
    var unset = new StreamEndOfLifeAttribute();
    var set = new StreamEndOfLifeAttribute {
      IdleTtl = "1.00:00:00",
      MaxAge = "2.00:00:00",
      KeepTailVersions = 3,
      TenantByteCap = 4,
      Action = StreamLifecycleAction.Compact,
    };

    await Assert.That(unset.IdleTtl).IsNull();
    await Assert.That(unset.MaxAge).IsNull();
    await Assert.That(unset.KeepTailVersions).IsEqualTo(0L);
    await Assert.That(unset.TenantByteCap).IsEqualTo(0L);
    await Assert.That(unset.Action).IsEqualTo(StreamLifecycleAction.Archive)
      .Because("the default action archives, the same default configuration has");
    await Assert.That(set.IdleTtl).IsEqualTo("1.00:00:00");
    await Assert.That(set.MaxAge).IsEqualTo("2.00:00:00");
    await Assert.That(set.KeepTailVersions).IsEqualTo(3L);
    await Assert.That(set.TenantByteCap).IsEqualTo(4L);
    await Assert.That(set.Action).IsEqualTo(StreamLifecycleAction.Compact);
  }

  [Test]
  public async Task AnAttributedEventTypeInThisAssembly_IsRegisteredByTheGenerator_Inherited_KeyedByItsLedgerStreamTypeAsync() {
    var all = StreamEndOfLifeDeclarations.All();

    await Assert.That(all).Contains(new StreamEndOfLifeDeclaration(_type<EolProbeOpened>(), "30.00:00:00", null, 50, 0, StreamLifecycleAction.Purge))
      .Because("the catalog generator's module initializer registers it before any test runs, keyed as the ledger records the stream type");
    await Assert.That(all).Contains(new StreamEndOfLifeDeclaration(_type<EolProbeReopened>(), "30.00:00:00", null, 50, 0, StreamLifecycleAction.Purge))
      .Because("the attribute is inherited, so the streams a derived event opens have the same policy");
    await Assert.That(all.Select(d => d.StreamType)).DoesNotContain(_type<EolPlainOpened>());
    await Assert.That(all.Select(d => d.StreamType)).IsEquivalentTo(all.Select(d => d.StreamType).Order(StringComparer.Ordinal),
      TUnit.Assertions.Enums.CollectionOrdering.Matching);
  }

  [Test]
  public async Task Register_KeepsTheFirstDeclarationOfAStreamType_AndRefusesNullAsync() {
    var key = "Whizbang.Tests.EolRegisteredTwice";

    StreamEndOfLifeDeclarations.Register(new StreamEndOfLifeDeclaration(key, "1.00:00:00", null, 0, 0, StreamLifecycleAction.Archive));
    StreamEndOfLifeDeclarations.Register(new StreamEndOfLifeDeclaration(key, "9.00:00:00", null, 0, 0, StreamLifecycleAction.Purge));

    await Assert.That(StreamEndOfLifeDeclarations.All().Single(d => d.StreamType == key).IdleTtl).IsEqualTo("1.00:00:00");
    await Assert.That(() => StreamEndOfLifeDeclarations.Register(null!)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Apply_SetsEachDeclaredTrigger_AndConfiguration_ThenWinsKeyByKeyAsync() {
    var options = new StreamLifecycleOptions();
    var declarations = new[] {
      new StreamEndOfLifeDeclaration("Shop.OrderPlaced", "30.00:00:00", "365.00:00:00", 0, 0, StreamLifecycleAction.Purge),
      new StreamEndOfLifeDeclaration("Shop.LedgerOpened", null, null, 100, 2048, StreamLifecycleAction.Archive),
    };

    ArchiveOptionsBinder.Apply(declarations, options);
    ArchiveOptionsBinder.Bind(_config(
      ("Whizbang:StreamLifecycle:Types:Shop.OrderPlaced:IdleTtl", "7.00:00:00"),
      ("Whizbang:StreamLifecycle:Types:Shop.OrderPlaced:Action", "Archive")), options);

    var order = options.Types["Shop.OrderPlaced"];
    var ledger = options.Types["Shop.LedgerOpened"];
    await Assert.That(order.IdleTtl).IsEqualTo(TimeSpan.FromDays(7)).Because("a key set in configuration wins over the attribute");
    await Assert.That(order.Action).IsEqualTo(StreamLifecycleAction.Archive);
    await Assert.That(order.MaxAge).IsEqualTo(TimeSpan.FromDays(365)).Because("a key configuration does not set keeps the attribute's value");
    await Assert.That(ledger.IdleTtl).IsNull();
    await Assert.That(ledger.MaxAge).IsNull();
    await Assert.That(ledger.KeepTailVersions).IsEqualTo(100L);
    await Assert.That(ledger.TenantByteCap).IsEqualTo(2048L);
    await Assert.That(order.KeepTailVersions).IsNull().Because("0 on the attribute is no version cap");
    await Assert.That(order.TenantByteCap).IsNull();
  }

  [Test]
  [Arguments("soon", null, 0L, 0L, "IdleTtl")]
  [Arguments("00:00:00", null, 0L, 0L, "IdleTtl")]
  [Arguments(null, "-1.00:00:00", 0L, 0L, "MaxAge")]
  [Arguments(null, null, -1L, 0L, "KeepTailVersions")]
  [Arguments(null, null, 0L, -1L, "TenantByteCap")]
  public async Task Apply_ADeclaredValueThatCannotBeUsed_IsRefusedNamingTheTypeAndTheSettingAsync(
      string? idle, string? age, long keep, long cap, string setting) {
    var declaration = new StreamEndOfLifeDeclaration("Shop.OrderPlaced", idle, age, keep, cap, StreamLifecycleAction.Archive);

    var thrown = await Assert.That(() => ArchiveOptionsBinder.Apply([declaration], new StreamLifecycleOptions()))
      .Throws<InvalidOperationException>();

    await Assert.That(thrown!.Message).Contains("[StreamEndOfLife] on Shop.OrderPlaced");
    await Assert.That(thrown.Message).Contains(setting);
  }

  [Test]
  public async Task Registration_StartsFromTheGeneratedDeclarations_EvenWithNoConfigurationAsync() {
    var services = new ServiceCollection();
    services.AddWhizbangWorkers();
    await using var provider = services.BuildServiceProvider();

    var options = provider.GetRequiredService<IOptions<StreamLifecycleOptions>>().Value;

    await Assert.That(options.Types[_type<EolProbeOpened>()].IdleTtl).IsEqualTo(TimeSpan.FromDays(30));
    await Assert.That(options.Types[_type<EolProbeOpened>()].Action).IsEqualTo(StreamLifecycleAction.Purge);
  }
}
