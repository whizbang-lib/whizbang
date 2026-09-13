using TUnit.Assertions.Extensions;
using Whizbang.Generators;

namespace Whizbang.Generators.Tests;

/// <summary>
/// The generated registration that tells the running process which models a request can shape.
/// </summary>
/// <remarks>
/// <para>
/// The build warning and this registration answer different questions. WHIZ306 is read once by
/// whoever compiles; the registry is what lets the process ask the same question against a live
/// table, where the row count is known and a missing index has a measurable cost.
/// </para>
/// <para>
/// Asserted on the emitted source rather than by loading it, because what matters here is that a
/// registration is emitted per exposed model, with the exposures combined and nothing emitted for an
/// assembly that exposes nothing. The registry's own behavior is pinned in
/// <c>QueryExposureRegistryTests</c>, and the module initializer actually running is pinned by the
/// saga chain's equivalent test.
/// </para>
/// </remarks>
/// <docs>operations/diagnostics/whiz306</docs>
public class QueryExposureRegistrationGeneratorTests {
  private const string PREAMBLE = """
    using System;
    using System.Linq;
    using Whizbang.Core;
    using Whizbang.Core.Lenses;
    using Whizbang.Core.Perspectives;

    namespace TestApp;

    [ComposesQueryFromRequest]
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class SortableAttribute : Attribute {
      public bool EnableSorting { get; set; } = true;
    }

    [ComposesQueryFromRequest(QueryExposures.Filtering)]
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class FilterOnlyAttribute : Attribute { }

    [ComposesQueryFromRequest(QueryExposures.Expression)]
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class ExpressiveAttribute : Attribute { }

    public class JobModel {
      [StreamId]
      public Guid Id { get; init; }
      public string JobName { get; init; } = string.Empty;
    }

    [SuppressIndexAdvisory("admin screen, tens of rows")]
    public class AnsweredModel {
      [StreamId]
      public Guid Id { get; init; }
      public string Note { get; init; } = string.Empty;
    }
    """;

  private static string _generate(string source) {
    var result = GeneratorTestHelper.RunGenerator<QueryExposureRegistrationGenerator>(
      PREAMBLE + Environment.NewLine + source);

    return GeneratorTestHelper.GetGeneratedSource(result, "QueryExposureRegistrations.g.cs") ?? "";
  }

  /// <summary>An exposed model is registered.</summary>
  [Test]
  public async Task AnExposedModelIsRegisteredAsync() {
    var generated = _generate("""
      [Sortable]
      public interface IJobLens : ILensQuery<JobModel>;
      """);

    await Assert.That(generated).Contains("QueryExposureRegistry.Register<global::TestApp.JobModel>");
    await Assert.That(generated).Contains("QueryExposures.Ordering");
    await Assert.That(generated).Contains("ModuleInitializer")
      .Because("the registration must run at assembly load, without the host calling anything");
    await Assert.That(generated).Contains("\"JobName\"")
      .Because("which fields were unaccounted for is a build-time fact the runtime cannot recompute "
        + "without reading the model's attributes");
  }

  /// <summary>
  /// A model that already answered registers its exposure with no unaccounted fields.
  /// </summary>
  /// <remarks>
  /// This is what carries a recorded decision to runtime. Registering the fields anyway would make
  /// [SuppressIndexAdvisory] stand down the build warning while the runtime advisory kept asking.
  /// </remarks>
  [Test]
  public async Task AnAccountedForModelRegistersNoFieldsAsync() {
    var generated = _generate("""
      [Sortable]
      public interface IAnsweredLens : ILensQuery<AnsweredModel>;
      """);

    await Assert.That(generated).Contains("Register<global::TestApp.AnsweredModel>");
    await Assert.That(generated).DoesNotContain("\"Note\"")
      .Because("the author recorded a decision, so there is nothing for the runtime to advise about");
  }

  /// <summary>Every surface shape the predicate accepts reaches the registry.</summary>
  /// <remarks>
  /// A lens can be declared as a record, and a resolver can be a method or a property. Each is a
  /// separate arm of the syntactic predicate, so each is a separate way for the registration to be
  /// silently absent while the build warning still fires.
  /// </remarks>
  [Test]
  [Arguments("[Sortable] public record RecordLens : ILensQuery<JobModel>;")]
  [Arguments("public class M { [Sortable] public IQueryable<JobModel> Q() => throw new NotImplementedException(); }")]
  [Arguments("public class P { [Sortable] public IQueryable<JobModel> Q => throw new NotImplementedException(); }")]
  public async Task EverySurfaceShapeIsRegisteredAsync(string surface) {
    var generated = _generate(surface);

    await Assert.That(generated).Contains("Register<global::TestApp.JobModel>");
  }

  /// <summary>
  /// A marked surface that exposes no model registers nothing.
  /// </summary>
  /// <remarks>
  /// An attribute can be marked and applied to something that is not a query at all. Registering it
  /// would need a model to register against, and inventing one is worse than saying nothing.
  /// </remarks>
  [Test]
  public async Task AMarkedSurfaceWithNoModelRegistersNothingAsync() {
    var generated = _generate("""
      [Sortable]
      public class NotAQuery {
        public string Name { get; init; } = string.Empty;
      }
      """);

    await Assert.That(generated).IsEmpty()
      .Because("the attribute says a request shapes the query, but there is no query and no model "
        + "behind it");
  }

  /// <summary>An assembly that exposes nothing emits no file.</summary>  /// <summary>An assembly that exposes nothing emits no file.</summary>
  /// <remarks>
  /// Most assemblies expose nothing, and an empty initializer in every one of them is cost with no
  /// content.
  /// </remarks>
  [Test]
  public async Task AnAssemblyWithNoExposureEmitsNothingAsync() {
    var generated = _generate("""
      public interface IQuietLens : ILensQuery<JobModel>;
      """);

    await Assert.That(generated).IsEmpty();
  }

  /// <summary>
  /// Several surfaces over one model produce one registration with the exposures combined.
  /// </summary>
  /// <remarks>
  /// A call per surface would say the same thing repeatedly and grow the generated file with the
  /// number of endpoints. The registry would still combine them, so the difference is only noise.
  /// </remarks>
  [Test]
  public async Task SeveralSurfacesOverOneModelCombineAsync() {
    var generated = _generate("""
      [Sortable]
      public interface ISortLens : ILensQuery<JobModel>;

      [FilterOnly]
      public interface IFilterLens : ILensQuery<JobModel>;
      """);

    var registrations = generated.Split("QueryExposureRegistry.Register<").Length - 1;

    await Assert.That(registrations).IsEqualTo(1);
    await Assert.That(generated).Contains("QueryExposures.Filtering | global::Whizbang.Core.Perspectives.QueryExposures.Ordering");
  }

  /// <summary>Filtering-only exposure is registered as filtering, not widened.</summary>
  [Test]
  public async Task AFilterOnlyExposureIsRegisteredAsFilteringAsync() {
    var generated = _generate("""
      [FilterOnly]
      public interface IFilterLens : ILensQuery<JobModel>;
      """);

    await Assert.That(generated).Contains("QueryExposures.Filtering");
    await Assert.That(generated).DoesNotContain("QueryExposures.Ordering")
      .Because("registering more than the surface offers would make the runtime view wrong in the "
        + "direction that raises false advisories");
  }

  /// <summary>An expression surface is registered as the widest exposure.</summary>
  [Test]
  public async Task AnExpressionSurfaceIsRegisteredAsExpressionAsync() {
    var generated = _generate("""
      [Expressive]
      public interface IExpressiveLens : ILensQuery<JobModel>;
      """);

    await Assert.That(generated).Contains("QueryExposures.Expression");
  }

  /// <summary>A surface with sorting switched off narrows what is registered.</summary>
  [Test]
  public async Task ASwitchedOffCapabilityNarrowsTheRegistrationAsync() {
    var generated = _generate("""
      [Sortable(EnableSorting = false)]
      public interface INoSortLens : ILensQuery<JobModel>;
      """);

    await Assert.That(generated).Contains("QueryExposures.Filtering");
    await Assert.That(generated).DoesNotContain("QueryExposures.Ordering");
  }
}
