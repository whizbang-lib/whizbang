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
    using Whizbang.Core;
    using Whizbang.Core.Lenses;
    using Whizbang.Core.Perspectives;

    namespace TestApp;

    [ComposesQueryFromRequest]
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class SortableAttribute : Attribute {
      public bool EnableSorting { get; set; } = true;
    }

    [ComposesQueryFromRequest(QueryExposure.Filtering)]
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class FilterOnlyAttribute : Attribute { }

    [ComposesQueryFromRequest(QueryExposure.Expression)]
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class ExpressiveAttribute : Attribute { }

    public class JobModel {
      [StreamId]
      public Guid Id { get; init; }
      public string JobName { get; init; } = string.Empty;
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
    await Assert.That(generated).Contains("QueryExposure.Ordering");
    await Assert.That(generated).Contains("ModuleInitializer")
      .Because("the registration must run at assembly load, without the host calling anything");
  }

  /// <summary>An assembly that exposes nothing emits no file.</summary>
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
    await Assert.That(generated).Contains("QueryExposure.Filtering | global::Whizbang.Core.Perspectives.QueryExposure.Ordering");
  }

  /// <summary>Filtering-only exposure is registered as filtering, not widened.</summary>
  [Test]
  public async Task AFilterOnlyExposureIsRegisteredAsFilteringAsync() {
    var generated = _generate("""
      [FilterOnly]
      public interface IFilterLens : ILensQuery<JobModel>;
      """);

    await Assert.That(generated).Contains("QueryExposure.Filtering");
    await Assert.That(generated).DoesNotContain("QueryExposure.Ordering")
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

    await Assert.That(generated).Contains("QueryExposure.Expression");
  }

  /// <summary>A surface with sorting switched off narrows what is registered.</summary>
  [Test]
  public async Task ASwitchedOffCapabilityNarrowsTheRegistrationAsync() {
    var generated = _generate("""
      [Sortable(EnableSorting = false)]
      public interface INoSortLens : ILensQuery<JobModel>;
      """);

    await Assert.That(generated).Contains("QueryExposure.Filtering");
    await Assert.That(generated).DoesNotContain("QueryExposure.Ordering");
  }
}
