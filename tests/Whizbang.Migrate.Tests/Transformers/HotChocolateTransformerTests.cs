using Whizbang.Migrate.Transformers;

namespace Whizbang.Migrate.Tests.Transformers;

/// <summary>
/// Tests for the HotChocolate/Marten to Whizbang lenses transformer.
/// </summary>
/// <remarks>
/// This transformer both rewrites and deletes calls in GraphQL server configuration, where the
/// consequence of a mistake is a query surface that silently loses filtering or sorting rather
/// than failing to build. Its warnings are what an operator acts on afterwards, so a warning that
/// is wrong is not cosmetic -- following one adds a duplicate registration.
/// </remarks>
/// <tests>Whizbang.Migrate/Transformers/HotChocolateTransformer.cs:*</tests>
public class HotChocolateTransformerTests {

  [Test]
  public async Task TransformAsync_FileWithoutMartenPatterns_IsLeftByteForByteAsync() {
    var transformer = new HotChocolateTransformer();
    const string source = """
      using System;

      public class Startup {
        public void Configure() { }
      }
      """;

    var result = await transformer.TransformAsync(source, "Startup.cs");

    await Assert.That(result.TransformedCode).IsEqualTo(source);
    await Assert.That(result.Changes).IsEmpty();
    await Assert.That(result.Warnings).IsEmpty();
  }

  [Test]
  public async Task TransformAsync_AddMartenFiltering_BecomesAddWhizbangLensesAsync() {
    // The replacement is not like-for-like: AddWhizbangLenses covers filtering, sorting and
    // projections, which is why the sorting call is dropped rather than translated.
    var transformer = new HotChocolateTransformer();
    const string source = """
      using HotChocolate.Data.Marten;

      public class Startup {
        public void Configure(IRequestExecutorBuilder b) {
          b.AddMartenFiltering();
        }
      }
      """;

    var result = await transformer.TransformAsync(source, "Startup.cs");

    await Assert.That(result.TransformedCode).Contains("AddWhizbangLenses()");
    await Assert.That(result.TransformedCode).DoesNotContain("AddMartenFiltering");
    await Assert.That(result.Changes.Any(c => c.ChangeType == ChangeType.MethodCallReplacement)).IsTrue();
  }

  [Test]
  public async Task TransformAsync_FilteringArguments_AreDroppedAsync() {
    // AddWhizbangLenses takes a different signature, so carrying the old arguments across would
    // produce code that does not compile.
    var transformer = new HotChocolateTransformer();
    const string source = """
      using HotChocolate.Data.Marten;

      public class Startup {
        public void Configure(IRequestExecutorBuilder b) {
          b.AddMartenFiltering("customScope");
        }
      }
      """;

    var result = await transformer.TransformAsync(source, "Startup.cs");

    await Assert.That(result.TransformedCode).Contains("AddWhizbangLenses()");
    await Assert.That(result.TransformedCode).DoesNotContain("customScope");
  }

  [Test]
  public async Task TransformAsync_AddMartenSortingAlone_IsRemovedAndWarnedAboutAsync() {
    // Dropping sorting without adding lenses silently removes the ability to sort a GraphQL
    // query. Nothing fails to build, so the warning is the only signal the operator gets.
    var transformer = new HotChocolateTransformer();
    const string source = """
      using HotChocolate.Data.Marten;

      public class Startup {
        public void Configure(IRequestExecutorBuilder b) {
          b.AddMartenSorting();
        }
      }
      """;

    var result = await transformer.TransformAsync(source, "Startup.cs");

    await Assert.That(result.TransformedCode).DoesNotContain("AddMartenSorting");
    await Assert.That(result.Warnings.Any(w => w.Contains("AddWhizbangLenses", StringComparison.Ordinal))).IsTrue()
      .Because("sorting support is gone and nothing else in the file restores it");
  }

  [Test]
  public async Task TransformAsync_FluentChain_CollapsesToLensesWithoutAFalseWarningAsync() {
    // A fluent chain nests the sorting call OUTSIDE the filtering call, so the rewriter reaches
    // it first. Deciding "were lenses added" from visit order made this file warn that lenses
    // were missing at the very moment it was adding them -- and acting on that warning would
    // register AddWhizbangLenses twice.
    var transformer = new HotChocolateTransformer();
    const string source = """
      using HotChocolate.Data.Marten;

      public class Startup {
        public void Configure(IRequestExecutorBuilder b) {
          b.AddMartenFiltering().AddMartenSorting();
        }
      }
      """;

    var result = await transformer.TransformAsync(source, "Startup.cs");

    await Assert.That(result.TransformedCode).Contains("AddWhizbangLenses()");
    await Assert.That(result.TransformedCode).DoesNotContain("AddMartenSorting");
    await Assert.That(result.Warnings.Any(w => w.Contains("was not added", StringComparison.Ordinal))).IsFalse()
      .Because("the file does end up with AddWhizbangLenses, so warning otherwise is simply wrong");
  }

  [Test]
  public async Task TransformAsync_SortingBeforeFilteringInSeparateStatements_DoesNotWarnAsync() {
    // Same invariant across statements rather than a chain: what matters is whether the file
    // ends up with lenses, not which call the walker happened to see first.
    var transformer = new HotChocolateTransformer();
    const string source = """
      using HotChocolate.Data.Marten;

      public class Startup {
        public void Configure(IRequestExecutorBuilder b) {
          b.AddMartenSorting();
          b.AddMartenFiltering();
        }
      }
      """;

    var result = await transformer.TransformAsync(source, "Startup.cs");

    await Assert.That(result.Warnings.Any(w => w.Contains("was not added", StringComparison.Ordinal))).IsFalse();
  }

  [Test]
  public async Task TransformAsync_MartenUsing_BecomesWhizbangHotChocolateAsync() {
    var transformer = new HotChocolateTransformer();
    const string source = """
      using System;
      using HotChocolate.Data.Marten;

      public class Startup {
        public void Configure(IRequestExecutorBuilder b) => b.AddMartenFiltering();
      }
      """;

    var result = await transformer.TransformAsync(source, "Startup.cs");

    await Assert.That(result.TransformedCode).Contains("using Whizbang.Transports.HotChocolate;");
    await Assert.That(result.TransformedCode).DoesNotContain("HotChocolate.Data.Marten");
    await Assert.That(result.TransformedCode).Contains("using System;")
      .Because("unrelated usings are not this transformer's to touch");
  }

  [Test]
  public async Task TransformAsync_IMartenQueryable_BecomesIQueryableAsync() {
    // The return type is part of the public shape of a resolver. Left as IMartenQueryable it
    // references a package that is being removed.
    var transformer = new HotChocolateTransformer();
    const string source = """
      using HotChocolate.Data.Marten;

      public class OrderQueries {
        public IMartenQueryable<Order> GetOrders() => null!;
      }

      public class Order { }
      """;

    var result = await transformer.TransformAsync(source, "OrderQueries.cs");

    await Assert.That(result.TransformedCode).Contains("IQueryable<Order>");
    await Assert.That(result.TransformedCode).DoesNotContain("IMartenQueryable");
    await Assert.That(result.Changes.Any(c => c.ChangeType == ChangeType.TypeRename)).IsTrue();
  }

  [Test]
  public async Task TransformAsync_IMartenQueryableWithoutTheUsing_IsStillRenamedAsync() {
    // Detection accepts the type alone, so a file relying on a global using is still migrated
    // rather than left referencing a package that no longer exists.
    var transformer = new HotChocolateTransformer();
    const string source = """
      public class OrderQueries {
        public IMartenQueryable<Order> GetOrders() => null!;
      }

      public class Order { }
      """;

    var result = await transformer.TransformAsync(source, "OrderQueries.cs");

    await Assert.That(result.TransformedCode).Contains("IQueryable<Order>");
  }

  [Test]
  public async Task TransformAsync_DuplicateMartenUsings_AreConsolidatedAsync() {
    // Two spellings of the same import must collapse to one replacement, not two identical
    // usings, which would not compile.
    var transformer = new HotChocolateTransformer();
    const string source = """
      using HotChocolate.Data.Marten;
      using HotChocolate.Data.Marten;

      public class Startup {
        public void Configure(IRequestExecutorBuilder b) => b.AddMartenFiltering();
      }
      """;

    var result = await transformer.TransformAsync(source, "Startup.cs");

    var count = result.TransformedCode.Split("using Whizbang.Transports.HotChocolate;").Length - 1;
    await Assert.That(count).IsEqualTo(1);
  }
}
