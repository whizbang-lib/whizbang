#pragma warning disable CA1707

using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;
using Whizbang.Data.Dapper.Postgres.Collective;

namespace Whizbang.Data.Dapper.Postgres.Tests.Collective;

/// <summary>
/// Locks the Dapper SQL compiler for collective-event SET clauses. The
/// compiler translates the perspective's <see cref="ICollectiveSpec{TModel}"/>
/// LINQ into a <c>jsonb_set</c> chain over the <c>data</c> column. Real
/// Postgres integration (executing the compiled SQL against a
/// Testcontainer) is the natural Slice 9 follow-up — these tests pin
/// the SQL string shape and parameter dictionary so behavior is locked
/// before the runner consumes it.
/// </summary>
/// <docs>fundamentals/messaging/collective-events</docs>
[Category("Unit")]
[Category("CollectiveEvents")]
public class DapperCollectiveSpecCompilerTests {

  private static readonly JsonSerializerOptions _jsonOptions = new() {
    PropertyNamingPolicy = null, // preserve PascalCase property names — they're the jsonb path
  };

  // ── Single SetProperty ─────────────────────────────────────────────────

  [Test]
  public async Task Compile_ConstantString_EmitsJsonbSetWithQuotedValueAsync() {
    var spec = _spec(s => s.SetProperty(j => j.Status, "Archived"));

    var compiled = DapperCollectiveSpecCompiler<_jobModel>.Compile(spec, _jsonOptions);

    await Assert.That(compiled.SqlFragment).Contains("jsonb_set(data, '{Status}'")
      .Because("Top-level property selector must navigate to '{Status}' inside the data jsonb column.");
    await Assert.That(compiled.SqlFragment).Contains("::jsonb")
      .Because("jsonb_set requires the new value to be jsonb-typed; the cast is part of the canonical shape.");
    await Assert.That(compiled.Parameters).Count().IsEqualTo(1);
    await Assert.That(compiled.Parameters.Values.Single()).IsEqualTo("\"Archived\"")
      .Because("JsonSerializer-string-encoded value: the bind value is the literal JSON token '\"Archived\"', not the raw string Archived.");
  }

  [Test]
  public async Task Compile_ConstantInt_EmitsJsonbSetWithNumericLiteralAsync() {
    var spec = _spec(s => s.SetProperty(j => j.ViewCount, 42));

    var compiled = DapperCollectiveSpecCompiler<_jobModel>.Compile(spec, _jsonOptions);

    await Assert.That(compiled.SqlFragment).Contains("'{ViewCount}'");
    await Assert.That(compiled.Parameters.Values.Single()).IsEqualTo("42")
      .Because("Numeric values serialize to the JSON literal '42', not quoted.");
  }

  // ── Chained SetProperty (multiple props) ───────────────────────────────

  [Test]
  public async Task Compile_TwoChainedSetProperty_EmitsNestedJsonbSetAsync() {
    var spec = _spec(s => s
      .SetProperty(j => j.Status, "Archived")
      .SetProperty(j => j.ViewCount, 0));

    var compiled = DapperCollectiveSpecCompiler<_jobModel>.Compile(spec, _jsonOptions);

    // The fragment should contain TWO jsonb_set calls — outer is the
    // second-emitted (chained) call, inner is the first.
    var openCount = compiled.SqlFragment.Split("jsonb_set(", StringSplitOptions.None).Length - 1;
    await Assert.That(openCount).IsEqualTo(2)
      .Because("Two SetProperty calls => two nested jsonb_set invocations on data.");
    await Assert.That(compiled.SqlFragment).Contains("'{Status}'");
    await Assert.That(compiled.SqlFragment).Contains("'{ViewCount}'");
    await Assert.That(compiled.Parameters).Count().IsEqualTo(2);
  }

  // ── Defensive errors ───────────────────────────────────────────────────

  [Test]
  public async Task Compile_EmptySpec_ThrowsInvalidOperationAsync() {
    // Statement-body lambdas can't convert to Expression trees, so build
    // an empty Expression manually (a no-op Action lambda body).
    var sParam = Expression.Parameter(typeof(ICollectiveSetters<_jobModel>), "s");
    var empty = Expression.Lambda<Action<ICollectiveSetters<_jobModel>>>(
      Expression.Empty(), sParam);
    var spec = new _stubSpec(empty);

    await Assert.That(() => DapperCollectiveSpecCompiler<_jobModel>.Compile(spec, _jsonOptions))
      .ThrowsExactly<InvalidOperationException>()
      .Because("A spec that mutates zero properties translates to a SQL UPDATE with no SET clause — that's a malformed handler, not a domain condition.");
  }

  [Test]
  public async Task Compile_ComputedArithmeticExpression_ThrowsNotSupportedAsync() {
    Expression<Action<ICollectiveSetters<_jobModel>>> body =
      s => s.SetProperty(j => j.ViewCount, j => j.ViewCount + 1);
    var spec = new _stubSpec(body);

    await Assert.That(() => DapperCollectiveSpecCompiler<_jobModel>.Compile(spec, _jsonOptions))
      .ThrowsExactly<NotSupportedException>()
      .Because("Computed arithmetic (j => j.ViewCount + 1) is still RawSql-only — only property-vs-constant comparisons are compiled.");
  }

  // ── Computed comparison (property vs constant => bool) ──────────────────

  [Test]
  public async Task Compile_ComputedPropertyEqualsConstant_EmitsToJsonbComparisonAsync() {
    var target = Guid.NewGuid();
    var spec = _spec(s => s.SetProperty(j => j.IsActive, j => j.Id == target));

    var compiled = DapperCollectiveSpecCompiler<_jobModel>.Compile(spec, _jsonOptions);

    await Assert.That(compiled.SqlFragment).Contains("jsonb_set(data, '{IsActive}'")
      .Because("The target property is still a top-level jsonb path.");
    await Assert.That(compiled.SqlFragment).Contains("to_jsonb(")
      .Because("A computed boolean is wrapped in to_jsonb so the result is a jsonb value.");
    await Assert.That(compiled.SqlFragment).Contains("data->'Id'")
      .Because("The compared property is read from the data jsonb column as a jsonb value.");
    await Assert.That(compiled.SqlFragment).Contains("::jsonb =")
      .Because("Equality is a jsonb-to-jsonb comparison (type-agnostic), not a text compare.");
    await Assert.That(compiled.Parameters).Count().IsEqualTo(1);
    await Assert.That(compiled.Parameters.Values.Single()).IsEqualTo($"\"{target}\"")
      .Because("The RHS constant is JSON-serialized the same way the column stores it (a Guid as a JSON string).");
  }

  [Test]
  public async Task Compile_ComputedPropertyNotEqualsConstant_EmitsInequalityAsync() {
    var target = Guid.NewGuid();
    var spec = _spec(s => s.SetProperty(j => j.IsActive, j => j.Id != target));

    var compiled = DapperCollectiveSpecCompiler<_jobModel>.Compile(spec, _jsonOptions);

    await Assert.That(compiled.SqlFragment).Contains("<>")
      .Because("!= compiles to the SQL inequality operator.");
    await Assert.That(compiled.Parameters).Count().IsEqualTo(1);
  }

  [Test]
  public async Task Compile_NestedPropertyPath_ThrowsNotSupportedAsync() {
    Expression<Action<ICollectiveSetters<_complexModel>>> body =
      s => s.SetProperty(j => j.Nested.Inner, "v");
    var spec = new _stubSpecComplex(body);

    await Assert.That(() => DapperCollectiveSpecCompiler<_complexModel>.Compile(spec, _jsonOptions))
      .ThrowsExactly<NotSupportedException>()
      .Because("Nested paths require multi-level jsonb_set composition — out of scope for the first cut.");
  }

  [Test]
  public async Task Compile_NullSpec_ThrowsArgumentNullAsync() {
    await Assert.That(() => DapperCollectiveSpecCompiler<_jobModel>.Compile(null!, _jsonOptions))
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task Compile_NullJsonOptions_ThrowsArgumentNullAsync() {
    var spec = _spec(s => s.SetProperty(j => j.Status, "X"));
    await Assert.That(() => DapperCollectiveSpecCompiler<_jobModel>.Compile(spec, null!))
      .ThrowsExactly<ArgumentNullException>();
  }

  // ── Captured-local value sources ──────────────────────────────────────

  [Test]
  public async Task Compile_CapturedLocal_ResolvedAtCompileTimeAsync() {
    var statusValue = "Pending";
    var spec = _spec(s => s.SetProperty(j => j.Status, statusValue));

    var compiled = DapperCollectiveSpecCompiler<_jobModel>.Compile(spec, _jsonOptions);

    await Assert.That(compiled.Parameters.Values.Single()).IsEqualTo("\"Pending\"")
      .Because("Captured-local value bound at compile time, JSON-serialized into the parameter dictionary.");
  }

  // ── Setter calls that aren't SetProperty ────────────────────────────────

  [Test]
  public async Task Compile_SetterCallsObjectInheritedMethod_ThrowsInvalidOperationAsync() {
    // A spec author could accidentally call an object-inherited method (e.g. ToString()) on
    // the setter instead of SetProperty. If the visitor mistook that call for a setter
    // mutation it could crash or silently drop the property; instead it must fall through to
    // the base visitor and be caught by the same "zero SetProperty calls" guard as a truly
    // empty spec, rather than something more exotic.
    Expression<Action<ICollectiveSetters<_jobModel>>> body = s => s.ToString();
    var spec = new _stubSpec(body);

    await Assert.That(() => DapperCollectiveSpecCompiler<_jobModel>.Compile(spec, _jsonOptions))
      .ThrowsExactly<InvalidOperationException>()
      .Because("A method call on the setter that isn't SetProperty (here, an object-inherited method) must fall through to the base visitor and surface as the standard empty-spec error, not a crash deeper in property extraction.");
  }

  // ── Manually constructed selector arguments (unreachable via C# call syntax) ───────────

  [Test]
  public async Task Compile_NonLambdaSelectorArgument_ThrowsInvalidOperationAsync() {
    // Ordinary C# call syntax can never produce a SetProperty selector argument that isn't a
    // lambda — the compiler only ever emits a direct LambdaExpression or a Quote-wrapped one.
    // This guards against a broken spec-building tool that assembles the call tree by hand and
    // substitutes something else into the selector slot: the compiler must fail loudly naming
    // the unexpected node kind, not misread garbage as a property path.
    var setPropertyMethod = _constantSetPropertyMethod(typeof(int));
    var sParam = Expression.Parameter(typeof(ICollectiveSetters<_jobModel>), "s");
    // A ConstantExpression whose declared Type is set to the selector's expected
    // Expression<Func<...>> type satisfies Expression.Call's parameter-type check directly
    // (no quoting involved), so it lands in Arguments[0] exactly as a ConstantExpression —
    // a node kind _unwrapLambda's switch has no named arm for, only its default throw arm.
    var notASelector = Expression.Constant(null, typeof(Expression<Func<_jobModel, int>>));
    var call = Expression.Call(sParam, setPropertyMethod, notASelector, Expression.Constant(42));
    var body = Expression.Lambda<Action<ICollectiveSetters<_jobModel>>>(call, sParam);
    var spec = new _stubSpec(body);

    await Assert.That(() => DapperCollectiveSpecCompiler<_jobModel>.Compile(spec, _jsonOptions))
      .ThrowsExactly<InvalidOperationException>()
      .WithMessageContaining("Constant")
      .Because("The selector argument's actual node kind (Constant, not a lambda) belongs in the message so a broken spec-building tool can be diagnosed without stepping through the compiler.");
  }

  [Test]
  public async Task Compile_BoxedIntPropertySelector_EmitsJsonbSetForUnderlyingPropertyAsync() {
    // Forcing TProp to object via an explicit type argument makes the compiler box the int
    // property access in a Convert node before the selector reaches property extraction.
    // Nothing in ordinary spec-writing needs this, but generic spec-building helpers can
    // produce it — if the Convert wasn't stripped, an otherwise-ordinary scalar selector would
    // be misclassified as an unsupported computed/nested selector.
    Expression<Action<ICollectiveSetters<_jobModel>>> body =
      s => s.SetProperty<object>(m => m.ViewCount, (object)42);
    var spec = new _stubSpec(body);

    var compiled = DapperCollectiveSpecCompiler<_jobModel>.Compile(spec, _jsonOptions);

    await Assert.That(compiled.SqlFragment).Contains("'{ViewCount}'")
      .Because("The boxed selector must still resolve to the underlying int property's jsonb path.");
    await Assert.That(compiled.Parameters.Values.Single()).IsEqualTo("42")
      .Because("The boxed value must still serialize as the plain numeric JSON literal, not an object wrapper.");
  }

  [Test]
  public async Task Compile_ComputedComparisonWithWidenedNumericProperty_EmitsToJsonbComparisonAsync() {
    // Comparing an int property against a long literal forces the compiler to widen the
    // property side with an implicit numeric-conversion Convert node before the comparison —
    // an ordinary consequence of C# numeric promotion, not a different spec shape. If that
    // Convert wasn't stripped before reading the compared property's name, an everyday
    // cross-width comparison would be rejected as unsupported instead of compiling the same
    // as a same-width one.
    var spec = _spec(s => s.SetProperty(j => j.IsActive, j => j.ViewCount == 100L));

    var compiled = DapperCollectiveSpecCompiler<_jobModel>.Compile(spec, _jsonOptions);

    await Assert.That(compiled.SqlFragment).Contains("jsonb_set(data, '{IsActive}'")
      .Because("The target property is still a top-level jsonb path.");
    await Assert.That(compiled.SqlFragment).Contains("data->'ViewCount'")
      .Because("The compared property's name must resolve correctly despite the compiler-inserted widening Convert on that side.");
    await Assert.That(compiled.SqlFragment).Contains("to_jsonb(")
      .Because("A computed boolean comparison is still wrapped in to_jsonb regardless of which side needed widening.");
    await Assert.That(compiled.Parameters).Count().IsEqualTo(1);
    await Assert.That(compiled.Parameters.Values.Single()).IsEqualTo("100")
      .Because("The long literal serializes as a plain numeric JSON literal on the right-hand side.");
  }

  [Test]
  public async Task Compile_ConstantValueFromMethodCall_ThrowsNotSupportedAsync() {
    // A value argument that's a method call is neither a constant literal, a captured local,
    // nor a captured field — resolving it would mean re-executing arbitrary code every time the
    // compiled SQL runs against a different row. The compiler must reject it loudly and name the
    // unsupported node kind, pointing the author at RawSql instead of silently binding a stale
    // or null parameter.
    Expression<Action<ICollectiveSetters<_jobModel>>> body =
      s => s.SetProperty(m => m.ViewCount, _computeValue());
    var spec = new _stubSpec(body);

    await Assert.That(() => DapperCollectiveSpecCompiler<_jobModel>.Compile(spec, _jsonOptions))
      .ThrowsExactly<NotSupportedException>()
      .WithMessageContaining("Call")
      .Because("The message should name the unsupported node kind (a method Call) so the author knows what part of the value expression is unsupported, not just that something failed.");
  }

  // ── Inline test types ──────────────────────────────────────────────────

  private sealed class _jobModel {
    public string Status { get; set; } = string.Empty;
    public int ViewCount { get; set; }
    public Guid Id { get; set; }
    public bool IsActive { get; set; }
  }

  private sealed class _complexModel {
    public _nested Nested { get; set; } = new();
  }
  private sealed class _nested {
    public string Inner { get; set; } = string.Empty;
  }

#pragma warning disable CA1859 // analyzer suggestion ignored — tests assert against the interface, not the concrete record
  private static ICollectiveSpec<_jobModel> _spec(Expression<Action<ICollectiveSetters<_jobModel>>> expr)
    => new _stubSpec(expr);
#pragma warning restore CA1859

  private sealed class _stubSpec(Expression<Action<ICollectiveSetters<_jobModel>>> setters) : ICollectiveSpec<_jobModel> {
    public Expression<Action<ICollectiveSetters<_jobModel>>> Setters { get; } = setters;
  }

  private sealed class _stubSpecComplex(Expression<Action<ICollectiveSetters<_complexModel>>> setters) : ICollectiveSpec<_complexModel> {
    public Expression<Action<ICollectiveSetters<_complexModel>>> Setters { get; } = setters;
  }

  // Closed MethodInfo for the constant-value SetProperty<TProp> overload (as opposed to the
  // computed-value overload) — distinguished by its second parameter being TProp itself rather
  // than Expression<Func<TModel, TProp>>. Manually built expression trees need the exact
  // MethodInfo to construct a MethodCallExpression by hand.
  private static MethodInfo _constantSetPropertyMethod(Type propertyType) =>
    typeof(ICollectiveSetters<_jobModel>).GetMethods()
      .Single(m => m.Name == nameof(ICollectiveSetters<_jobModel>.SetProperty) && m.GetParameters()[1].ParameterType.IsGenericParameter)
      .MakeGenericMethod(propertyType);

  // A non-constant value source (a plain method call) for Compile_ConstantValueFromMethodCall_ThrowsNotSupportedAsync.
  private static int _computeValue() => 42;
}
