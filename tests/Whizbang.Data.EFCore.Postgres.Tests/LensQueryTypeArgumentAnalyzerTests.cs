using System.Globalization;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.EFCore.Postgres.Generators;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Tests for <see cref="LensQueryTypeArgumentAnalyzer"/>.
/// Verifies that invalid type arguments to Query&lt;T&gt;() and GetByIdAsync&lt;T&gt;()
/// on multi-generic ILensQuery are detected at compile time.
/// </summary>
[Category("Unit")]
[Category("Analyzers")]
public class LensQueryTypeArgumentAnalyzerTests {
  #region Valid Usage - No Diagnostic

  /// <summary>
  /// Verifies that Query&lt;T1&gt;() with valid type argument does not trigger diagnostic.
  /// </summary>
  [Test]
  public async Task Query_WithT1_NoDiagnosticAsync() {
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
                public interface ILensQuery<T1, T2> : ILensQuery, IAsyncDisposable
                    where T1 : class
                    where T2 : class {
                    IQueryable<PerspectiveRow<T>> Query<T>() where T : class;
                    System.Threading.Tasks.Task<T?> GetByIdAsync<T>(Guid id, System.Threading.CancellationToken ct = default) where T : class;
                }
            }

            namespace TestNamespace {
                public class Order { public Guid Id { get; set; } }
                public class Customer { public Guid Id { get; set; } }

                public class TestResolver {
                    private readonly Whizbang.Core.Lenses.ILensQuery<Order, Customer> _query;

                    public TestResolver(Whizbang.Core.Lenses.ILensQuery<Order, Customer> query) {
                        _query = query;
                    }

                    public void ValidUsage() {
                        var orders = _query.Query<Order>(); // Valid - Order is T1
                    }
                }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<LensQueryTypeArgumentAnalyzer>(source);

    // Assert
    await Assert.That(diagnostics).IsEmpty();
  }

  /// <summary>
  /// Verifies that Query&lt;T2&gt;() with valid type argument does not trigger diagnostic.
  /// </summary>
  [Test]
  public async Task Query_WithT2_NoDiagnosticAsync() {
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
                public interface ILensQuery<T1, T2> : ILensQuery, IAsyncDisposable
                    where T1 : class
                    where T2 : class {
                    IQueryable<PerspectiveRow<T>> Query<T>() where T : class;
                    System.Threading.Tasks.Task<T?> GetByIdAsync<T>(Guid id, System.Threading.CancellationToken ct = default) where T : class;
                }
            }

            namespace TestNamespace {
                public class Order { public Guid Id { get; set; } }
                public class Customer { public Guid Id { get; set; } }

                public class TestResolver {
                    private readonly Whizbang.Core.Lenses.ILensQuery<Order, Customer> _query;

                    public TestResolver(Whizbang.Core.Lenses.ILensQuery<Order, Customer> query) {
                        _query = query;
                    }

                    public void ValidUsage() {
                        var customers = _query.Query<Customer>(); // Valid - Customer is T2
                    }
                }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<LensQueryTypeArgumentAnalyzer>(source);

    // Assert
    await Assert.That(diagnostics).IsEmpty();
  }

  /// <summary>
  /// Verifies that GetByIdAsync&lt;T1&gt;() with valid type argument does not trigger diagnostic.
  /// </summary>
  [Test]
  public async Task GetByIdAsync_WithT1_NoDiagnosticAsync() {
    // Arrange
    const string source = """
            using System;
            using System.Linq;
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
                    Task<T?> GetByIdAsync<T>(Guid id, System.Threading.CancellationToken ct = default) where T : class;
                }
            }

            namespace TestNamespace {
                public class Order { public Guid Id { get; set; } }
                public class Customer { public Guid Id { get; set; } }

                public class TestResolver {
                    private readonly Whizbang.Core.Lenses.ILensQuery<Order, Customer> _query;

                    public TestResolver(Whizbang.Core.Lenses.ILensQuery<Order, Customer> query) {
                        _query = query;
                    }

                    public async Task ValidUsageAsync() {
                        var order = await _query.GetByIdAsync<Order>(Guid.NewGuid()); // Valid
                    }
                }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<LensQueryTypeArgumentAnalyzer>(source);

    // Assert
    await Assert.That(diagnostics).IsEmpty();
  }

  /// <summary>
  /// Verifies that single-generic ILensQuery&lt;T&gt; is not analyzed (no Query&lt;T&gt;() method).
  /// </summary>
  [Test]
  public async Task SingleGenericILensQuery_NoDiagnosticAsync() {
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
                public interface ILensQuery<TModel> : ILensQuery where TModel : class {
                    IQueryable<PerspectiveRow<TModel>> Query { get; }
                    System.Threading.Tasks.Task<TModel?> GetByIdAsync(Guid id, System.Threading.CancellationToken ct = default);
                }
            }

            namespace TestNamespace {
                public class Order { public Guid Id { get; set; } }

                public class TestResolver {
                    private readonly Whizbang.Core.Lenses.ILensQuery<Order> _query;

                    public TestResolver(Whizbang.Core.Lenses.ILensQuery<Order> query) {
                        _query = query;
                    }

                    public void ValidUsage() {
                        var orders = _query.Query; // Property access, not generic method
                    }
                }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<LensQueryTypeArgumentAnalyzer>(source);

    // Assert
    await Assert.That(diagnostics).IsEmpty();
  }

  #endregion

  #region Invalid Usage - Reports WHIZ400

  /// <summary>
  /// Verifies that Query&lt;InvalidType&gt;() reports WHIZ400.
  /// </summary>
  [Test]
  public async Task Query_WithInvalidType_ReportsWHIZ400Async() {
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
                public interface ILensQuery<T1, T2> : ILensQuery, IAsyncDisposable
                    where T1 : class
                    where T2 : class {
                    IQueryable<PerspectiveRow<T>> Query<T>() where T : class;
                    System.Threading.Tasks.Task<T?> GetByIdAsync<T>(Guid id, System.Threading.CancellationToken ct = default) where T : class;
                }
            }

            namespace TestNamespace {
                public class Order { public Guid Id { get; set; } }
                public class Customer { public Guid Id { get; set; } }
                public class Product { public Guid Id { get; set; } } // Not in ILensQuery<Order, Customer>

                public class TestResolver {
                    private readonly Whizbang.Core.Lenses.ILensQuery<Order, Customer> _query;

                    public TestResolver(Whizbang.Core.Lenses.ILensQuery<Order, Customer> query) {
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
    await Assert.That(diagnostics[0].Id).IsEqualTo("WHIZ400");
    await Assert.That(diagnostics[0].GetMessage(CultureInfo.InvariantCulture)).Contains("Product");
    await Assert.That(diagnostics[0].GetMessage(CultureInfo.InvariantCulture)).Contains("Order");
    await Assert.That(diagnostics[0].GetMessage(CultureInfo.InvariantCulture)).Contains("Customer");
  }

  /// <summary>
  /// Verifies that GetByIdAsync&lt;InvalidType&gt;() reports WHIZ400.
  /// </summary>
  [Test]
  public async Task GetByIdAsync_WithInvalidType_ReportsWHIZ400Async() {
    // Arrange
    const string source = """
            using System;
            using System.Linq;
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
                    Task<T?> GetByIdAsync<T>(Guid id, System.Threading.CancellationToken ct = default) where T : class;
                }
            }

            namespace TestNamespace {
                public class Order { public Guid Id { get; set; } }
                public class Customer { public Guid Id { get; set; } }
                public class Product { public Guid Id { get; set; } }

                public class TestResolver {
                    private readonly Whizbang.Core.Lenses.ILensQuery<Order, Customer> _query;

                    public TestResolver(Whizbang.Core.Lenses.ILensQuery<Order, Customer> query) {
                        _query = query;
                    }

                    public async Task InvalidUsageAsync() {
                        var product = await _query.GetByIdAsync<Product>(Guid.NewGuid()); // INVALID
                    }
                }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<LensQueryTypeArgumentAnalyzer>(source);

    // Assert
    await Assert.That(diagnostics).Count().IsEqualTo(1);
    await Assert.That(diagnostics[0].Id).IsEqualTo("WHIZ400");
    await Assert.That(diagnostics[0].GetMessage(CultureInfo.InvariantCulture)).Contains("Product");
  }

  /// <summary>
  /// Verifies that three-generic ILensQuery with invalid type reports WHIZ400.
  /// </summary>
  [Test]
  public async Task ThreeGeneric_Query_WithInvalidType_ReportsWHIZ400Async() {
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
                public interface ILensQuery<T1, T2, T3> : ILensQuery, IAsyncDisposable
                    where T1 : class
                    where T2 : class
                    where T3 : class {
                    IQueryable<PerspectiveRow<T>> Query<T>() where T : class;
                    System.Threading.Tasks.Task<T?> GetByIdAsync<T>(Guid id, System.Threading.CancellationToken ct = default) where T : class;
                }
            }

            namespace TestNamespace {
                public class Order { public Guid Id { get; set; } }
                public class Customer { public Guid Id { get; set; } }
                public class Product { public Guid Id { get; set; } }
                public class Inventory { public Guid Id { get; set; } } // Not registered

                public class TestResolver {
                    private readonly Whizbang.Core.Lenses.ILensQuery<Order, Customer, Product> _query;

                    public TestResolver(Whizbang.Core.Lenses.ILensQuery<Order, Customer, Product> query) {
                        _query = query;
                    }

                    public void InvalidUsage() {
                        var inventory = _query.Query<Inventory>(); // INVALID
                    }
                }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<LensQueryTypeArgumentAnalyzer>(source);

    // Assert
    await Assert.That(diagnostics).Count().IsEqualTo(1);
    await Assert.That(diagnostics[0].Id).IsEqualTo("WHIZ400");
    await Assert.That(diagnostics[0].GetMessage(CultureInfo.InvariantCulture)).Contains("Inventory");
    await Assert.That(diagnostics[0].GetMessage(CultureInfo.InvariantCulture)).Contains("Order");
    await Assert.That(diagnostics[0].GetMessage(CultureInfo.InvariantCulture)).Contains("Customer");
    await Assert.That(diagnostics[0].GetMessage(CultureInfo.InvariantCulture)).Contains("Product");
  }

  /// <summary>
  /// Verifies that three-generic ILensQuery with valid T3 does NOT report diagnostic.
  /// </summary>
  [Test]
  public async Task ThreeGeneric_Query_WithT3_NoDiagnosticAsync() {
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
                public interface ILensQuery<T1, T2, T3> : ILensQuery, IAsyncDisposable
                    where T1 : class
                    where T2 : class
                    where T3 : class {
                    IQueryable<PerspectiveRow<T>> Query<T>() where T : class;
                    System.Threading.Tasks.Task<T?> GetByIdAsync<T>(Guid id, System.Threading.CancellationToken ct = default) where T : class;
                }
            }

            namespace TestNamespace {
                public class Order { public Guid Id { get; set; } }
                public class Customer { public Guid Id { get; set; } }
                public class Product { public Guid Id { get; set; } }

                public class TestResolver {
                    private readonly Whizbang.Core.Lenses.ILensQuery<Order, Customer, Product> _query;

                    public TestResolver(Whizbang.Core.Lenses.ILensQuery<Order, Customer, Product> query) {
                        _query = query;
                    }

                    public void ValidUsage() {
                        var products = _query.Query<Product>(); // Valid - Product is T3
                    }
                }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<LensQueryTypeArgumentAnalyzer>(source);

    // Assert
    await Assert.That(diagnostics).IsEmpty();
  }

  #endregion
}
