// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Linq.Expressions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives.Hooks;
using Whizbang.Data.Postgres.Collective;

namespace Whizbang.Data.EFCore.Postgres.Tests.BranchCoverage.SecondPass;

/// <summary>
/// One collective apply hook that records every kind of op, folded by the planner and composed into a
/// cohort, so a single class takes every arm of the fold's switch and both outcomes of each parameter
/// rebinding: the hook's own parameter is replaced, and a parameter of a lambda nested inside the
/// predicate is left alone. No database.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Collective/CollectiveApplyHookPlanner.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Collective/CollectiveApplyHookPlan.cs</code-under-test>
[Category("Unit")]
[Category("Shard2")]
public class CollectiveApplyHookFoldTests {

  [Test]
  public async Task Resolve_EveryOpKind_FoldsEachIntoItsPartOfThePlanAsync() {
    var plan = _resolve();

    await Assert.That(plan.ModelFieldSetters).Count().IsEqualTo(1);
    await Assert.That(plan.ModelFieldSetters[0].PropertyName).IsEqualTo(nameof(TaggedModel.Name));
    await Assert.That(plan.RemovedModelFields.Contains(nameof(TaggedModel.Tags))).IsTrue();
    await Assert.That(plan.StoreColumns).Count().IsEqualTo(1)
      .Because("the version column is dropped in favor of the bump, and a column set twice is listed once");
    await Assert.That(plan.StoreColumns[0].Column).IsEqualTo("audit_marker");
    await Assert.That(plan.StoreColumns[0].Value).IsEqualTo(2).Because("the last write to a column wins");
    await Assert.That(plan.BumpVersion).IsTrue();
    await Assert.That(plan.AndWheres).Count().IsEqualTo(1);
    await Assert.That(plan.ReplaceWhere).IsNotNull();
  }

  // The composed cohort is the replacement predicate AND the refinement, each rebased onto one row
  // parameter. The refinement holds a nested lambda (t => t == "keep"); its parameter must survive the
  // rebinding untouched, or the compiled predicate would read the wrong value or fail to compile.
  [Test]
  public async Task ComposeCohort_ReplacementAndRefinement_SelectOnlyRowsMatchingBothAsync() {
    var plan = _resolve();
    Expression<Func<PerspectiveRow<TaggedModel>, bool>> specWhere = r => r.Data.Name == "spec-only";

    var cohort = plan.ComposeCohort(specWhere);

    await Assert.That(cohort).IsNotNull();
    var matches = cohort!.Compile();
    await Assert.That(matches(_row("a", "keep"))).IsTrue();
    await Assert.That(matches(_row("skip", "keep"))).IsFalse()
      .Because("the replacement predicate excludes this name");
    await Assert.That(matches(_row("a", "drop"))).IsFalse()
      .Because("the refinement requires a 'keep' tag");
    await Assert.That(matches(_row("spec-only", "drop"))).IsFalse()
      .Because("the replacement swaps the spec's own predicate out entirely");
  }

  private static CollectiveApplyHookPlan<TaggedModel> _resolve() {
    var registry = new CollectiveApplyHookRegistry();
    registry.Register<TaggedModel>(new EveryOpHook());
    var context = new ApplyHookContext { ModelType = typeof(TaggedModel), ApplyTimestamp = DateTimeOffset.UtcNow };
    return CollectiveApplyHookPlanner.Resolve<TaggedModel>(registry, context);
  }

  private static PerspectiveRow<TaggedModel> _row(string name, string tag) => new() {
    Id = Guid.NewGuid(),
    Data = new TaggedModel { Name = name, Tags = [tag] },
    Metadata = new PerspectiveMetadata { EventType = "e", EventId = Guid.NewGuid().ToString(), Timestamp = DateTime.UtcNow },
    Scope = new PerspectiveScope(),
    CreatedAt = DateTime.UtcNow,
    UpdatedAt = DateTime.UtcNow,
    Version = 1,
  };

  private sealed class TaggedModel {
    public string Name { get; set; } = string.Empty;
    public List<string> Tags { get; set; } = [];
  }

  private sealed class EveryOpHook : ICollectiveApplyHook<TaggedModel> {
    public void Configure(ICollectiveApplyHookBuilder<TaggedModel> builder, ApplyHookContext context) {
      builder.SetProperty(m => m.Name, "hooked");
      builder.RemoveSetter(m => m.Tags);
      builder.SetColumn("audit_marker", 1);
      builder.SetColumn("audit_marker", 2);
      builder.SetColumn(ApplyHookColumns.VERSION, 7L);
      builder.BumpVersion();
      builder.SuppressActivity();
      builder.AndWhere(m => m.Tags.Any(t => t == "keep"));
      builder.ReplaceWhere(m => m.Name != "skip");
    }
  }
}
