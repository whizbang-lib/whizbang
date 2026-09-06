using Whizbang.Core.Perspectives.Hooks;

namespace Whizbang.Core.Tests.Perspectives.Hooks;

/// <summary>
/// Coverage for <see cref="ApplyHookSelector.PropertyName"/>, reached through
/// <see cref="ApplyHookBuilder{TMarker}.SetProperty{TProp}"/>: stripping the compiler's boxing
/// <c>Convert</c> around a value-type property selector, and rejecting a selector that isn't a
/// top-level property access.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Perspectives/Hooks/ApplyHookBuilders.cs</code-under-test>
public class ApplyHookBuildersCoverageTests {
  private sealed class _marker {
    public int Count { get; set; }
    public string GetLabel() => "label";
  }

  // If the boxing Convert the compiler inserts around `m => m.Count` (boxed to object) weren't
  // stripped, every apply-hook call that sets a value-type property through an object-typed
  // selector would record the wrong (or no) property name, silently breaking that column's
  // perspective row updates.
  [Test]
  public async Task SetProperty_BoxedValueTypeSelector_StripsConvertAndResolvesPropertyNameAsync() {
    var builder = new ApplyHookBuilder<_marker>();

    builder.SetProperty<object>(m => m.Count, 5);

    var op = builder.Ops.OfType<SetPropertyOp>().Single();
    await Assert.That(op.PropertyName).IsEqualTo(nameof(_marker.Count))
      .Because("stripping the boxing Convert must still resolve to the underlying member name");
  }

  // If a non-property selector (a method call, here) were silently accepted instead of rejected,
  // the recorded ApplyHookOp would carry a wrong or missing column name instead of failing loudly
  // at hook-configuration time — corrupting the compiled UPDATE at apply time instead of at setup.
  [Test]
  public async Task SetProperty_NonMemberSelector_ThrowsNotSupportedExceptionAsync() {
    var builder = new ApplyHookBuilder<_marker>();

    await Assert.That(() => builder.SetProperty(m => m.GetLabel(), "y"))
      .Throws<NotSupportedException>()
      .WithMessageContaining("top-level property selector");
  }
}
