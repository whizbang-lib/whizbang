// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Configuration;
using Whizbang.Core.Fingerprint;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Fingerprint;

/// <summary>
/// Branch coverage for the lineage relationship <see cref="TypeDefinitionReconciler.ReconcileAsync"/>
/// records on drift. The sibling coverage suite pins the metadata-only edge; these pin the other
/// two: a settings change toward Ephemeral with the shape unchanged is a reclassification, and a
/// shape change is a schema upgrade even when the type is also ephemeral (the shape change is the
/// one that needs an upcaster, so it must not be reported as a mere reclassification).
/// </summary>
/// <code-under-test>src/Whizbang.Core/Fingerprint/TypeDefinitionReconciler.cs</code-under-test>
public class TypeDefinitionReconcilerBranchCoverageTests {

  private static readonly EphemeralInfo _ephemeral = new(Destruction.WhenConsumed, TransientStorage.PersistedRow, -1, -1);

  [Test]
  public async Task ReconcileAsync_SettingsChangedTowardEphemeral_SchemaUnchanged_RecordsReclassificationAsync() {
    var catalog = new FakeCatalog(
      new MessageTypeCatalogEntry(typeof(object), "DriftEvent", "event", null) {
        SettingsHash = "settings-new",
        SchemaHash = "schema-same",
        Ephemeral = _ephemeral
      });
    var coordinator = new LineageCapturingDriftCoordinator(storedSettingsHash: "settings-old", storedSchemaHash: "schema-same");

    var summary = await _reconciler(coordinator, catalog).ReconcileAsync(CancellationToken.None);

    await Assert.That(summary.DriftDetected).IsEqualTo(1);
    await Assert.That(coordinator.Recorded).IsNotNull();
    await Assert.That(coordinator.Recorded!.Value.Relationship).IsEqualTo(DefinitionRelationship.ReclassifiedTo)
      .Because("the payload shape is unchanged and the type is now ephemeral: that is a reclassification, "
        + "not a metadata tweak and not a schema upgrade");
  }

  [Test]
  public async Task ReconcileAsync_SchemaChangedOnAnEphemeralType_RecordsSchemaUpgradeAsync() {
    var catalog = new FakeCatalog(
      new MessageTypeCatalogEntry(typeof(object), "DriftEvent", "event", null) {
        SettingsHash = "settings-same",
        SchemaHash = "schema-new",
        Ephemeral = _ephemeral
      });
    var coordinator = new LineageCapturingDriftCoordinator(storedSettingsHash: "settings-same", storedSchemaHash: "schema-old");

    var summary = await _reconciler(coordinator, catalog).ReconcileAsync(CancellationToken.None);

    await Assert.That(summary.DriftDetected).IsEqualTo(1);
    await Assert.That(coordinator.Recorded).IsNotNull();
    await Assert.That(coordinator.Recorded!.Value.Relationship).IsEqualTo(DefinitionRelationship.SchemaUpgradedTo)
      .Because("a changed payload shape takes precedence over the ephemeral classification: it is the change an "
        + "operator has to act on with an upcaster");
  }

  private static TypeDefinitionReconciler _reconciler(IWorkCoordinator coordinator, IMessageTypeCatalog catalog) {
    var services = new ServiceCollection();
    services.AddSingleton(coordinator);
    var provider = services.BuildServiceProvider();
    return new TypeDefinitionReconciler(
      provider.GetRequiredService<IServiceScopeFactory>(),
      Options.Create(new EphemeralOptions()),
      NullLogger<TypeDefinitionReconciler>.Instance,
      catalog);
  }

  private sealed class FakeCatalog(params MessageTypeCatalogEntry[] entries) : IMessageTypeCatalog {
    public IReadOnlyList<MessageTypeCatalogEntry> GetAll() => entries;
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
}
