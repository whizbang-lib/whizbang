using System.Diagnostics.CodeAnalysis;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Coverage-focused tests for <c>CollectiveApplyDiscoveryGenerator</c> targeting the handler-shape
/// guards in <c>_extract</c>: a coincidentally-attributed method, a return type whose simple name
/// matches <c>ICollectiveSpec</c> but lives in the wrong namespace, a return type with the wrong
/// generic arity, a zero/too-many parameter method, a first parameter that doesn't implement
/// <c>ICollectiveEvent</c>, and a second parameter that doesn't implement <c>ICollectiveQuery</c>.
/// Complements CollectiveApplyDiscoveryGeneratorTests.cs.
/// </summary>
/// <tests>src/Whizbang.Generators/CollectiveApplyDiscoveryGenerator.cs</tests>
[Category("Generators")]
[Category("CollectiveEvents")]
public class CollectiveApplyDiscoveryGeneratorCoverageTests {

  /// <summary>
  /// Source-code stub of the collective-events surface — the generator runs against test source
  /// plus this stub so the <c>using</c> / <c>[CollectiveApplyFor]</c> references resolve.
  /// </summary>
  private const string COLLECTIVE_STUBS = """
        namespace Whizbang.Core.Messaging {
          public interface ICollectiveScope { string ScopeKind { get; } }
          public interface ICollectiveEvent {
            ICollectiveScope Scope { get; }
            System.Collections.Generic.IReadOnlyList<System.Guid> MatchedStreamIds { get; }
          }
        }

        namespace Whizbang.Core.Perspectives {
          using Whizbang.Core.Messaging;
          public interface ICollectiveSetters<TModel> where TModel : class { }
          public interface ICollectiveQuery { }
          public interface ICollectiveSpec<TModel> where TModel : class {
            System.Linq.Expressions.Expression<System.Action<ICollectiveSetters<TModel>>> Setters { get; }
          }

          public enum CollectiveScopeHandling { Framework = 0, Custom = 1 }
          public enum CollectiveSpecKind { Linq = 0, RawSql = 1 }

          [System.AttributeUsage(System.AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
          public sealed class CollectiveApplyForAttribute : System.Attribute {
            public CollectiveScopeHandling ScopeHandling { get; init; } = CollectiveScopeHandling.Framework;
            public CollectiveSpecKind SpecKind { get; init; } = CollectiveSpecKind.Linq;
            public int BatchSize { get; init; }
            public int StatementTimeoutSeconds { get; init; }
          }
        }
        """;

  // ── Method carries an attribute, but not [CollectiveApplyFor] ──────────

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_MethodWithUnrelatedAttribute_NotDiscoveredAsHandlerAsync() {
    // The syntactic predicate only checks AttributeLists.Count > 0, so a method decorated with any
    // attribute (here [Obsolete]) reaches semantic analysis. If the attribute-name lookup were
    // broken, a coincidentally-attributed method could be mistaken for a collective handler and
    // silently registered against the projection runner's dispatch table.
    const string source = $$"""
            using Whizbang.Core.Messaging;
            using Whizbang.Core.Perspectives;

            namespace TestApp;

            public sealed class JobModel { }

            public sealed record TouchEvent(
              ICollectiveScope Scope,
              System.Collections.Generic.IReadOnlyList<System.Guid> MatchedStreamIds) : ICollectiveEvent;

            public sealed class JobPerspective {
              [System.Obsolete]
              public ICollectiveSpec<JobModel> NotAHandler(TouchEvent e) => null!;

              [CollectiveApplyFor]
              public ICollectiveSpec<JobModel> RealHandler(TouchEvent e) => null!;
            }

            {{COLLECTIVE_STUBS}}
            """;

    var result = GeneratorTestHelper.RunGenerator<CollectiveApplyDiscoveryGenerator>(source);
    var code = GeneratorTestHelper.GetGeneratedSource(result, "CollectiveApplyRegistry.g.cs");

    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("RealHandler")
      .Because("The genuinely attributed handler must still be discovered.");
    await Assert.That(code).DoesNotContain("NotAHandler")
      .Because("A method whose only attribute is unrelated to [CollectiveApplyFor] must not be treated as a collective handler.");
  }

  // ── Return type's simple name matches but the namespace doesn't ────────

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_HandlerReturnTypeInWrongNamespace_NotDiscoveredAsync() {
    // A decoy type with the same simple name ("ICollectiveSpec") but a different namespace must
    // not satisfy the return-type shape check. If the generator matched on bare name alone, any
    // unrelated "ICollectiveSpec<T>" defined elsewhere in a large codebase could be mistaken for
    // the real contract and silently registered with a return value the runner cannot use.
    const string source = $$"""
            using Whizbang.Core.Messaging;
            using Whizbang.Core.Perspectives;

            namespace Decoy {
              public interface ICollectiveSpec<TModel> where TModel : class { }
            }

            namespace TestApp {
              public sealed class JobModel { }

              public sealed record TouchEvent(
                ICollectiveScope Scope,
                System.Collections.Generic.IReadOnlyList<System.Guid> MatchedStreamIds) : ICollectiveEvent;

              public sealed class JobPerspective {
                [CollectiveApplyFor]
                public Decoy.ICollectiveSpec<JobModel> WrongNamespaceHandler(TouchEvent e) => null!;

                [CollectiveApplyFor]
                public ICollectiveSpec<JobModel> RealHandler(TouchEvent e) => null!;
              }
            }

            {{COLLECTIVE_STUBS}}
            """;

    var result = GeneratorTestHelper.RunGenerator<CollectiveApplyDiscoveryGenerator>(source);
    var code = GeneratorTestHelper.GetGeneratedSource(result, "CollectiveApplyRegistry.g.cs");

    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("RealHandler")
      .Because("The handler returning the real, correctly-namespaced ICollectiveSpec<T> must be discovered.");
    await Assert.That(code).DoesNotContain("WrongNamespaceHandler")
      .Because("A same-named ICollectiveSpec<T> from the wrong namespace must be rejected — bare name matching is not enough.");
  }

  // ── Return type has the right name/namespace but the wrong arity ───────

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_HandlerReturnTypeWithWrongArity_NotDiscoveredAsync() {
    // INamedTypeSymbol.Name ignores arity, so an "ICollectiveSpec<TModel1, TModel2>" overload in
    // the very same namespace passes the name+namespace check and only fails on TypeArguments.Length.
    // Without that check, a 2-arity overload could be accepted and its (wrong) single "model type"
    // extraction would silently point the projection runner at the wrong model.
    const string source = $$"""
            using Whizbang.Core.Messaging;
            using Whizbang.Core.Perspectives;

            namespace Whizbang.Core.Perspectives {
              public interface ICollectiveSpec<TModel1, TModel2> where TModel1 : class where TModel2 : class { }
            }

            namespace TestApp {
              public sealed class JobModel { }

              public sealed record TouchEvent(
                ICollectiveScope Scope,
                System.Collections.Generic.IReadOnlyList<System.Guid> MatchedStreamIds) : ICollectiveEvent;

              public sealed class JobPerspective {
                [CollectiveApplyFor]
                public ICollectiveSpec<JobModel, JobModel> WrongArityHandler(TouchEvent e) => null!;

                [CollectiveApplyFor]
                public ICollectiveSpec<JobModel> RealHandler(TouchEvent e) => null!;
              }
            }

            {{COLLECTIVE_STUBS}}
            """;

    var result = GeneratorTestHelper.RunGenerator<CollectiveApplyDiscoveryGenerator>(source);
    var code = GeneratorTestHelper.GetGeneratedSource(result, "CollectiveApplyRegistry.g.cs");

    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("RealHandler")
      .Because("The 1-arity ICollectiveSpec<TModel> handler must still be discovered.");
    await Assert.That(code).DoesNotContain("WrongArityHandler")
      .Because("A same-name, same-namespace ICollectiveSpec overload with the wrong number of type arguments must be rejected.");
  }

  // ── Zero or too many parameters ─────────────────────────────────────────

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_HandlerWithZeroOrTooManyParameters_NotDiscoveredAsync() {
    // A handler must take exactly one parameter (the event) or two (event + ICollectiveQuery
    // context). A zero-parameter method has no event to dispatch on; a three-parameter method
    // has no defined slot for the extra argument. Either shape must be rejected rather than
    // crashing on Parameters[0] or emitting an invoker with an unexplained extra argument.
    const string source = $$"""
            using Whizbang.Core.Messaging;
            using Whizbang.Core.Perspectives;

            namespace TestApp;

            public sealed class JobModel { }

            public sealed record TouchEvent(
              ICollectiveScope Scope,
              System.Collections.Generic.IReadOnlyList<System.Guid> MatchedStreamIds) : ICollectiveEvent;

            public sealed class JobPerspective {
              [CollectiveApplyFor]
              public ICollectiveSpec<JobModel> ZeroParamHandler() => null!;

              [CollectiveApplyFor]
              public ICollectiveSpec<JobModel> TooManyParamsHandler(TouchEvent e, ICollectiveQuery q, int extra) => null!;

              [CollectiveApplyFor]
              public ICollectiveSpec<JobModel> RealHandler(TouchEvent e) => null!;
            }

            {{COLLECTIVE_STUBS}}
            """;

    var result = GeneratorTestHelper.RunGenerator<CollectiveApplyDiscoveryGenerator>(source);
    var code = GeneratorTestHelper.GetGeneratedSource(result, "CollectiveApplyRegistry.g.cs");

    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("RealHandler")
      .Because("A correctly-shaped single-parameter handler must still be discovered.");
    await Assert.That(code).DoesNotContain("ZeroParamHandler")
      .Because("A handler with no parameters has no event to dispatch on and must be rejected.");
    await Assert.That(code).DoesNotContain("TooManyParamsHandler")
      .Because("A handler with more than the event + query parameters has no defined slot for the extra argument and must be rejected.");
  }

  // ── First parameter doesn't implement ICollectiveEvent ──────────────────

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_HandlerFirstParameterNotCollectiveEvent_NotDiscoveredAsync() {
    // The first parameter must implement ICollectiveEvent — it carries the Scope/MatchedStreamIds
    // the projection runner needs to resolve which rows to apply against. A handler taking some
    // unrelated type as its first parameter must be rejected rather than emitting an invoker whose
    // cast to ICollectiveEvent-derived logic would be meaningless.
    const string source = $$"""
            using Whizbang.Core.Messaging;
            using Whizbang.Core.Perspectives;

            namespace TestApp;

            public sealed class JobModel { }

            public sealed record TouchEvent(
              ICollectiveScope Scope,
              System.Collections.Generic.IReadOnlyList<System.Guid> MatchedStreamIds) : ICollectiveEvent;

            public sealed class JobPerspective {
              [CollectiveApplyFor]
              public ICollectiveSpec<JobModel> NonEventParamHandler(int notAnEvent) => null!;

              [CollectiveApplyFor]
              public ICollectiveSpec<JobModel> RealHandler(TouchEvent e) => null!;
            }

            {{COLLECTIVE_STUBS}}
            """;

    var result = GeneratorTestHelper.RunGenerator<CollectiveApplyDiscoveryGenerator>(source);
    var code = GeneratorTestHelper.GetGeneratedSource(result, "CollectiveApplyRegistry.g.cs");

    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("RealHandler")
      .Because("A handler whose first parameter implements ICollectiveEvent must be discovered.");
    await Assert.That(code).DoesNotContain("NonEventParamHandler")
      .Because("A handler whose first parameter does not implement ICollectiveEvent must be rejected.");
  }

  // ── Second parameter present but doesn't implement ICollectiveQuery ─────

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_QueryParameterNotImplementingICollectiveQuery_NotDiscoveredAsync() {
    // A two-parameter handler's second parameter must be (or implement) ICollectiveQuery — the
    // scoped-perspective query context threaded through the invoker. A handler that takes some
    // unrelated second parameter must be rejected wholesale rather than emitting an invoker that
    // silently drops or miscasts the second argument.
    const string source = $$"""
            using Whizbang.Core.Messaging;
            using Whizbang.Core.Perspectives;

            namespace TestApp;

            public sealed class JobModel { }

            public sealed record TouchEvent(
              ICollectiveScope Scope,
              System.Collections.Generic.IReadOnlyList<System.Guid> MatchedStreamIds) : ICollectiveEvent;

            public sealed class JobPerspective {
              [CollectiveApplyFor]
              public ICollectiveSpec<JobModel> BadQueryHandler(TouchEvent e, int notAQuery) => null!;

              [CollectiveApplyFor]
              public ICollectiveSpec<JobModel> RealHandler(TouchEvent e, ICollectiveQuery q) => null!;
            }

            {{COLLECTIVE_STUBS}}
            """;

    var result = GeneratorTestHelper.RunGenerator<CollectiveApplyDiscoveryGenerator>(source);
    var code = GeneratorTestHelper.GetGeneratedSource(result, "CollectiveApplyRegistry.g.cs");

    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("RealHandler")
      .Because("A handler whose second parameter implements ICollectiveQuery must be discovered.");
    await Assert.That(code).DoesNotContain("BadQueryHandler")
      .Because("A handler whose second parameter is neither ICollectiveQuery nor implements it must be rejected entirely, not just have its query context dropped.");
  }
}
