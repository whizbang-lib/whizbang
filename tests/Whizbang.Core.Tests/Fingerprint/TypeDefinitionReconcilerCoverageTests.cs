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
    var catalog = new FakeCatalog(
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
    var catalog = new FakeCatalog(
      new MessageTypeCatalogEntry(typeof(object), "DriftEvent", "event", null) {
        SettingsHash = "settings-same",
        SchemaHash = "schema-new"
      });
    var logger = new CapturingLogger();
    var reconciler = _reconciler(new DriftCoordinator(), catalog, logger);

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
    var poisonType = typeof(GenericPerspectiveModel<>).GetGenericArguments()[0];
    Whizbang.Core.Perspectives.PerspectiveTtlRegistry.Register(poisonType, 30);

    var coordinator = new RetentionCapturingCoordinator();
    var reconciler = _reconciler(coordinator, new FakeCatalog());

    await reconciler.ReconcileAsync(CancellationToken.None);

    await Assert.That(coordinator.CapturedDeclarations).IsNotNull()
      .Because("our own registration guarantees at least one declaring model, so the sync must run");
    await Assert.That(coordinator.CapturedDeclarations!.Any(d => d.ClrTypeName is null)).IsFalse()
      .Because("a type without a resolvable FullName must be skipped, not forwarded with a null key that "
        + "would corrupt wh_perspective_registry lookups for every legitimately-named perspective sharing it");
  }

  // Settings changed while the payload shape did not: that is metadata drift, and the lineage edge
  // must say so. Recording it as a schema upgrade sends an operator looking for an upcaster nobody
  // needs; recording it as a reclassification claims an ephemeral change that never happened.
  [Test]
  public async Task ReconcileAsync_OnlySettingsHashChanged_RecordsMetadataDriftLineageAsync() {
    var catalog = new FakeCatalog(
      new MessageTypeCatalogEntry(typeof(object), "DriftEvent", "event", null) {
        SettingsHash = "settings-new",
        SchemaHash = "schema-same"
      });
    var coordinator = new LineageCapturingDriftCoordinator(storedSettingsHash: "settings-old", storedSchemaHash: "schema-same");
    var reconciler = _reconciler(coordinator, catalog);

    var summary = await reconciler.ReconcileAsync(CancellationToken.None);

    await Assert.That(summary.DriftDetected).IsEqualTo(1)
      .Because("a changed settings hash against the stored definition is drift");
    await Assert.That(coordinator.Recorded).IsNotNull()
      .Because("drift between two definitions is always recorded as a lineage edge");
    await Assert.That(coordinator.Recorded!.Value.Relationship).IsEqualTo(DefinitionRelationship.MetadataChangedTo)
      .Because("the schema hash is unchanged and the entry is not ephemeral, so the only remaining reading is a metadata change");
    await Assert.That(coordinator.Recorded.Value.From).IsEqualTo(1);
    await Assert.That(coordinator.Recorded.Value.To).IsEqualTo(2);
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
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Sonar", "S2326:Unused type parameters should be removed", Justification = "An open generic type is the shape under test; the parameter carries no data by design.")]
  private sealed class GenericPerspectiveModel<T>;

  private sealed class FakeCatalog(params MessageTypeCatalogEntry[] entries) : IMessageTypeCatalog {
    public IReadOnlyList<MessageTypeCatalogEntry> GetAll() => entries;
  }

  /// <summary>Reports one prior definition (id 1) and always registers as a NEW definition (id 2)
  /// superseding it, so every catalog entry it processes takes the drift-detected path.</summary>
  private sealed class DriftCoordinator : Whizbang.Core.Tests.Workers.NoOpWorkCoordinator, IWorkCoordinator {
    public Task<IReadOnlyList<TypeDefinitionInfo>> GetTypeDefinitionsAsync(CancellationToken cancellationToken = default) =>
      Task.FromResult<IReadOnlyList<TypeDefinitionInfo>>([
        new TypeDefinitionInfo(1, "DriftEvent", "settings-same", "schema-old", 0)
      ]);

    public Task<TypeDefinitionRegistration> RegisterTypeDefinitionAsync(
        string eventTypeName, string settingsHashHex, string schemaHashHex, int schemaVersion,
        CancellationToken cancellationToken = default) =>
      Task.FromResult(new TypeDefinitionRegistration(DefinitionId: 2, IsNew: true, PreviousDefinitionId: 1));
  }

  /// <summary>Stores one prior definition (id 1) with the given hashes, registers every entry as a NEW
  /// definition (id 2) superseding it, and captures the lineage edge the reconciler records.</summary>
  private sealed class LineageCapturingDriftCoordinator(string storedSettingsHash, string storedSchemaHash)
      : Whizbang.Core.Tests.Workers.NoOpWorkCoordinator, IWorkCoordinator {
    public (int From, int To, DefinitionRelationship Relationship)? Recorded { get; private set; }

    public Task<IReadOnlyList<TypeDefinitionInfo>> GetTypeDefinitionsAsync(CancellationToken cancellationToken = default) =>
      Task.FromResult<IReadOnlyList<TypeDefinitionInfo>>([
        new TypeDefinitionInfo(1, "DriftEvent", storedSettingsHash, storedSchemaHash, 0)
      ]);

    public Task<TypeDefinitionRegistration> RegisterTypeDefinitionAsync(
        string eventTypeName, string settingsHashHex, string schemaHashHex, int schemaVersion,
        CancellationToken cancellationToken = default) =>
      Task.FromResult(new TypeDefinitionRegistration(DefinitionId: 2, IsNew: true, PreviousDefinitionId: 1));

    public Task RecordDefinitionLineageAsync(
        int fromDefinitionId, int toDefinitionId, DefinitionRelationship relationship, string? migrationRef,
        CancellationToken cancellationToken = default) {
      Recorded = (fromDefinitionId, toDefinitionId, relationship);
      return Task.CompletedTask;
    }
  }

  private sealed class RetentionCapturingCoordinator : Whizbang.Core.Tests.Workers.NoOpWorkCoordinator, IWorkCoordinator {
    public IReadOnlyList<PerspectiveRetentionDeclaration>? CapturedDeclarations { get; private set; }

    public Task SyncPerspectiveRetentionAsync(
        IReadOnlyList<PerspectiveRetentionDeclaration> declarations, CancellationToken cancellationToken = default) {
      CapturedDeclarations = declarations;
      return Task.CompletedTask;
    }
  }

  private sealed class CapturingLogger : ILogger<TypeDefinitionReconciler> {
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
