using System;
using System.Collections.Generic;
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
  private readonly IWorkCoordinator _coordinator;
  private readonly EphemeralOptions _options;
  private readonly ILogger<TypeDefinitionReconciler> _logger;
  private readonly IMessageTypeCatalog? _catalog;

  /// <summary>Creates the reconciler. <paramref name="catalog"/> is optional — absent means no-op.</summary>
  public TypeDefinitionReconciler(
      IWorkCoordinator coordinator,
      IOptions<EphemeralOptions> options,
      ILogger<TypeDefinitionReconciler> logger,
      IMessageTypeCatalog? catalog = null) {
    _coordinator = coordinator;
    _options = options.Value;
    _logger = logger;
    _catalog = catalog;
  }

  /// <summary>Runs one reconciliation pass over the catalog. Returns a summary of what it found/did.</summary>
  public async Task<TypeDefinitionReconcileSummary> ReconcileAsync(CancellationToken cancellationToken = default) {
    if (_catalog is null) {
      return TypeDefinitionReconcileSummary.Empty;
    }

    // Pre-register snapshot of stored definitions, so a genuinely-new registration's previous_definition_id
    // resolves to the prior hashes (to tell settings-drift from schema-drift).
    var snapshot = await _coordinator.GetTypeDefinitionsAsync(cancellationToken).ConfigureAwait(false);
    var byId = new Dictionary<int, TypeDefinitionInfo>(snapshot.Count);
    foreach (var d in snapshot) {
      byId[d.DefinitionId] = d;
    }

    var driftDetected = 0;
    var typesReclassified = 0;

    foreach (var entry in _catalog.GetAll()) {
      cancellationToken.ThrowIfCancellationRequested();

      // Only entries the generator fingerprinted participate.
      if (string.IsNullOrEmpty(entry.SettingsHash) || string.IsNullOrEmpty(entry.SchemaHash)) {
        continue;
      }

      // schema_version stays 0 until [SchemaVersion] exists (event-versioning phase).
      var reg = await _coordinator.RegisterTypeDefinitionAsync(
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
      await _coordinator.RecordDefinitionLineageAsync(
        prevId, reg.DefinitionId, relationship, relationship.ToString(), cancellationToken).ConfigureAwait(false);
      LogDrift(_logger, entry.ClrTypeName, settingsChanged, schemaChanged, relationship.ToString());

      // Settings drift toward Ephemeral: reclassify historical events (act-opt-in).
      if (settingsChanged && isEphemeral && string.Equals(entry.Kind, "event", StringComparison.Ordinal)) {
        if (_options.ReconcileHistoricalOnStartup) {
          var names = new List<string>(1 + entry.FormerNames.Count) { entry.ClrTypeName };
          names.AddRange(entry.FormerNames);
          var result = await _coordinator.ReclassifyEventsEphemeralAsync(names, cancellationToken).ConfigureAwait(false);
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

    return new TypeDefinitionReconcileSummary(driftDetected, typesReclassified);
  }

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
public sealed record TypeDefinitionReconcileSummary(int DriftDetected, int TypesReclassified) {
  /// <summary>Nothing found — the default/no-op result.</summary>
  public static TypeDefinitionReconcileSummary Empty { get; } = new(0, 0);
}
