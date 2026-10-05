// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Issue #983: the generated runner strips a Split class model's promoted fields in place before the write,
/// so a snapshot of the same instance taken after the write lacks them. For that model the runner serializes
/// the snapshot before the write, and (issue #1002) only on a run whose snapshot is due, decided before the
/// write; for every other model (a record is stripped as a copy, and nothing else is stripped) the snapshot
/// after the write is the model it applied.
/// </summary>
/// <tests>src/Whizbang.Generators/PerspectiveRunnerGenerator.cs</tests>
/// <tests>src/Whizbang.Generators/Templates/PerspectiveRunnerTemplate.cs</tests>
[Category("SourceGenerators")]
public class PerspectiveRunnerSplitSnapshotTests {
  private const string SOURCE = """
    #nullable enable
    using System;
    using Whizbang.Core;
    using Whizbang.Core.Perspectives;

    namespace TestNamespace;

    public class ProductUpdatedEvent : IEvent {
      public Guid Id { get; set; }
    }

    [PerspectiveStorage(FieldStorageMode.Split)]
    public class SplitClassModel {
      [StreamId]
      public Guid Id { get; set; }

      [PhysicalField]
      public string Status { get; set; } = "";
    }

    [PerspectiveStorage(FieldStorageMode.Split)]
    public record SplitRecordModel {
      [StreamId]
      public Guid Id { get; init; }

      [PhysicalField]
      public string Status { get; init; } = "";
    }

    [PerspectiveStorage(FieldStorageMode.Extracted)]
    public class ExtractedClassModel {
      [StreamId]
      public Guid Id { get; set; }

      [PhysicalField]
      public string Status { get; set; } = "";
    }

    [PerspectiveStorage(FieldStorageMode.Split)]
    public class BareSplitClassModel {
      [StreamId]
      public Guid Id { get; set; }
    }

    public class SplitClassPerspective : IPerspectiveFor<SplitClassModel, ProductUpdatedEvent> {
      public SplitClassModel Apply(SplitClassModel currentData, ProductUpdatedEvent @event) => currentData;
    }

    public class SplitRecordPerspective : IPerspectiveFor<SplitRecordModel, ProductUpdatedEvent> {
      public SplitRecordModel Apply(SplitRecordModel currentData, ProductUpdatedEvent @event) => currentData;
    }

    public class ExtractedClassPerspective : IPerspectiveFor<ExtractedClassModel, ProductUpdatedEvent> {
      public ExtractedClassModel Apply(ExtractedClassModel currentData, ProductUpdatedEvent @event) => currentData;
    }

    public class BareSplitClassPerspective : IPerspectiveFor<BareSplitClassModel, ProductUpdatedEvent> {
      public BareSplitClassModel Apply(BareSplitClassModel currentData, ProductUpdatedEvent @event) => currentData;
    }
    """;

  private const string SNAPSHOTS_BEFORE_WRITE = "SnapshotBeforeWrite(global::TestNamespace.SplitClassModel model) => ToSnapshotJson(model);";

  private static string _runner(string perspective) {
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(SOURCE);
    return GeneratorTestHelper.GetGeneratedSource(result, $"{perspective}Runner.g.cs") ?? "";
  }

  private static string _compact(string source) => string.Concat(source.Where(c => !char.IsWhiteSpace(c)));

  [Test]
  [RequiresAssemblyFiles()]
  public async Task SplitClassModel_SerializesTheSnapshotBeforeTheWriteStripsItAsync() {
    var runner = _runner("SplitClassPerspective");

    await Assert.That(runner).Contains(SNAPSHOTS_BEFORE_WRITE);
    await Assert.That(runner).Contains("model.Status = default!;")
      .Because("the write still strips the promoted field from the document");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task EveryOtherModel_SnapshotsTheModelAfterTheWriteAsync() {
    foreach (var perspective in new[] { "SplitRecordPerspective", "ExtractedClassPerspective", "BareSplitClassPerspective" }) {
      var runner = _runner(perspective);

      await Assert.That(runner).IsNotEmpty();
      await Assert.That(runner).DoesNotContain("=> ToSnapshotJson(model);")
        .Because($"{perspective} is not stripped in place, so its snapshot after the write is the model it applied");
      await Assert.That(_compact(runner)).Contains("privatestaticJsonDocument?SnapshotBeforeWrite(")
        .Because("the call sites are the same for every model; this one never has a snapshot to hand them");
    }
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Runner_TakesTheSnapshotBeforeTheWriteAtBothWritesThatSnapshotAsync() {
    var runner = _compact(_runner("SplitClassPerspective"));

    // The end of a run, and the end of a rewind: each captures the snapshot before it writes and hands
    // it to the store in place of one taken afterwards.
    await Assert.That(runner.Split("snapshotBeforeWrite=snapshotDue?SnapshotBeforeWrite(updatedModel):null;").Length - 1).IsEqualTo(2);
    await Assert.That(runner.Split("snapshotBeforeWrite??ToSnapshotJson(updatedModel)").Length - 1).IsEqualTo(2);
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Runner_DecidesWhetherASnapshotIsDueBeforeTheWrite_AndSnapshotsOnThatDecisionAsync() {
    var runner = _compact(_runner("SplitClassPerspective"));

    // Issue #1002: the end of a run serializes before the write only when the snapshot after it is due.
    const string RUN_DUE = "varsnapshotDue=_snapshotStoreisnotnull&&_snapshotOptions?.Value.Enabled==true"
      + "&&!pendingPurge&&updatedModelisnotnull&&hasWrittenUpdate&&lastSuccessfulEventId.HasValue"
      + "&&_eventsSinceLastSnapshot+eventsProcessed>=_snapshotCadence().Threshold;";
    await Assert.That(runner).Contains(RUN_DUE);
    await Assert.That(runner.IndexOf(RUN_DUE, StringComparison.Ordinal))
      .IsLessThan(runner.IndexOf("awaitSaveModelAndCheckpointAsync(", StringComparison.Ordinal));
    await Assert.That(runner).Contains("_eventsSinceLastSnapshot+=eventsProcessed;if(snapshotDue){")
      .Because("the snapshot after the write is taken on the same decision, so the two cannot disagree");
    // The end of a rewind snapshots whenever snapshots are on.
    await Assert.That(runner).Contains(
      "varsnapshotDue=_snapshotStoreisnotnull&&_snapshotOptions?.Value.Enabled==true&&lastSuccessfulEventId.HasValue;");
    await Assert.That(runner).Contains("private(intThreshold,intRetention)_snapshotCadence(){");
  }
}
