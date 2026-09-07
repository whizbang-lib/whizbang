using System.Linq.Expressions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;
using Whizbang.Data.Postgres.Collective;

namespace Whizbang.Data.EFCore.Postgres.Tests.Collective;

/// <summary>
/// Coverage for the property-selector unwrapping in <see cref="CollectiveInMemoryEvaluator{TModel}"/>'s
/// private property-resolution helper — the boxing-<c>Convert</c> unwrap loop and the
/// unsupported-selector-shape guard that the sibling <see cref="CollectiveInMemoryEvaluatorTests"/>
/// suite never reaches (every selector there is a direct top-level property access with no implicit
/// conversion). Pure logic, no DB.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Collective/CollectiveInMemoryEvaluator.cs</code-under-test>
[Category("Shard1")]
public class CollectiveInMemoryEvaluatorCoverageTests {

  private sealed class _model {
    public Guid Id { get; set; }
    public bool IsActive { get; set; }
    public string Name { get; set; } = string.Empty;
  }

  private sealed record _spec(Expression<Action<ICollectiveSetters<_model>>> Setters) : ICollectiveSpec<_model>;

  // A replay/rebuild fold re-applies this same spec in memory to keep a rebuilt row consistent
  // with the live SQL apply path. If the boxing-Convert selector shape stopped unwrapping to the
  // underlying property, the rebuilt row would silently keep its stale value while the live path
  // (which never runs this reflection code) moved on — a rebuilt perspective would permanently
  // diverge from the live one with no error raised anywhere.
  [Test]
  public async Task Apply_SelectorWithBoxingConvert_UnwrapsToTheUnderlyingPropertyAsync() {
    var spec = new _spec(Setters: s => s.SetProperty<object>(o => o.IsActive, true));
    var model = new _model { IsActive = false };

    CollectiveInMemoryEvaluator<_model>.Apply(spec, model);

    await Assert.That(model.IsActive).IsTrue()
      .Because("the Convert(box) node the compiler inserts for a bool selector bound to SetProperty<object> must be unwrapped to reach the real IsActive property");
  }

  // Selectors reaching this evaluator are restricted at compile time (WHIZ106) to direct
  // top-level property access. If this guard stopped throwing for a nested/computed shape, a
  // malformed spec would silently target the wrong reflection member — or fail with a confusing
  // cast exception deep inside Flush — instead of failing clearly at the point of misuse.
  [Test]
  public async Task Apply_SelectorNotADirectTopLevelProperty_ThrowsNotSupportedAsync() {
    var spec = new _spec(Setters: s => s.SetProperty(o => o.Name.Length, 5));
    var model = new _model { Name = "abc" };

    await Assert.That(() => CollectiveInMemoryEvaluator<_model>.Apply(spec, model))
      .Throws<NotSupportedException>()
      .Because("o.Name.Length is a member access whose Expression is another MemberExpression, not the row parameter — not replayable in-memory");
  }
}
