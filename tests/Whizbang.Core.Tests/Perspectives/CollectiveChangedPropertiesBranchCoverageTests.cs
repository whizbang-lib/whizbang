// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Linq.Expressions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// Branch backfill for <see cref="CollectiveChangedProperties"/>: a call that shares a setter's name
/// but takes no arguments is not a setter and names nothing, and a call with any other name names
/// nothing even when its first argument is a member selector.
/// </summary>
[Category("Unit")]
[Category("CollectiveEvents")]
public class CollectiveChangedPropertiesBranchCoverageTests {

  [Test]
  public async Task Of_AZeroArgumentCallNamedLikeASetter_IsSkippedAsync() {
    // The value argument is itself a call named SetProperty, with no selector argument to read.
    Expression<Action<ICollectiveSetters<JobModel>>> setters = s => s.SetProperty(j => j.Status, Values.SetProperty());

    await Assert.That(CollectiveChangedProperties.Of(setters)).IsEquivalentTo(["Status"])
      .Because("only the real setter's selector is a property path; a same-named call with no arguments has none to read");
  }

  [Test]
  public async Task Of_AValueComputedByAnotherMethodTakingASelector_NamesOnlyTheSetterAsync() {
    // The value argument is a call to a method that is neither setter, and its first argument is a
    // selector naming a real model member. Only the setter's selector is an assignment.
    Expression<Action<ICollectiveSetters<JobModel>>> setters = s => s.SetProperty(j => j.Status, Values.Describe(j => j.Count));

    await Assert.That(CollectiveChangedProperties.Of(setters)).IsEquivalentTo(["Status"])
      .Because("a call that is not SetProperty or UpsertElement assigns nothing, even when it is handed a member selector");
  }

  private static class Values {
#pragma warning disable S3400 // The method NAME is the case under test: a same-named call with no arguments.
    public static string SetProperty() => "value";
#pragma warning restore S3400

    public static string Describe(Expression<Func<JobModel, int>> selector) => selector.ToString();
  }

  private sealed class JobModel {
    public string Status { get; set; } = "";
    public int Count { get; }
  }
}
