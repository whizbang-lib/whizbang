// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Linq.Expressions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Perspectives.Hooks;
using Whizbang.Data.EFCore.Postgres.Collective;

namespace Whizbang.Data.EFCore.Postgres.Tests.BranchCoverage.SecondPass;

/// <summary>
/// The collective setters rewriter's decisions in one class: hook setters with and without a value,
/// computed comparisons with each operator, and every selector shape it accepts or refuses (a property
/// of the model, a nested member, a field, a property declared on a base type, a method call). No
/// database.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/Collective/CollectiveSettersRewriter.cs</code-under-test>
[Category("Unit")]
[Category("CollectiveEvents")]
[Category("Shard2")]
public class CollectiveSettersShapeTests {

  // A hook setter with a value serializes that value as its own runtime type; one without serializes a
  // JSON null against the declared property type, and says it is null.
  [Test]
  public async Task FromHookSetters_WithAndWithoutAValue_SerializesEachAsync() {
    Expression<Func<ShapeModel, string>> name = m => m.Name;
    Expression<Func<ShapeModel, int>> count = m => m.Count;

    var assignments = CollectiveSettersRewriter.FromHookSetters([
      new SetPropertyOp(name, nameof(ShapeModel.Name), null, typeof(string)),
      new SetPropertyOp(count, nameof(ShapeModel.Count), 7, typeof(int)),
    ]);

    await Assert.That(assignments).Count().IsEqualTo(2);
    await Assert.That(assignments[0].JsonValue).IsEqualTo("null");
    await Assert.That(assignments[0].IsNull).IsTrue();
    await Assert.That(assignments[1].JsonValue).IsEqualTo("7");
    await Assert.That(assignments[1].IsNull).IsFalse();
  }

  // A computed comparison carries its operator: == compiles to = and != to <>.
  [Test]
  public async Task ComputedComparison_EachOperator_CarriesItsSqlOperatorAsync() {
    Expression<Action<ICollectiveSetters<ShapeModel>>> equal = s => s.SetProperty(j => j.IsActive, j => j.Count == 5);
    Expression<Action<ICollectiveSetters<ShapeModel>>> notEqual = s => s.SetProperty(j => j.IsActive, j => j.Count != 5);

    var equalAssignment = CollectiveSettersRewriter.CollectAssignments(equal).Single();
    var notEqualAssignment = CollectiveSettersRewriter.CollectAssignments(notEqual).Single();

    await Assert.That(equalAssignment.Comparison!.SqlOperator).IsEqualTo("=");
    await Assert.That(notEqualAssignment.Comparison!.SqlOperator).IsEqualTo("<>");
  }

  // Only a property the model itself declares, read straight off the parameter, is a settable path.
  // Every other selector shape is refused by name rather than written somewhere unintended.
  [Test]
  public async Task SelectorShapes_OnlyAModelPropertyIsAcceptedAsync() {
    Expression<Action<ICollectiveSetters<ShapeModel>>> property = s => s.SetProperty(j => j.Name, "set");
    Expression<Action<ICollectiveSetters<ShapeModel>>> nested = s => s.SetProperty(j => j.Name.Length, 3);
    Expression<Action<ICollectiveSetters<ShapeModel>>> field = s => s.SetProperty(j => j.Marker, 1);
    Expression<Action<ICollectiveSetters<ShapeModel>>> inherited = s => s.SetProperty(j => j.Inherited, "base");
    Expression<Action<ICollectiveSetters<ShapeModel>>> method = s => s.SetProperty(j => j.Name.Trim(), "x");

    var accepted = CollectiveSettersRewriter.CollectAssignments(property).Single();

    await Assert.That(accepted.PathName).IsEqualTo(nameof(ShapeModel.Name));
    await Assert.That(() => CollectiveSettersRewriter.CollectAssignments(nested)).Throws<NotSupportedException>();
    await Assert.That(() => CollectiveSettersRewriter.CollectAssignments(field)).Throws<NotSupportedException>();
    await Assert.That(() => CollectiveSettersRewriter.CollectAssignments(inherited)).Throws<NotSupportedException>()
      .Because("a property declared on a base type is not one of the model's own document members");
    await Assert.That(() => CollectiveSettersRewriter.CollectAssignments(method)).Throws<NotSupportedException>();
  }

  private class ShapeBase {
    public string Inherited { get; set; } = string.Empty;
  }

  private sealed class ShapeModel : ShapeBase {
#pragma warning disable S1104 // A public field is the selector shape under test.
    public int Marker = 1;
#pragma warning restore S1104
    public string Name { get; set; } = string.Empty;
    public int Count { get; set; }
    public bool IsActive { get; set; }
  }
}
