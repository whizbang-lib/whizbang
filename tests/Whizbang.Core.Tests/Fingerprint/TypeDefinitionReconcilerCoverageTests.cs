using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Configuration;
using Whizbang.Core.Fingerprint;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Fingerprint;

/// <summary>
/// Coverage-round-23 targets for <see cref="TypeDefinitionReconciler.ReconcileAsync"/>: three guards
/// this startup walk relies on to avoid corrupting the fingerprint/retention tables it writes to. A
/// wrong answer here either blocks a valid deploy behind bogus drift, lets an incompatible one through
/// unnoticed, or writes a garbage key that corrupts lookups for every OTHER type/perspective sharing
/// it. (The full walk against a real database is covered in
/// <c>Whizbang.Data.EFCore.Postgres.Tests</c>; this exercises the pure decision logic against the
/// documented <see cref="IWorkCoordinator"/> no-op defaults.)
/// </summary>
public class TypeDefinitionReconcilerCoverageTests {
  // Only entries the generator actually fingerprinted (both hashes present) may register — an
  // unfingerprinted entry inserting a definition row keyed on an empty hash would corrupt drift
  // detection for every type still predating fingerprinting.
  [Test]
  public async Task ReconcileAsync_EntryWithoutFingerprintHashes_IsSkippedButOthersStillRegisterAsync() {
    var catalog = new _fakeCatalog(
      new MessageTypeCatalogEntry(typeof(object), "NoHashEvent", "event", null),
      new MessageTypeCatalogEntry(typeof(object), "HashedEvent", "event", null) {
        SettingsHash = "s1",
        SchemaHash = "h1"
      });
    var reconciler = _reconciler(new Whizbang.Core.Tests.Workers.NoOpWorkCoordinator(), catalog);

    var summary = await reconciler.ReconcileAsync(CancellationToken.None);

    await Assert.That(summary.TypesRegistered).IsEqualTo(1)
      .Because("the unfingerprinted entry must be skipped entirely (never registered) while the "
        + "fingerprinted sibling still is — a count of 2 here would mean a bogus definition got "
        + "inserted for a type the generator never hashed");
  }

  // A changed schema hash is upcasting-relevant and must surface as its own operator warning,
  // distinct from the general drift-lineage log — losing it here means a payload shape change (which
  // needs event-versioning attention) reads identically to a purely behavioral/metadata change.
  [Test]
  public async Task ReconcileAsync_SchemaHashChanged_LogsSchemaDriftForThatTypeAsync() {
    var catalog = new _fakeCatalog(
      new MessageTypeCatalogEntry(typeof(object), "DriftEvent", "event", null) {
        SettingsHash = "settings-same",
        SchemaHash = "schema-new"
      });
    var logger = new _capturingLogger();
    var reconciler = _reconciler(new _driftCoordinator(), catalog, logger);

    var summary = await reconciler.ReconcileAsync(CancellationToken.None);

    await Assert.That(summary.DriftDetected).IsEqualTo(1)
      .Because("a changed schema hash against the previously-stored definition IS drift, by definition");
    await Assert.That(logger.Entries.Any(e => e.EventId == 9213 && e.Message.Contains("DriftEvent"))).IsTrue()
      .Because("schema drift needs its own surfaced warning (event-versioning/upcasting territory) — "
        + "silently folding it into the generic drift-lineage log would bury a payload-shape change "
        + "among purely behavioral drift that needs no code changes at all");
  }

  // A perspective model type without a usable name (e.g. an unresolved/open generic slipping into the
  // registry) must never reach the retention-sync payload with a null key: every OTHER legitimate
  // perspective's retention lookup keys off ClrTypeName, and a null entry corrupts that lookup for all
  // of them, not just the malformed one.
  [Test]
  public async Task ReconcileAsync_PerspectiveModelTypeWithNullFullName_IsExcludedFromRetentionSyncAsync() {
    var poisonType = typeof(_genericPerspectiveModel<>).GetGenericArguments()[0];
    Whizbang.Core.Perspectives.PerspectiveTtlRegistry.Register(poisonType, 30);

    var coordinator = new _retentionCapturingCoordinator();
    var reconciler = _reconciler(coordinator, new _fakeCatalog());

    await reconciler.ReconcileAsync(CancellationToken.None);

    await Assert.That(coordinator.CapturedDeclarations).IsNotNull()
      .Because("our own registration guarantees at least one declaring model, so the sync must run");
    await Assert.That(coordinator.CapturedDeclarations!.Any(d => d.ClrTypeName is null)).IsFalse()
      .Because("a type without a resolvable FullName must be skipped, not forwarded with a null key that "
        + "would corrupt wh_perspective_registry lookups for every legitimately-named perspective sharing it");
  }

  private static TypeDefinitionReconciler _reconciler(
      IWorkCoordinator coordinator, IMessageTypeCatalog catalog, ILogger<TypeDefinitionReconciler>? logger = null) {
    var services = new ServiceCollection();
    services.AddSingleton(coordinator);
    var provider = services.BuildServiceProvider();
    return new TypeDefinitionReconciler(
      provider.GetRequiredService<IServiceScopeFactory>(),
      Options.Create(new EphemeralOptions()),
      logger ?? NullLogger<TypeDefinitionReconciler>.Instance,
      catalog);
  }

  /// <summary>Never actually constructed — only its unbound generic parameter's `Type` (whose
  /// <c>FullName</c> is null) is registered, standing in for a malformed registry entry.</summary>
  private sealed class _genericPerspectiveModel<T>;

  private sealed class _fakeCatalog(params MessageTypeCatalogEntry[] entries) : IMessageTypeCatalog {
    public IReadOnlyList<MessageTypeCatalogEntry> GetAll() => entries;
  }

  /// <summary>Reports one prior definition (id 1) and always registers as a NEW definition (id 2)
  /// superseding it, so every catalog entry it processes takes the drift-detected path.</summary>
  private sealed class _driftCoordinator : Whizbang.Core.Tests.Workers.NoOpWorkCoordinator, IWorkCoordinator {
    public Task<IReadOnlyList<TypeDefinitionInfo>> GetTypeDefinitionsAsync(CancellationToken cancellationToken = default) =>
      Task.FromResult<IReadOnlyList<TypeDefinitionInfo>>([
        new TypeDefinitionInfo(1, "DriftEvent", "settings-same", "schema-old", 0)
      ]);

    public Task<TypeDefinitionRegistration> RegisterTypeDefinitionAsync(
        string eventTypeName, string settingsHashHex, string schemaHashHex, int schemaVersion,
        CancellationToken cancellationToken = default) =>
      Task.FromResult(new TypeDefinitionRegistration(DefinitionId: 2, IsNew: true, PreviousDefinitionId: 1));
  }

  private sealed class _retentionCapturingCoordinator : Whizbang.Core.Tests.Workers.NoOpWorkCoordinator, IWorkCoordinator {
    public IReadOnlyList<PerspectiveRetentionDeclaration>? CapturedDeclarations { get; private set; }

    public Task SyncPerspectiveRetentionAsync(
        IReadOnlyList<PerspectiveRetentionDeclaration> declarations, CancellationToken cancellationToken = default) {
      CapturedDeclarations = declarations;
      return Task.CompletedTask;
    }
  }

  private sealed class _capturingLogger : ILogger<TypeDefinitionReconciler> {
    public List<(int EventId, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        Microsoft.Extensions.Logging.EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
      => Entries.Add((eventId.Id, formatter(state, exception)));
  }
}
