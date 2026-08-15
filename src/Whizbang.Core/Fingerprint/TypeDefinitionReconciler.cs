using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Configuration;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Fingerprint;

/// <summary>
/// Startup reconciler for the type-definition fingerprint. Registers each message type's current
/// definition (content hashes), detects drift against the stored fingerprint, records a lineage edge, and
/// — for settings drift toward Ephemeral, when enabled — reclassifies the type's historical events.
/// Detect-by-default, act-by-opt-in (<see cref="EphemeralOptions.ReconcileHistoricalOnStartup"/>).
/// </summary>
/// <docs>fundamentals/events/type-definition-fingerprint</docs>
public sealed partial class TypeDefinitionReconciler {
  private readonly IServiceScopeFactory _scopeFactory;
  private readonly EphemeralOptions _options;
  private readonly ILogger<TypeDefinitionReconciler> _logger;

  /// <summary>
  /// How recently a sibling's reconcile suppresses this one. Deliberately short: this is startup
  /// work, and a genuine redeploy minutes later SHOULD reconcile again (its hashes may differ).
  /// The window only needs to cover one fleet's simultaneous boot, not a whole deployment cycle.
  /// </summary>
  private static readonly TimeSpan _claimWindow = TimeSpan.FromMinutes(2);
  private readonly IMessageTypeCatalog? _catalog;

  /// <summary>Creates the reconciler. <paramref name="catalog"/> is optional — absent means no-op.</summary>
  /// <remarks>
  /// <see cref="IWorkCoordinator"/> is a SCOPED service (one per DbContext scope), so this singleton
  /// reconciler resolves it from a freshly-created scope per pass rather than capturing it in the
  /// constructor — capturing a scoped service on a singleton is a captive-dependency bug that throws
  /// under scope validation ("Cannot resolve scoped service … from root provider") and yields a broken
  /// root-scoped instance without it. Mirrors <c>MaintenanceWorker</c>.
  /// </remarks>
  public TypeDefinitionReconciler(
      IServiceScopeFactory scopeFactory,
      IOptions<EphemeralOptions> options,
      ILogger<TypeDefinitionReconciler> logger,
      IMessageTypeCatalog? catalog = null) {
    _scopeFactory = scopeFactory;
    _options = options.Value;
    _logger = logger;
    _catalog = catalog;
  }

  /// <summary>Runs one reconciliation pass over the catalog. Returns a summary of what it found/did.</summary>
  public async Task<TypeDefinitionReconcileSummary> ReconcileAsync(CancellationToken cancellationToken = default) {
    if (_catalog is null) {
      return TypeDefinitionReconcileSummary.Empty;
    }

    // IWorkCoordinator is scoped — resolve it from a fresh scope for this pass (see the ctor remarks).
    using var scope = _scopeFactory.CreateScope();
    var coordinator = scope.ServiceProvider.GetRequiredService<IWorkCoordinator>();

    // One instance per service per window does this, not every replica. The walk is idempotent, so
    // N instances running it was never incorrect — just N times the cost for one instance's worth
    // of result, and at deploy time every replica of every service does it simultaneously against
    // one shared database. The claim goes FIRST: skipping has to skip the catalog walk and its
    // per-type round-trips, not merely the writes at the end.
    if (!await coordinator.TryClaimTypeDefinitionReconcileAsync(_claimWindow, cancellationToken).ConfigureAwait(false)) {
      LogSkippedByClaim(_logger);
      return TypeDefinitionReconcileSummary.SkippedByClaim;
    }

    // Sync per-type rewind-grace overrides ([Ephemeral(RewindGraceSeconds >= 0)]) so the reaper resolves
    // COALESCE(type grace, global default) per event. Full replace — declaring set upserted, rest pruned.
    var graceOverrides = new List<EphemeralTypeGrace>();
    foreach (var e in _catalog.GetAll()) {
      if (e.Ephemeral is { RewindGraceSeconds: >= 0 } eph) {
        graceOverrides.Add(new EphemeralTypeGrace(e.ClrTypeName, eph.RewindGraceSeconds));
      }
    }
    await coordinator.SyncEphemeralTypeGraceAsync(graceOverrides, cancellationToken).ConfigureAwait(false);

    // Carry each perspective's row-retention declaration into the perspective registry, so the reaper
    // resolves enrolment and windows from SQL instead of having them threaded in every cycle.
    //
    // The source is what module initializers REGISTERED across the loaded assemblies — the only
    // AOT-legal discovery route, since assembly scanning needs the reflection that source-generated
    // self-registration exists to avoid.
    // Union both registries. Keying only off the TTL registry would make a perspective that declares
    // [RowCap] WITHOUT [RowTtl] invisible to the sync — its cap would register in memory and never reach
    // wh_perspective_registry, so the sweep would never see it. A cap is a complete retention policy on
    // its own (bound cardinality, let age alone), so it has to enrol on its own too.
    var ttlRegistered = Whizbang.Core.Perspectives.PerspectiveTtlRegistry.RegisteredModels();
    var capRegistered = Whizbang.Core.Perspectives.PerspectiveRowCapRegistry.RegisteredModels();
    var declaringModels = new Dictionary<Type, int>();
    foreach (var (modelType, ttlSeconds) in ttlRegistered) {
      declaringModels[modelType] = ttlSeconds;
    }
    foreach (var (modelType, _) in capRegistered) {
      // -1 = enrolled with no sliding rule, which is distinct from zero and from absent.
      if (!declaringModels.ContainsKey(modelType)) {
        declaringModels[modelType] = -1;
      }
    }

    var registered = declaringModels;
    if (registered.Count > 0) {
      var retention = new List<PerspectiveRetentionDeclaration>(registered.Count);
      foreach (var (modelType, ttlSeconds) in registered) {
        var clrTypeName = modelType.FullName;
        if (clrTypeName is null) {
          continue;
        }
        // A registered perspective is enrolled by construction — the declaration IS the enrolment.
        // A negative window means enrolled with no default rule, which is distinct from zero.
        var cap = Whizbang.Core.Perspectives.PerspectiveRowCapRegistry.Resolve(modelType);
        retention.Add(new PerspectiveRetentionDeclaration(
          clrTypeName,
          Enrolled: true,
          TtlSeconds: ttlSeconds >= 0 ? ttlSeconds : null,
          MaxAgeSeconds: null,
          CapPerScope: cap?.Cap,
          CapScopeKey: cap?.ScopeKey));
      }
      await coordinator.SyncPerspectiveRetentionAsync(retention, cancellationToken).ConfigureAwait(false);
    }

    // Pre-register snapshot of stored definitions, so a genuinely-new registration's previous_definition_id
    // resolves to the prior hashes (to tell settings-drift from schema-drift).
    var snapshot = await coordinator.GetTypeDefinitionsAsync(cancellationToken).ConfigureAwait(false);
    var byId = new Dictionary<int, TypeDefinitionInfo>(snapshot.Count);
    foreach (var d in snapshot) {
      byId[d.DefinitionId] = d;
    }

    var driftDetected = 0;
    var typesRegistered = 0;
    var typesReclassified = 0;

    foreach (var entry in _catalog.GetAll()) {
      cancellationToken.ThrowIfCancellationRequested();

      // Only entries the generator fingerprinted participate.
      if (string.IsNullOrEmpty(entry.SettingsHash) || string.IsNullOrEmpty(entry.SchemaHash)) {
        continue;
      }

      // schema_version stays 0 until [SchemaVersion] exists (event-versioning phase).
      typesRegistered++;
      var reg = await coordinator.RegisterTypeDefinitionAsync(
        entry.ClrTypeName, entry.SettingsHash, entry.SchemaHash, schemaVersion: 0, cancellationToken).ConfigureAwait(false);

      // Not new (already the current definition) — or new but first-ever (no prior to drift from).
      if (!reg.IsNew || reg.PreviousDefinitionId is not int prevId || !byId.TryGetValue(prevId, out var prev)) {
        continue;
      }

      driftDetected++;
      var settingsChanged = !string.Equals(prev.SettingsHashHex, entry.SettingsHash, StringComparison.OrdinalIgnoreCase);
      var schemaChanged = !string.Equals(prev.SchemaHashHex, entry.SchemaHash, StringComparison.OrdinalIgnoreCase);
      var isEphemeral = entry.Ephemeral is not null;

      var relationship = schemaChanged
        ? DefinitionRelationship.SchemaUpgradedTo
        : (isEphemeral ? DefinitionRelationship.ReclassifiedTo : DefinitionRelationship.MetadataChangedTo);
      await coordinator.RecordDefinitionLineageAsync(
        prevId, reg.DefinitionId, relationship, relationship.ToString(), cancellationToken).ConfigureAwait(false);
      LogDrift(_logger, entry.ClrTypeName, settingsChanged, schemaChanged, relationship.ToString());

      // Settings drift toward Ephemeral: reclassify historical events (act-opt-in).
      if (settingsChanged && isEphemeral && string.Equals(entry.Kind, "event", StringComparison.Ordinal)) {
        if (_options.ReconcileHistoricalOnStartup) {
          var names = new List<string>(1 + entry.FormerNames.Count) { entry.ClrTypeName };
          names.AddRange(entry.FormerNames);
          var result = await coordinator.ReclassifyEventsEphemeralAsync(names, cancellationToken).ConfigureAwait(false);
          if (result.EventsReclassified > 0) {
            typesReclassified++;
          }
          LogReclassified(_logger, entry.ClrTypeName, result.EventsReclassified, result.StreamsBlocked);
        } else {
          LogReclassifyPending(_logger, entry.ClrTypeName);
        }
      }

      // Schema drift: upcasting is the event-versioning phase's job — surface it for now.
      if (schemaChanged) {
        LogSchemaDrift(_logger, entry.ClrTypeName);
      }
    }

    return new TypeDefinitionReconcileSummary(driftDetected, typesReclassified, typesRegistered);
  }

  [LoggerMessage(EventId = 61, Level = LogLevel.Debug,
    Message = "Type-definition reconcile skipped — a sibling instance claimed this window.")]
  private static partial void LogSkippedByClaim(ILogger logger);

  [LoggerMessage(EventId = 9210, Level = LogLevel.Warning,
    Message = "Type-definition drift for '{TypeName}' (settingsChanged={SettingsChanged}, schemaChanged={SchemaChanged}) — recorded lineage {Relationship}.")]
  private static partial void LogDrift(ILogger logger, string typeName, bool settingsChanged, bool schemaChanged, string relationship);

  [LoggerMessage(EventId = 9211, Level = LogLevel.Information,
    Message = "Reclassified historical events of '{TypeName}' to Ephemeral: {EventsReclassified} events, {StreamsBlocked} streams skipped as mixed.")]
  private static partial void LogReclassified(ILogger logger, string typeName, long eventsReclassified, long streamsBlocked);

  [LoggerMessage(EventId = 9212, Level = LogLevel.Warning,
    Message = "'{TypeName}' is now Ephemeral but has historical Sourced events — reclassification is pending. Enable EphemeralOptions.ReconcileHistoricalOnStartup or run the reclassify command to reclaim them.")]
  private static partial void LogReclassifyPending(ILogger logger, string typeName);

  [LoggerMessage(EventId = 9213, Level = LogLevel.Warning,
    Message = "Payload schema drift for '{TypeName}' — events written under the prior schema may need upcasting (event-versioning).")]
  private static partial void LogSchemaDrift(ILogger logger, string typeName);
}

/// <summary>Outcome of a <see cref="TypeDefinitionReconciler"/> pass.</summary>
/// <docs>fundamentals/events/type-definition-fingerprint</docs>
public sealed record TypeDefinitionReconcileSummary(
    int DriftDetected,
    int TypesReclassified,
    int TypesRegistered = 0,
    bool Skipped = false) {
  /// <summary>Nothing found — the default/no-op result.</summary>
  public static TypeDefinitionReconcileSummary Empty { get; } = new(0, 0);

  /// <summary>A sibling instance already reconciled inside the claim window; no walk was performed.</summary>
  public static TypeDefinitionReconcileSummary SkippedByClaim { get; } = new(0, 0, 0, Skipped: true);
}
