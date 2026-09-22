using System.Globalization;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.EFCore.Postgres.Generators;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Coverage for <see cref="LensQueryTypeArgumentAnalyzer"/> paths the existing
/// <c>LensQueryTypeArgumentAnalyzerTests</c> never exercise: a non-instance (static) call to a
/// same-named generic method, a single-generic <c>ILensQuery&lt;TModel&gt;</c> that happens to
/// expose its own generic <c>Query&lt;T&gt;()</c> method, and the two ways a receiver can carry
/// the multi-generic contract without literally being an interface named <c>ILensQuery</c>: a
/// derived/facade interface, and a concrete class.
/// </summary>
[Category("Unit")]
[Category("Analyzers")]
[Category("Shard2")]
public class LensQueryTypeArgumentAnalyzerCoverageTests {
  #region Non-Instance Receiver - No Diagnostic

  /// <summary>
  /// Verifies that a static generic method literally named <c>Query</c> is left alone.
  /// If this guard regresses, the analyzer would try to resolve a receiver instance that
  /// doesn't exist for a static call, and either throw analyzing unrelated code or misreport
  /// a diagnostic against a method that has nothing to do with any <c>ILensQuery</c>.
  /// </summary>
  [Test]
  public async Task StaticGenericQueryMethod_NoInstanceReceiver_NoDiagnosticAsync() {
    // Arrange
    const string source = """
            using System;

            namespace TestNamespace {
                public class Order { public Guid Id { get; set; } }

                public static class QueryHelpers {
                    // Unrelated static method that happens to match this analyzer's name/arity trigger.
                    public static string? Query<T>() where T : class => null;
                }

                public class TestResolver {
                    public void UseStaticHelper() {
                        var result = QueryHelpers.Query<Order>(); // Static call - no instance receiver at all
                    }
                }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<LensQueryTypeArgumentAnalyzer>(source);

    // Assert
    await Assert.That(diagnostics).IsEmpty().Because("a static call has no receiver instance, so the analyzer must not attempt to inspect one");
  }

  #endregion

  #region Single-Generic Interface With Its Own Generic Method - No Diagnostic

  /// <summary>
  /// Verifies that a single-generic <c>ILensQuery&lt;TModel&gt;</c> whose own generic method is
  /// named <c>Query</c> is not treated as the multi-generic contract this analyzer protects.
  /// If this guard regresses, the analyzer would need to reason about interfaces with fewer than
  /// two type parameters - where there is no ambiguity to catch in the first place - and could
  /// misfire WHIZ400 on correct code that has nothing to do with the multi-generic case.
  /// </summary>
  [Test]
  public async Task SingleGenericLensQueryWithGenericMethod_NotMultiGenericContract_NoDiagnosticAsync() {
    // Arrange
    const string source = """
            using System;
            using System.Linq;

            namespace Whizbang.Core.Lenses {
                public interface ILensQuery { }
                public class PerspectiveRow<T> where T : class {
                    public Guid Id { get; set; }
                    public T Data { get; set; } = default!;
                }

                // Single-generic contract that (unusually) exposes its own generic Query<T>()
                // method, matching this analyzer's name/arity trigger without being the
                // multi-generic contract it protects.
                public interface ILensQuery<TModel> : ILensQuery where TModel : class {
                    IQueryable<PerspectiveRow<T>> Query<T>() where T : class;
                }
            }

            namespace TestNamespace {
                public class Order { public Guid Id { get; set; } }

                public class TestResolver {
                    private readonly Whizbang.Core.Lenses.ILensQuery<Order> _query;

                    public TestResolver(Whizbang.Core.Lenses.ILensQuery<Order> query) {
                        _query = query;
                    }

                    public void UseSingleGeneric() {
                        var orders = _query.Query<Order>(); // Only one type parameter - not multi-generic
                    }
                }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<LensQueryTypeArgumentAnalyzer>(source);

    // Assert
    await Assert.That(diagnostics).IsEmpty().Because("ILensQuery<TModel> has only one type parameter, so there is no ambiguous type argument to validate");
  }

  #endregion

  #region Multi-Generic Contract Reached Through A Derived Interface

  /// <summary>
  /// Verifies that the analyzer still finds the multi-generic contract when the receiver's
  /// compile-time type is a derived/facade interface rather than <c>ILensQuery</c> itself, and
  /// stays silent for a valid type argument. If this path regresses, any consumer-facing lens
  /// facade interface would either be silently skipped (missing real bugs, see the companion
  /// invalid-type test) or the analyzer would fail to recognize it at all.
  /// </summary>
  [Test]
  public async Task DerivedInterfaceOverMultiGenericLensQuery_WithValidType_NoDiagnosticAsync() {
    // Arrange
    const string source = """
            using System;
            using System.Linq;
            using System.Threading;
            using System.Threading.Tasks;

            namespace Whizbang.Core.Lenses {
                public interface ILensQuery { }
                public class PerspectiveRow<T> where T : class {
                    public Guid Id { get; set; }
                    public T Data { get; set; } = default!;
                }
                public interface ILensQuery<T1, T2> : ILensQuery, IAsyncDisposable
                    where T1 : class
                    where T2 : class {
                    IQueryable<PerspectiveRow<T>> Query<T>() where T : class;
                    Task<T?> GetByIdAsync<T>(Guid id, CancellationToken ct = default) where T : class;
                }
            }

            namespace TestNamespace {
                public class Order { public Guid Id { get; set; } }
                public class Customer { public Guid Id { get; set; } }

                // A domain-specific facade that is not literally named "ILensQuery" but inherits
                // the multi-generic contract this analyzer protects.
                public interface IOrderCustomerLens : Whizbang.Core.Lenses.ILensQuery<Order, Customer> { }

                public class TestResolver {
                    private readonly IOrderCustomerLens _query;

                    public TestResolver(IOrderCustomerLens query) {
                        _query = query;
                    }

                    public void ValidUsage() {
                        var orders = _query.Query<Order>(); // Valid - Order is T1, found via the inherited interface
                    }
                }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<LensQueryTypeArgumentAnalyzer>(source);

    // Assert
    await Assert.That(diagnostics).IsEmpty().Because("Order is a valid type argument for the inherited ILensQuery<Order, Customer> contract");
  }

  /// <summary>
  /// Verifies that the analyzer still reports WHIZ400 for an invalid type argument when the
  /// receiver's compile-time type is a derived/facade interface rather than <c>ILensQuery</c>
  /// itself. Without this, an invalid type argument on any facade lens interface would slip past
  /// compile time and only fail with a runtime ArgumentException.
  /// </summary>
  [Test]
  public async Task DerivedInterfaceOverMultiGenericLensQuery_WithInvalidType_ReportsWHIZ400Async() {
    // Arrange
    const string source = """
            using System;
            using System.Linq;
            using System.Threading;
            using System.Threading.Tasks;

            namespace Whizbang.Core.Lenses {
                public interface ILensQuery { }
                public class PerspectiveRow<T> where T : class {
                    public Guid Id { get; set; }
                    public T Data { get; set; } = default!;
                }
                public interface ILensQuery<T1, T2> : ILensQuery, IAsyncDisposable
                    where T1 : class
                    where T2 : class {
                    IQueryable<PerspectiveRow<T>> Query<T>() where T : class;
                    Task<T?> GetByIdAsync<T>(Guid id, CancellationToken ct = default) where T : class;
                }
            }

            namespace TestNamespace {
                public class Order { public Guid Id { get; set; } }
                public class Customer { public Guid Id { get; set; } }
                public class Product { public Guid Id { get; set; } } // Not in ILensQuery<Order, Customer>

                public interface IOrderCustomerLens : Whizbang.Core.Lenses.ILensQuery<Order, Customer> { }

                public class TestResolver {
                    private readonly IOrderCustomerLens _query;

                    public TestResolver(IOrderCustomerLens query) {
                        _query = query;
                    }

                    public void InvalidUsage() {
                        var products = _query.Query<Product>(); // INVALID - Product is not T1 or T2
                    }
                }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<LensQueryTypeArgumentAnalyzer>(source);

    // Assert
    await Assert.That(diagnostics).Count().IsEqualTo(1);
    await Assert.That(diagnostics[0].Id).IsEqualTo("WHIZ400").Because("an invalid type argument reached through a derived interface must still be reported");
    await Assert.That(diagnostics[0].GetMessage(CultureInfo.InvariantCulture)).Contains("Product");
  }

  #endregion

  #region Multi-Generic Contract Reached Through A Concrete Class

  /// <summary>
  /// Verifies that the analyzer reports WHIZ400 when the receiver's compile-time type is a
  /// concrete class implementing <c>ILensQuery&lt;T1, T2&gt;</c> directly, rather than the
  /// interface. Without this, code holding a concrete lens implementation (not the interface
  /// type) would silently accept an invalid type argument that later throws at runtime.
  /// </summary>
  [Test]
  public async Task ConcreteClassImplementingMultiGenericLensQuery_WithInvalidType_ReportsWHIZ400Async() {
    // Arrange
    const string source = """
            using System;
            using System.Linq;
            using System.Threading;
            using System.Threading.Tasks;

            namespace Whizbang.Core.Lenses {
                public interface ILensQuery { }
                public class PerspectiveRow<T> where T : class {
                    public Guid Id { get; set; }
                    public T Data { get; set; } = default!;
                }
                public interface ILensQuery<T1, T2> : ILensQuery, IAsyncDisposable
                    where T1 : class
                    where T2 : class {
                    IQueryable<PerspectiveRow<T>> Query<T>() where T : class;
                    Task<T?> GetByIdAsync<T>(Guid id, CancellationToken ct = default) where T : class;
                }
            }

            namespace TestNamespace {
                public class Order { public Guid Id { get; set; } }
                public class Customer { public Guid Id { get; set; } }
                public class Product { public Guid Id { get; set; } } // Not in ILensQuery<Order, Customer>

                // Concrete implementation - the receiver below is typed as this class, not the interface.
                public class OrderCustomerLensImpl : Whizbang.Core.Lenses.ILensQuery<Order, Customer> {
                    public IQueryable<Whizbang.Core.Lenses.PerspectiveRow<T>> Query<T>() where T : class =>
                        throw new NotImplementedException();

                    public Task<T?> GetByIdAsync<T>(Guid id, CancellationToken ct = default) where T : class =>
                        throw new NotImplementedException();

                    public ValueTask DisposeAsync() => default;
                }

                public class TestResolver {
                    private readonly OrderCustomerLensImpl _query;

                    public TestResolver(OrderCustomerLensImpl query) {
                        _query = query;
                    }

                    public void InvalidUsage() {
                        var products = _query.Query<Product>(); // INVALID - Product is not T1 or T2
                    }
                }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<LensQueryTypeArgumentAnalyzer>(source);

    // Assert
    await Assert.That(diagnostics).Count().IsEqualTo(1);
    await Assert.That(diagnostics[0].Id).IsEqualTo("WHIZ400").Because("an invalid type argument on a concrete class receiver must still be reported");
    await Assert.That(diagnostics[0].GetMessage(CultureInfo.InvariantCulture)).Contains("Product");
  }

  #endregion
}
