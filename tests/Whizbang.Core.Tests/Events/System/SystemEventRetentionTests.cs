// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Configuration;
using Whizbang.Core.Events.System;
using Whizbang.Core.Fingerprint;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Events.System;

/// <summary>
/// #2320: the framework's own rewind and rebuild events are operational bookkeeping, each in a stream of its own, and
/// were stored forever (millions of rows, a large share of a database's event bytes). They are ephemeral now, kept for
/// 30 days by default, configurable under <c>Whizbang:Ephemeral:SystemEvents:Ttl</c>. The retention reaches the TTL a new
/// event is stamped with and the per-type grace the reaper measures from each stored event's creation time, so a
/// reclassified historical event ages out on the same rule.
/// </summary>
/// <docs>fundamentals/events/ephemeral-events#framework-system-events</docs>
/// <tests>src/Whizbang.Core/Events/System/SystemEventRetention.cs</tests>
[Category("Core")]
[Category("Ephemeral")]
public class SystemEventRetentionTests {
  private const int SEVEN_DAYS = 7 * 24 * 60 * 60;

  private static readonly Type[] _systemEvents = [
    typeof(PerspectiveRebuildStarted),
    typeof(PerspectiveRebuildProgress),
    typeof(PerspectiveRebuildCompleted),
    typeof(PerspectiveRebuildFailed),
    typeof(PerspectiveRewindStarted),
    typeof(PerspectiveRewindCompleted),
  ];

  private static MessageTypeCatalogEntry _entryOf(Type type) =>
    new Whizbang.Core.Generated.GeneratedMessageTypeCatalog().GetAll().Single(e => e.Type == type);

  [Test]
  public async Task EveryRewindAndRebuildEvent_IsEphemeral_ForThirtyDaysByDefaultAsync() {
    foreach (var type in _systemEvents) {
      var entry = _entryOf(type);

      await Assert.That(entry.Ephemeral).IsNotNull().Because($"{type.Name} is bookkeeping in a stream of its own");
      await Assert.That(entry.Ephemeral!.Destruction).IsEqualTo(Destruction.AfterTtl);
      await Assert.That(entry.Ephemeral.TtlSeconds).IsEqualTo(SystemEventRetention.DEFAULT_TTL_SECONDS);
      await Assert.That(entry.Ephemeral.RewindGraceSeconds).IsEqualTo(SystemEventRetention.DEFAULT_TTL_SECONDS)
        .Because("the reaper measures the grace from the stored creation time, so a reclassified historical event, which "
          + "carries no stamped expiry, is still kept for the TTL");
      await Assert.That(typeof(IEphemeralSystemEvent).IsAssignableFrom(type)).IsTrue();
    }
  }

  [Test]
  public async Task StreamRewindEvents_StaySourced_BecauseTheyNameTheApplicationStreamAsync() {
    // Their StreamId is the stream being rewound: an ephemeral event there would mix modes in an application stream.
    await Assert.That(_entryOf(typeof(StreamRewindStarted)).Ephemeral).IsNull();
    await Assert.That(_entryOf(typeof(StreamRewindCompleted)).Ephemeral).IsNull();
  }

  [Test]
  public async Task Apply_ToASystemEvent_SetsTtlAndGrace_AndLeavesOtherTypesAloneAsync() {
    var declared = new EphemeralInfo(Destruction.AfterTtl, TransientStorage.PersistedRow, RewindGraceSeconds: 5, TtlSeconds: 9);

    var system = SystemEventRetention.Apply(typeof(PerspectiveRewindStarted), declared, TimeSpan.FromDays(7));
    var other = SystemEventRetention.Apply(typeof(StreamRewindStarted), declared, TimeSpan.FromDays(7));

    await Assert.That(system.TtlSeconds).IsEqualTo(SEVEN_DAYS);
    await Assert.That(system.RewindGraceSeconds).IsEqualTo(SEVEN_DAYS);
    await Assert.That(system.Destruction).IsEqualTo(Destruction.AfterTtl);
    await Assert.That(other).IsSameReferenceAs(declared);
  }

  [Test]
  public async Task Resolver_AppliesTheConfiguredTtl_ToSystemEventsOnlyAsync() {
    var userType = new MessageTypeCatalogEntry(typeof(StreamRewindStarted), "Ns.UserPresence", "event", null) {
      Ephemeral = new EphemeralInfo(Destruction.AfterTtl, TransientStorage.PersistedRow, TtlSeconds: 30),
    };
    var catalog = new FakeCatalog(_entryOf(typeof(PerspectiveRewindStarted)), userType);
    var options = Options.Create(new EphemeralOptions { SystemEvents = { Ttl = TimeSpan.FromDays(7) } });

    var resolver = new EphemeralModeResolver(catalog, options);

    await Assert.That(resolver.Resolve(typeof(PerspectiveRewindStarted))!.TtlSeconds).IsEqualTo(SEVEN_DAYS);
    await Assert.That(resolver.Resolve(_entryOf(typeof(PerspectiveRewindStarted)).ClrTypeName)!.TtlSeconds).IsEqualTo(SEVEN_DAYS);
    await Assert.That(resolver.Resolve("Ns.UserPresence")!.TtlSeconds).IsEqualTo(30)
      .Because("a type that is not a framework system event keeps its own declared TTL");
    await Assert.That(EphemeralTtlDeriver.Derive(
        new PerspectiveRewindStarted(Guid.Empty, Guid.Empty, "p", Guid.Empty, null, false, DateTimeOffset.UnixEpoch), resolver))
      .IsEqualTo(SEVEN_DAYS).Because("the TTL stamped on a new event at dispatch is the configured one");
  }

  [Test]
  public async Task SystemEventsTtl_DefaultsToThirtyDaysAsync() {
    await Assert.That(new EphemeralOptions().SystemEvents.Ttl).IsEqualTo(TimeSpan.FromDays(30));
  }

  [Test]
  public async Task SystemEventsTtl_BindsFromConfigurationAsync() {
    var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
      ["Whizbang:Ephemeral:SystemEvents:Ttl"] = "7.00:00:00",
    }).Build();
    var services = new ServiceCollection();
    // The catalog a host's generated code registers; registered first so the defaults keep it.
    services.AddSingleton<IMessageTypeCatalog>(new Whizbang.Core.Generated.GeneratedMessageTypeCatalog());
    services.TryAddWhizbangDefaults();
    services.AddSingleton<IConfiguration>(configuration);
    services.AddWhizbangWorkers();
    await using var provider = services.BuildServiceProvider();

    var bound = provider.GetRequiredService<IOptions<EphemeralOptions>>().Value.SystemEvents;
    var info = provider.GetRequiredService<IEphemeralModeResolver>().Resolve(typeof(PerspectiveRebuildStarted));

    await Assert.That(bound.Ttl).IsEqualTo(TimeSpan.FromDays(7));
    await Assert.That(info).IsNotNull();
    await Assert.That(info!.TtlSeconds).IsEqualTo(SEVEN_DAYS)
      .Because("the registered resolver is built with the options, so dispatch stamps the configured TTL");
  }

  [Test]
  [Arguments(0)]
  [Arguments(-1)]
  public async Task Validator_RejectsANonPositiveTtl_NamingTheKeyAsync(int seconds) {
    var result = new EphemeralOptionsValidator().Validate(null,
      new EphemeralOptions { SystemEvents = { Ttl = TimeSpan.FromSeconds(seconds) } });

    await Assert.That(result.Failed).IsTrue();
    await Assert.That(result.FailureMessage).Contains("Whizbang:Ephemeral:SystemEvents:Ttl");
  }

  [Test]
  public async Task Validator_RejectsATtlBeyondWholeSecondsInAnInt_AndAcceptsOneSecondAsync() {
    var tooLong = new EphemeralOptionsValidator().Validate(null,
      new EphemeralOptions { SystemEvents = { Ttl = TimeSpan.FromSeconds((double)int.MaxValue + 1) } });
    var oneSecond = new EphemeralOptionsValidator().Validate(null,
      new EphemeralOptions { SystemEvents = { Ttl = TimeSpan.FromSeconds(1) } });

    await Assert.That(tooLong.Failed).IsTrue();
    await Assert.That(tooLong.FailureMessage).Contains("Whizbang:Ephemeral:SystemEvents:Ttl");
    await Assert.That(oneSecond.Succeeded).IsTrue();
  }

  [Test]
  public async Task Reconciler_SyncsTheConfiguredTtl_AsTheGraceOfEverySystemEventAsync() {
    var catalog = new FakeCatalog([.. _systemEvents.Select(_entryOf)]);
    var coordinator = new GraceCapturingCoordinator();
    var services = new ServiceCollection().AddSingleton<IWorkCoordinator>(coordinator).BuildServiceProvider();
    var reconciler = new TypeDefinitionReconciler(
      services.GetRequiredService<IServiceScopeFactory>(),
      Options.Create(new EphemeralOptions { SystemEvents = { Ttl = TimeSpan.FromDays(7) } }),
      NullLogger<TypeDefinitionReconciler>.Instance,
      catalog);

    await reconciler.ReconcileAsync(CancellationToken.None);

    foreach (var type in _systemEvents) {
      var stored = TypeNameFormatter.Format(type);
      await Assert.That(coordinator.Graces.Single(g => g.EventTypeName == stored).GraceSeconds).IsEqualTo(SEVEN_DAYS)
        .Because($"{type.Name}'s reaper grace is the configured retention, keyed by the stored type name");
    }
  }

  private sealed class FakeCatalog(params MessageTypeCatalogEntry[] entries) : IMessageTypeCatalog {
    public IReadOnlyList<MessageTypeCatalogEntry> GetAll() => entries;
  }

  private sealed class GraceCapturingCoordinator : Whizbang.Core.Tests.Workers.NoOpWorkCoordinator, IWorkCoordinator {
    public List<EphemeralTypeGrace> Graces { get; } = [];

    public Task SyncEphemeralTypeGraceAsync(IReadOnlyList<EphemeralTypeGrace> graceOverrides, CancellationToken cancellationToken = default) {
      Graces.AddRange(graceOverrides);
      return Task.CompletedTask;
    }
  }
}
