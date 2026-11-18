// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Linq.Expressions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// The properties a collective spec assigns are read from its setters' expression tree without running it (#1045):
/// what a tag hook needs to turn a collective apply into a change notification.
/// </summary>
/// <docs>fundamentals/messages/message-tags#changed-properties</docs>
[Category("Unit")]
[Category("CollectiveEvents")]
public class CollectiveChangedPropertiesTests {
  [Test]
  public async Task Of_ReadsEverySetterInWrittenOrder_EachOnceAsync() {
    Expression<Action<ICollectiveSetters<JobModel>>> setters = s => s
      .SetProperty(j => j.Status, "Archived")
      .SetProperty(j => j.Count, j => j.Count + 1)
      .SetProperty(j => j.Status, "Again");

    await Assert.That(CollectiveChangedProperties.Of(setters)).IsEquivalentTo(["Status", "Count"]);
  }

  [Test]
  public async Task Of_NamesANestedMemberByItsPathAsync() {
    Expression<Action<ICollectiveSetters<JobModel>>> setters = s => s.SetProperty(j => j.Place.City, "Springfield");

    await Assert.That(CollectiveChangedProperties.Of(setters)).IsEquivalentTo(["Place.City"]);
  }

  [Test]
  public async Task Of_ReportsTheCollectionAnUpsertWritesAsync() {
    Expression<Action<ICollectiveSetters<JobModel>>> setters = s => s
      .UpsertElement(j => j.Tags, t => t.Key, new Tag("k", "v"))
      .SetProperty(j => (object)j.Count, 2);

    await Assert.That(CollectiveChangedProperties.Of(setters)).IsEquivalentTo(["Tags", "Count"]);
  }

  /// <summary>A setter whose selector is not a member of the model names nothing rather than guessing.</summary>
  [Test]
  public async Task Of_SkipsASelectorThatIsNotAMemberOfTheModelAsync() {
    var other = new JobModel();
    Expression<Action<ICollectiveSetters<JobModel>>> setters = s => s
      .SetProperty(_ => other.Status, "x")
      .SetProperty(j => j.Status.Length, 1);

    await Assert.That(CollectiveChangedProperties.Of(setters)).IsEquivalentTo(["Status.Length"]);
  }

  /// <summary>A selector handed over in a variable is not written in the tree, so it names nothing.</summary>
  [Test]
  public async Task Of_SkipsASelectorHeldInAVariableAsync() {
    Expression<Func<JobModel, string>> selector = j => j.Status;
    Expression<Action<ICollectiveSetters<JobModel>>> setters = s => s.SetProperty(selector, "x");

    await Assert.That(CollectiveChangedProperties.Of(setters)).IsEmpty();
  }

  [Test]
  public async Task Of_ANullExpression_ThrowsAsync() =>
    await Assert.That(() => CollectiveChangedProperties.Of(null!)).Throws<ArgumentNullException>();

  /// <summary>Any spec exposes its setters without its model type, for the code that holds it as an object.</summary>
  [Test]
  public async Task AnySpec_ExposesItsSettersUntypedAsync() {
    ICollectiveSpecSetters spec = new Spec(s => s.SetProperty(j => j.Status, "x"));

    await Assert.That(spec.UntypedSetters).IsSameReferenceAs(((ICollectiveSpec<JobModel>)spec).Setters);
  }

  private sealed record Tag(string Key, string Value);

  private sealed class Place {
    public string City { get; set; } = "";
  }

  private sealed class JobModel {
    public string Status { get; set; } = "";
    public int Count { get; }
    public Place Place { get; set; } = new();
    public List<Tag> Tags { get; set; } = [];
  }

  private sealed record Spec(Expression<Action<ICollectiveSetters<JobModel>>> Setters) : ICollectiveSpec<JobModel>;
}
