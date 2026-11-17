// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

extern alias shared;

using System.Diagnostics.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using TemplateUtilities = shared::Whizbang.Generators.Shared.Utilities.TemplateUtilities;

namespace Whizbang.Generators.Tests;

/// <summary>
/// #2246: every generated runner splits its apply into a prepare, which folds the batch and returns the row write
/// instead of making it, and a complete, which does what follows the write. The per-pair path is built from the same
/// pieces, so the two cannot drift: one fold loop, one row load, one eviction probe, the same physical values and
/// Split stripping on both writes. Prepare and complete take no apply lock, because the caller holds the locks of the
/// whole batch across its write.
/// </summary>
/// <tests>src/Whizbang.Generators/PerspectiveRunnerGenerator.cs</tests>
/// <tests>src/Whizbang.Generators/Templates/PerspectiveRunnerTemplate.cs</tests>
[Category("SourceGenerators")]
public class PerspectiveRunnerPrepareCompleteTests {
  private const string SOURCE = """
    #nullable enable
    using System;
    using Whizbang.Core;
    using Whizbang.Core.Perspectives;

    namespace TestNamespace;

    public class ThingChangedEvent : IEvent {
      [StreamId]
      public Guid Id { get; set; }
      public string Status { get; set; } = "";
    }

    public class PlainModel {
      [StreamId]
      public Guid Id { get; set; }
      public string Status { get; set; } = "";
    }

    [PerspectiveStorage(FieldStorageMode.Split)]
    public class SplitClassModel {
      [StreamId]
      public Guid Id { get; set; }

      [PhysicalField]
      public string Status { get; set; } = "";
    }

    [PerspectiveStorage(FieldStorageMode.Extracted)]
    public record ExtractedRecordModel {
      [StreamId]
      public Guid Id { get; init; }

      [PhysicalField]
      public string Status { get; init; } = "";
    }

    public class PlainPerspective : IPerspectiveFor<PlainModel, ThingChangedEvent> {
      public PlainModel Apply(PlainModel currentData, ThingChangedEvent @event) => new() { Id = @event.Id, Status = @event.Status };
    }

    public class SplitClassPerspective : IPerspectiveFor<SplitClassModel, ThingChangedEvent> {
      public SplitClassModel Apply(SplitClassModel currentData, ThingChangedEvent @event) {
        currentData.Status = @event.Status;
        return currentData;
      }
    }

    public class ExtractedRecordPerspective : IPerspectiveFor<ExtractedRecordModel, ThingChangedEvent> {
      public ExtractedRecordModel Apply(ExtractedRecordModel currentData, ThingChangedEvent @event) =>
        currentData with { Id = @event.Id, Status = @event.Status };
    }
    """;

  private static readonly System.Reflection.Assembly _generatorsAssembly = typeof(MessageRegistryGenerator).Assembly;

  [RequiresAssemblyFiles()]
  private static string _runner(string perspective) {
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(SOURCE);
    return GeneratorTestHelper.GetGeneratedSource(result, $"{perspective}Runner.g.cs") ?? "";
  }

  [RequiresAssemblyFiles()]
  private static string _template() =>
    TemplateUtilities.GetEmbeddedTemplate(_generatorsAssembly, "PerspectiveRunnerTemplate.cs");

  private static string _compact(string source) => string.Concat(source.Where(c => !char.IsWhiteSpace(c)));

  private static int _count(string source, string text) => source.Split(text).Length - 1;

  /// <summary>The text of one method, from its signature to the next member's signature.</summary>
  private static string _method(string source, string signature, string nextSignature) {
    var start = source.IndexOf(signature, StringComparison.Ordinal);
    var end = source.IndexOf(nextSignature, start + signature.Length, StringComparison.Ordinal);
    return start < 0 || end < 0 ? "" : source[start..end];
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task EveryRunner_OverridesPrepareAndCompleteAsync() {
    foreach (var perspective in new[] { "PlainPerspective", "SplitClassPerspective", "ExtractedRecordPerspective" }) {
      var runner = _compact(_runner(perspective));

      await Assert.That(runner).Contains("publicasyncTask<PreparedPerspectiveApply>PrepareAsync(")
        .Because($"{perspective} must take the set-based write, not the default that hands every pair back");
      await Assert.That(runner).Contains("publicasyncTask<PerspectiveCursorCompletion>CompleteAsync(");
    }
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task GeneratedRunners_CompileAsync() {
    var errors = GeneratorTestHelper.GetGeneratedCompilationErrors<PerspectiveRunnerGenerator>(SOURCE);

    await Assert.That(errors).IsEmpty();
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PrepareAndComplete_TakeNoApplyLockAsync() {
    var template = _template();
    var prepare = _method(template, "public async Task<PreparedPerspectiveApply> PrepareAsync(", "public async Task<PerspectiveCursorCompletion> CompleteAsync(");
    var complete = _method(template, "public async Task<PerspectiveCursorCompletion> CompleteAsync(", "/// <summary>");

    await Assert.That(prepare).IsNotEmpty();
    await Assert.That(complete).IsNotEmpty();
    await Assert.That(prepare).DoesNotContain("AcquireAsync(")
      .Because("the caller holds the batch's locks across its write; a lock taken here would be released before the write");
    await Assert.That(complete).DoesNotContain("AcquireAsync(")
      .Because("the coordinator's lock is not reentrant, so taking it again under the caller's would deadlock");
    await Assert.That(complete).DoesNotContain("RunWithEventsAsync(")
      .Because("the public per-pair entry takes the lock; complete reruns a pair through the lock-free core");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerPairAndPrepare_ShareOneFoldOneLoadAndOneProbeAsync() {
    var template = _template();

    await Assert.That(_count(template, "foreach (var envelope in foldEvents)")).IsEqualTo(2)
      .Because("one fold for the live apply (per-pair and prepare) and one for the replay; a third is a copy that can drift");
    await Assert.That(_count(template, "ApplyEvent(perspective")).IsEqualTo(4);
    await Assert.That(_count(template, "HasStreamEventsBeforeAsync(")).IsEqualTo(1)
      .Because("the eviction probe is made in one place for both entries");
    await Assert.That(_count(template, "await _perspectiveStore.ReadForApplyAsync(")).IsEqualTo(3)
      .Because("the live row load is one method, the seam a batched state source replaces; the other two are the replay's");
    await Assert.That(template).Contains("private async Task<RowLoad> _loadRowForApplyAsync(");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PlainModel_PreparesItsWriteWithNoPhysicalValuesAsync() {
    var runner = _compact(_runner("PlainPerspective"));

    await Assert.That(runner).Contains("_perspectiveStore.PrepareWrite(streamId,model,scope??newglobal::Whizbang.Core.Lenses.PerspectiveScope(),scope!=null&&forceUpdateScope,metadata,expectedVersion,null)")
      .Because("the per-stream write passes a null scope as an empty one that never forces a scope update; the prepared write must too");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task SplitClassModel_PreparesItsWriteWithThePhysicalValuesAndTheStrippedDocumentAsync() {
    var runner = _runner("SplitClassPerspective");

    await Assert.That(_compact(runner)).Contains("_perspectiveStore.PrepareWrite(streamId,model,scope??newglobal::Whizbang.Core.Lenses.PerspectiveScope(),forceUpdateScope,metadata,expectedVersion,physicalFieldValues)");
    await Assert.That(_count(runner, "{ \"status\", model.Status }")).IsEqualTo(2)
      .Because("both writes take the promoted column's value from the model before it is stripped");
    await Assert.That(_count(runner, "model.Status = default!;")).IsEqualTo(2)
      .Because("both writes keep the promoted field out of the document of a Split model");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task ExtractedRecordModel_PreparesItsWriteWithThePhysicalValuesAndAFullDocumentAsync() {
    var runner = _runner("ExtractedRecordPerspective");

    await Assert.That(_count(runner, "{ \"status\", model.Status }")).IsEqualTo(2);
    await Assert.That(runner).DoesNotContain("model = model with")
      .Because("an Extracted model keeps its fields in the document as well as in their columns");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task APurge_IsPreparedThroughTheStoreAfterItsMarkerAsync() {
    var template = _template();
    var prepare = _method(template, "public async Task<PreparedPerspectiveApply> PrepareAsync(", "public async Task<PerspectiveCursorCompletion> CompleteAsync(");
    var preparePurge = prepare.IndexOf("_perspectiveStore.PreparePurge(streamId)", StringComparison.Ordinal);
    var marker = prepare.IndexOf("_markPurgedAsync(", StringComparison.Ordinal);

    await Assert.That(preparePurge).IsGreaterThan(0);
    await Assert.That(marker).IsGreaterThan(preparePurge)
      .Because("the marker is recorded once the purge is known to take the batch write, and before the row is removed, "
             + "so a crash leaves a marker on a live row and never a missing row a later event could recreate");
  }
}
