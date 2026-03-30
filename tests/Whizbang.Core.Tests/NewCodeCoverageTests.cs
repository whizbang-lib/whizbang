using TUnit.Core;
using Whizbang.Core.Commands.System;
using Whizbang.Core.Data;
using Whizbang.Core.Events.System;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Perspectives.System;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests;

/// <summary>
/// Coverage tests for record types, models, and enums that need instantiation/property verification.
/// Covers: SystemEvents, PerspectiveStatusModel, IMigrationProvider records, SystemCommands,
/// IPerspectiveRebuilder records, and PendingMigrationRebuild.
/// </summary>
public class NewCodeCoverageTests {

  // ========================================
  // SystemEvents.cs — Perspective rebuild events
  // ========================================

  [Test]
  public async Task PerspectiveRebuildStarted_Properties_RoundTripCorrectlyAsync() {
    var streamId = Guid.NewGuid();
    var now = DateTimeOffset.UtcNow;

    var evt = new PerspectiveRebuildStarted(streamId, "OrderSummary", RebuildMode.BlueGreen, 42, now);

    await Assert.That(evt.StreamId).IsEqualTo(streamId);
    await Assert.That(evt.PerspectiveName).IsEqualTo("OrderSummary");
    await Assert.That(evt.Mode).IsEqualTo(RebuildMode.BlueGreen);
    await Assert.That(evt.TotalStreams).IsEqualTo(42);
    await Assert.That(evt.StartedAt).IsEqualTo(now);
  }

  [Test]
  public async Task PerspectiveRebuildProgress_Properties_RoundTripCorrectlyAsync() {
    var streamId = Guid.NewGuid();
    var now = DateTimeOffset.UtcNow;

    var evt = new PerspectiveRebuildProgress(streamId, "OrderSummary", RebuildMode.InPlace, 10, 50, 200, now);

    await Assert.That(evt.StreamId).IsEqualTo(streamId);
    await Assert.That(evt.PerspectiveName).IsEqualTo("OrderSummary");
    await Assert.That(evt.Mode).IsEqualTo(RebuildMode.InPlace);
    await Assert.That(evt.ProcessedStreams).IsEqualTo(10);
    await Assert.That(evt.TotalStreams).IsEqualTo(50);
    await Assert.That(evt.EventsReplayed).IsEqualTo(200);
    await Assert.That(evt.StartedAt).IsEqualTo(now);
  }

  [Test]
  public async Task PerspectiveRebuildCompleted_Properties_RoundTripCorrectlyAsync() {
    var streamId = Guid.NewGuid();
    var duration = TimeSpan.FromSeconds(45);

    var evt = new PerspectiveRebuildCompleted(streamId, "InventoryLevels", RebuildMode.SelectedStreams, 100, 500, duration);

    await Assert.That(evt.StreamId).IsEqualTo(streamId);
    await Assert.That(evt.PerspectiveName).IsEqualTo("InventoryLevels");
    await Assert.That(evt.Mode).IsEqualTo(RebuildMode.SelectedStreams);
    await Assert.That(evt.StreamsProcessed).IsEqualTo(100);
    await Assert.That(evt.EventsReplayed).IsEqualTo(500);
    await Assert.That(evt.Duration).IsEqualTo(duration);
  }

  [Test]
  public async Task PerspectiveRebuildFailed_Properties_RoundTripCorrectlyAsync() {
    var streamId = Guid.NewGuid();
    var duration = TimeSpan.FromSeconds(12);

    var evt = new PerspectiveRebuildFailed(streamId, "OrderSummary", RebuildMode.BlueGreen, "Connection lost", 5, duration);

    await Assert.That(evt.StreamId).IsEqualTo(streamId);
    await Assert.That(evt.PerspectiveName).IsEqualTo("OrderSummary");
    await Assert.That(evt.Mode).IsEqualTo(RebuildMode.BlueGreen);
    await Assert.That(evt.Error).IsEqualTo("Connection lost");
    await Assert.That(evt.StreamsProcessedBeforeFailure).IsEqualTo(5);
    await Assert.That(evt.Duration).IsEqualTo(duration);
  }

  // ========================================
  // SystemEvents.cs — Migration events
  // ========================================

  [Test]
  public async Task MigrationItemStarted_Properties_RoundTripCorrectlyAsync() {
    var streamId = Guid.NewGuid();

    var evt = new MigrationItemStarted(streamId, "perspective:Orders", MigrationStrategy.ColumnCopy, "abc123", "def456");

    await Assert.That(evt.StreamId).IsEqualTo(streamId);
    await Assert.That(evt.MigrationKey).IsEqualTo("perspective:Orders");
    await Assert.That(evt.Strategy).IsEqualTo(MigrationStrategy.ColumnCopy);
    await Assert.That(evt.OldHash).IsEqualTo("abc123");
    await Assert.That(evt.NewHash).IsEqualTo("def456");
  }

  [Test]
  public async Task MigrationItemStarted_NullOldHash_AllowedAsync() {
    var evt = new MigrationItemStarted(Guid.NewGuid(), "key", MigrationStrategy.DirectDdl, null, "hash");

    await Assert.That(evt.OldHash).IsNull();
  }

  [Test]
  public async Task MigrationItemCompleted_Properties_RoundTripCorrectlyAsync() {
    var streamId = Guid.NewGuid();
    var duration = TimeSpan.FromMilliseconds(350);

    var evt = new MigrationItemCompleted(streamId, "perspective:Orders", MigrationStatus.Applied, "Applied successfully", duration);

    await Assert.That(evt.StreamId).IsEqualTo(streamId);
    await Assert.That(evt.MigrationKey).IsEqualTo("perspective:Orders");
    await Assert.That(evt.Status).IsEqualTo(MigrationStatus.Applied);
    await Assert.That(evt.StatusDescription).IsEqualTo("Applied successfully");
    await Assert.That(evt.Duration).IsEqualTo(duration);
  }

  [Test]
  public async Task MigrationItemFailed_Properties_RoundTripCorrectlyAsync() {
    var streamId = Guid.NewGuid();
    var duration = TimeSpan.FromSeconds(2);

    var evt = new MigrationItemFailed(streamId, "infra:events", MigrationStatus.Failed, MigrationFailureReason.SqlError, "syntax error", duration);

    await Assert.That(evt.StreamId).IsEqualTo(streamId);
    await Assert.That(evt.MigrationKey).IsEqualTo("infra:events");
    await Assert.That(evt.Status).IsEqualTo(MigrationStatus.Failed);
    await Assert.That(evt.FailureReason).IsEqualTo(MigrationFailureReason.SqlError);
    await Assert.That(evt.Error).IsEqualTo("syntax error");
    await Assert.That(evt.Duration).IsEqualTo(duration);
  }

  [Test]
  public async Task MigrationBatchStarted_Properties_RoundTripCorrectlyAsync() {
    var streamId = Guid.NewGuid();

    var evt = new MigrationBatchStarted(streamId, "0.9.4", 10, 3);

    await Assert.That(evt.StreamId).IsEqualTo(streamId);
    await Assert.That(evt.LibraryVersion).IsEqualTo("0.9.4");
    await Assert.That(evt.TotalMigrations).IsEqualTo(10);
    await Assert.That(evt.TotalPerspectives).IsEqualTo(3);
  }

  [Test]
  public async Task MigrationBatchCompleted_Properties_RoundTripCorrectlyAsync() {
    var streamId = Guid.NewGuid();
    var duration = TimeSpan.FromSeconds(30);
    var results = new[] {
      new MigrationBatchItemResult("key1", MigrationStatus.Applied, "Applied"),
      new MigrationBatchItemResult("key2", MigrationStatus.Skipped, "No changes")
    };

    var evt = new MigrationBatchCompleted(streamId, "0.9.5", results, 1, 0, 1, 0, duration);

    await Assert.That(evt.StreamId).IsEqualTo(streamId);
    await Assert.That(evt.LibraryVersion).IsEqualTo("0.9.5");
    await Assert.That(evt.Results.Length).IsEqualTo(2);
    await Assert.That(evt.Applied).IsEqualTo(1);
    await Assert.That(evt.Updated).IsEqualTo(0);
    await Assert.That(evt.Skipped).IsEqualTo(1);
    await Assert.That(evt.Failed).IsEqualTo(0);
    await Assert.That(evt.TotalDuration).IsEqualTo(duration);
  }

  [Test]
  public async Task MigrationBatchItemResult_Properties_RoundTripCorrectlyAsync() {
    var result = new MigrationBatchItemResult("perspective:Orders", MigrationStatus.Updated, "Hash changed");

    await Assert.That(result.MigrationKey).IsEqualTo("perspective:Orders");
    await Assert.That(result.Status).IsEqualTo(MigrationStatus.Updated);
    await Assert.That(result.StatusDescription).IsEqualTo("Hash changed");
  }

  // ========================================
  // SystemEvents.cs — Enums
  // ========================================

  [Test]
  public async Task MigrationStatus_AllValues_AreDefinedAsync() {
    var values = Enum.GetValues<MigrationStatus>();
    await Assert.That(values).Contains(MigrationStatus.Applied);
    await Assert.That(values).Contains(MigrationStatus.Updated);
    await Assert.That(values).Contains(MigrationStatus.Skipped);
    await Assert.That(values).Contains(MigrationStatus.MigratingInBackground);
    await Assert.That(values).Contains(MigrationStatus.Failed);
  }

  [Test]
  public async Task MigrationStrategy_AllValues_AreDefinedAsync() {
    var strategies = Enum.GetValues<MigrationStrategy>();
    await Assert.That(strategies).Contains(MigrationStrategy.DirectDdl);
    await Assert.That(strategies).Contains(MigrationStrategy.ColumnCopy);
    await Assert.That(strategies).Contains(MigrationStrategy.EventReplay);
  }

  [Test]
  public async Task MigrationFailureReason_AllValues_AreDefinedAsync() {
    var values = Enum.GetValues<MigrationFailureReason>();
    await Assert.That(values).Contains(MigrationFailureReason.Unknown);
    await Assert.That(values).Contains(MigrationFailureReason.SqlError);
    await Assert.That(values).Contains(MigrationFailureReason.Timeout);
    await Assert.That(values).Contains(MigrationFailureReason.ColumnTypeMismatch);
    await Assert.That(values).Contains(MigrationFailureReason.DataCopyFailed);
    await Assert.That(values).Contains(MigrationFailureReason.SwapFailed);
  }

  // ========================================
  // PerspectiveStatusModel.cs
  // ========================================

  [Test]
  public async Task PerspectiveStatusModel_AllProperties_RoundTripCorrectlyAsync() {
    var id = Guid.NewGuid();
    var now = DateTimeOffset.UtcNow;
    var duration = TimeSpan.FromMinutes(5);

    var model = new PerspectiveStatusModel {
      Id = id,
      PerspectiveName = "OrderSummary",
      State = PerspectiveState.Rebuilding,
      SchemaHash = "abc123",
      LastRebuildStartedAt = now,
      LastRebuildCompletedAt = now.AddMinutes(5),
      LastRebuildDuration = duration,
      LastRebuildMode = RebuildMode.BlueGreen,
      LastError = null,
      LastUpdatedAt = now
    };

    await Assert.That(model.Id).IsEqualTo(id);
    await Assert.That(model.PerspectiveName).IsEqualTo("OrderSummary");
    await Assert.That(model.State).IsEqualTo(PerspectiveState.Rebuilding);
    await Assert.That(model.SchemaHash).IsEqualTo("abc123");
    await Assert.That(model.LastRebuildStartedAt).IsEqualTo(now);
    await Assert.That(model.LastRebuildCompletedAt).IsEqualTo(now.AddMinutes(5));
    await Assert.That(model.LastRebuildDuration).IsEqualTo(duration);
    await Assert.That(model.LastRebuildMode).IsEqualTo(RebuildMode.BlueGreen);
    await Assert.That(model.LastError).IsNull();
    await Assert.That(model.LastUpdatedAt).IsEqualTo(now);
  }

  [Test]
  public async Task PerspectiveStatusModel_Defaults_AreCorrectAsync() {
    var model = new PerspectiveStatusModel();

    await Assert.That(model.Id).IsEqualTo(Guid.Empty);
    await Assert.That(model.PerspectiveName).IsEqualTo("");
    await Assert.That(model.State).IsEqualTo(PerspectiveState.Active);
    await Assert.That(model.SchemaHash).IsNull();
    await Assert.That(model.LastRebuildStartedAt).IsNull();
    await Assert.That(model.LastRebuildCompletedAt).IsNull();
    await Assert.That(model.LastRebuildDuration).IsNull();
    await Assert.That(model.LastRebuildMode).IsNull();
    await Assert.That(model.LastError).IsNull();
  }

  [Test]
  public async Task PerspectiveStatusModel_WithError_StoresErrorAsync() {
    var model = new PerspectiveStatusModel {
      State = PerspectiveState.Failed,
      LastError = "Connection timeout"
    };

    await Assert.That(model.State).IsEqualTo(PerspectiveState.Failed);
    await Assert.That(model.LastError).IsEqualTo("Connection timeout");
  }

  [Test]
  public async Task PerspectiveState_AllValues_AreDefinedAsync() {
    var states = Enum.GetValues<PerspectiveState>();
    await Assert.That(states).Contains(PerspectiveState.Active);
    await Assert.That(states).Contains(PerspectiveState.Rebuilding);
    await Assert.That(states).Contains(PerspectiveState.MigratingBlueGreen);
    await Assert.That(states).Contains(PerspectiveState.Failed);
    await Assert.That(states).Contains(PerspectiveState.Stale);
  }

  // ========================================
  // IMigrationProvider.cs — Records and Enum
  // ========================================

  [Test]
  public async Task MigrationScript_Properties_RoundTripCorrectlyAsync() {
    var script = new MigrationScript("001_create_events", "CREATE TABLE wh_events (...)");

    await Assert.That(script.Name).IsEqualTo("001_create_events");
    await Assert.That(script.Sql).IsEqualTo("CREATE TABLE wh_events (...)");
  }

  [Test]
  public async Task MigrationStep_AllProperties_RoundTripCorrectlyAsync() {
    var added = new[] { "new_col" };
    var removed = new[] { "old_col" };
    var step = new MigrationStep("perspective:Orders", MigrationAction.BlueGreenColumnCopy, "old", "new", added, removed);

    await Assert.That(step.Name).IsEqualTo("perspective:Orders");
    await Assert.That(step.Action).IsEqualTo(MigrationAction.BlueGreenColumnCopy);
    await Assert.That(step.OldHash).IsEqualTo("old");
    await Assert.That(step.NewHash).IsEqualTo("new");
    await Assert.That(step.AddedColumns).IsNotNull();
    await Assert.That(step.AddedColumns![0]).IsEqualTo("new_col");
    await Assert.That(step.RemovedColumns).IsNotNull();
    await Assert.That(step.RemovedColumns![0]).IsEqualTo("old_col");
  }

  [Test]
  public async Task MigrationStep_NullableColumns_AllowNullAsync() {
    var step = new MigrationStep("key", MigrationAction.Apply, null, "hash", null, null);

    await Assert.That(step.OldHash).IsNull();
    await Assert.That(step.AddedColumns).IsNull();
    await Assert.That(step.RemovedColumns).IsNull();
  }

  [Test]
  public async Task MigrationPlan_WithSteps_StoresStepsAsync() {
    var steps = new List<MigrationStep> {
      new("step1", MigrationAction.Apply, null, "h1", null, null),
      new("step2", MigrationAction.Skip, "h1", "h1", null, null)
    };
    var plan = new MigrationPlan(steps);

    await Assert.That(plan.Steps.Count).IsEqualTo(2);
    await Assert.That(plan.Steps[0].Name).IsEqualTo("step1");
    await Assert.That(plan.Steps[1].Action).IsEqualTo(MigrationAction.Skip);
  }

  [Test]
  public async Task MigrationAction_AllValues_AreDefinedAsync() {
    var actions = Enum.GetValues<MigrationAction>();
    await Assert.That(actions).Contains(MigrationAction.Skip);
    await Assert.That(actions).Contains(MigrationAction.Apply);
    await Assert.That(actions).Contains(MigrationAction.Update);
    await Assert.That(actions).Contains(MigrationAction.BlueGreenColumnCopy);
    await Assert.That(actions).Contains(MigrationAction.BlueGreenEventReplay);
  }

  // ========================================
  // SystemCommands.cs
  // ========================================

  [Test]
  public async Task RebuildPerspectiveCommand_Defaults_AreCorrectAsync() {
    var cmd = new RebuildPerspectiveCommand();

    await Assert.That(cmd.PerspectiveNames).IsNull();
    await Assert.That(cmd.Mode).IsEqualTo(RebuildMode.BlueGreen);
    await Assert.That(cmd.IncludeStreamIds).IsNull();
    await Assert.That(cmd.ExcludeStreamIds).IsNull();
    await Assert.That(cmd.FromEventId).IsNull();
  }

  [Test]
  public async Task RebuildPerspectiveCommand_WithAllProperties_RoundTripsAsync() {
    var includes = new[] { Guid.NewGuid() };
    var excludes = new[] { Guid.NewGuid() };
    var cmd = new RebuildPerspectiveCommand(
      PerspectiveNames: ["OrderSummary"],
      Mode: RebuildMode.InPlace,
      IncludeStreamIds: includes,
      ExcludeStreamIds: excludes,
      FromEventId: 100
    );

    await Assert.That(cmd.PerspectiveNames).IsNotNull();
    await Assert.That(cmd.PerspectiveNames![0]).IsEqualTo("OrderSummary");
    await Assert.That(cmd.Mode).IsEqualTo(RebuildMode.InPlace);
    await Assert.That(cmd.IncludeStreamIds).IsNotNull();
    await Assert.That(cmd.ExcludeStreamIds).IsNotNull();
    await Assert.That(cmd.FromEventId).IsEqualTo(100);
  }

  [Test]
  public async Task CancelPerspectiveRebuildCommand_Properties_RoundTripCorrectlyAsync() {
    var cmd = new CancelPerspectiveRebuildCommand("OrderSummary");

    await Assert.That(cmd.PerspectiveName).IsEqualTo("OrderSummary");
  }

  [Test]
  public async Task ClearCacheCommand_Defaults_AreNullAsync() {
    var cmd = new ClearCacheCommand();

    await Assert.That(cmd.CacheKey).IsNull();
    await Assert.That(cmd.CacheRegion).IsNull();
  }

  [Test]
  public async Task ClearCacheCommand_WithValues_RoundTripsAsync() {
    var cmd = new ClearCacheCommand("user:123", "sessions");

    await Assert.That(cmd.CacheKey).IsEqualTo("user:123");
    await Assert.That(cmd.CacheRegion).IsEqualTo("sessions");
  }

  [Test]
  public async Task DiagnosticsCommand_Properties_RoundTripCorrectlyAsync() {
    var correlationId = Guid.NewGuid();
    var cmd = new DiagnosticsCommand(DiagnosticType.Full, correlationId);

    await Assert.That(cmd.Type).IsEqualTo(DiagnosticType.Full);
    await Assert.That(cmd.CorrelationId).IsEqualTo(correlationId);
  }

  [Test]
  public async Task DiagnosticsCommand_DefaultCorrelationId_IsNullAsync() {
    var cmd = new DiagnosticsCommand(DiagnosticType.HealthCheck);

    await Assert.That(cmd.CorrelationId).IsNull();
  }

  [Test]
  public async Task DiagnosticType_AllValues_AreDefinedAsync() {
    var types = Enum.GetValues<DiagnosticType>();
    await Assert.That(types).Contains(DiagnosticType.HealthCheck);
    await Assert.That(types).Contains(DiagnosticType.ResourceMetrics);
    await Assert.That(types).Contains(DiagnosticType.PipelineStatus);
    await Assert.That(types).Contains(DiagnosticType.PerspectiveStatus);
    await Assert.That(types).Contains(DiagnosticType.Full);
  }

  [Test]
  public async Task PauseProcessingCommand_Defaults_AreNullAsync() {
    var cmd = new PauseProcessingCommand();

    await Assert.That(cmd.DurationSeconds).IsNull();
    await Assert.That(cmd.Reason).IsNull();
  }

  [Test]
  public async Task PauseProcessingCommand_WithValues_RoundTripsAsync() {
    var cmd = new PauseProcessingCommand(DurationSeconds: 300, Reason: "Maintenance window");

    await Assert.That(cmd.DurationSeconds).IsEqualTo(300);
    await Assert.That(cmd.Reason).IsEqualTo("Maintenance window");
  }

  [Test]
  public async Task ResumeProcessingCommand_Default_ReasonIsNullAsync() {
    var cmd = new ResumeProcessingCommand();

    await Assert.That(cmd.Reason).IsNull();
  }

  [Test]
  public async Task ResumeProcessingCommand_WithReason_RoundTripsAsync() {
    var cmd = new ResumeProcessingCommand("Maintenance complete");

    await Assert.That(cmd.Reason).IsEqualTo("Maintenance complete");
  }

  // ========================================
  // IPerspectiveRebuilder.cs — Records and Enum
  // ========================================

  [Test]
  public async Task RebuildResult_Success_AllProperties_RoundTripCorrectlyAsync() {
    var duration = TimeSpan.FromSeconds(30);
    var result = new RebuildResult("OrderSummary", 100, 500, duration, true, null);

    await Assert.That(result.PerspectiveName).IsEqualTo("OrderSummary");
    await Assert.That(result.StreamsProcessed).IsEqualTo(100);
    await Assert.That(result.EventsReplayed).IsEqualTo(500);
    await Assert.That(result.Duration).IsEqualTo(duration);
    await Assert.That(result.Success).IsTrue();
    await Assert.That(result.Error).IsNull();
  }

  [Test]
  public async Task RebuildResult_Failure_StoresErrorAsync() {
    var result = new RebuildResult("OrderSummary", 5, 10, TimeSpan.FromSeconds(2), false, "Connection lost");

    await Assert.That(result.Success).IsFalse();
    await Assert.That(result.Error).IsEqualTo("Connection lost");
  }

  [Test]
  public async Task RebuildStatus_Properties_RoundTripCorrectlyAsync() {
    var now = DateTimeOffset.UtcNow;
    var status = new RebuildStatus("OrderSummary", RebuildMode.BlueGreen, 100, 50, now);

    await Assert.That(status.PerspectiveName).IsEqualTo("OrderSummary");
    await Assert.That(status.Mode).IsEqualTo(RebuildMode.BlueGreen);
    await Assert.That(status.TotalStreams).IsEqualTo(100);
    await Assert.That(status.ProcessedStreams).IsEqualTo(50);
    await Assert.That(status.StartedAt).IsEqualTo(now);
  }

  [Test]
  public async Task RebuildMode_AllValues_AreDefinedAsync() {
    var modes = Enum.GetValues<RebuildMode>();
    await Assert.That(modes).Contains(RebuildMode.BlueGreen);
    await Assert.That(modes).Contains(RebuildMode.InPlace);
    await Assert.That(modes).Contains(RebuildMode.SelectedStreams);
  }

  // ========================================
  // PendingMigrationRebuild record
  // ========================================

  [Test]
  public async Task PendingMigrationRebuild_Properties_RoundTripCorrectlyAsync() {
    var pending = new PendingMigrationRebuild("OrderSummary", "perspective:OrderSummary");

    await Assert.That(pending.PerspectiveName).IsEqualTo("OrderSummary");
    await Assert.That(pending.MigrationKey).IsEqualTo("perspective:OrderSummary");
  }
}
