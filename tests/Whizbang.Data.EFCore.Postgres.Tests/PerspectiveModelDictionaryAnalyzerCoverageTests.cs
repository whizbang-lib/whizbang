using System.Globalization;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.EFCore.Postgres.Generators;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Coverage for <see cref="PerspectiveModelDictionaryAnalyzer"/> paths the existing
/// <c>PerspectiveModelDictionaryAnalyzerTests</c> never exercise: an open generic type
/// parameter standing in for TModel, a non-named (array) property type, and the
/// Newtonsoft.Json ignore-attribute variant of the ignored-property check.
/// </summary>
[Category("Unit")]
[Category("Analyzers")]
[Category("Shard2")]
public class PerspectiveModelDictionaryAnalyzerCoverageTests {
  #region Open Generic Type Parameter As TModel - Guarded, Not Crashed

  /// <summary>
  /// Verifies that a generic perspective class whose model is its own open type parameter
  /// (so the first type argument of <c>IPerspectiveFor&lt;TState, TestEvent&gt;</c> is an
  /// <c>ITypeParameterSymbol</c>, not an <c>INamedTypeSymbol</c>) is skipped rather than
  /// crashing the analyzer, and that a genuine Dictionary property elsewhere in the same
  /// compilation is still analyzed normally. If this guard regressed to an unconditional
  /// cast, a consumer who writes a generic base perspective class would see the analyzer
  /// throw (reported as an AD0001 analyzer-crash diagnostic) instead of quietly moving on,
  /// which would also mask the real WHIZ810 finding below it in build output.
  /// </summary>
  [Test]
  public async Task GenericPerspective_WithOpenTypeParameterModel_SkippedWithoutCrashingAsync() {
    // Arrange
    const string source = """
            using System;
            using System.Collections.Generic;

            namespace Whizbang.Core.Perspectives {
                public interface IPerspectiveFor<TModel> { }
                public interface IPerspectiveFor<TModel, TEvent1> : IPerspectiveFor<TModel> { }
            }

            namespace TestNamespace {
                public class TestModel {
                    public Guid Id { get; set; }
                    public Dictionary<string, string> Attributes { get; set; } = new();
                }

                public record TestEvent(Guid Id);

                // TState is an open type parameter here - the first type argument of
                // IPerspectiveFor<TState, TestEvent> is an ITypeParameterSymbol, not an
                // INamedTypeSymbol, and must be skipped rather than crash the analyzer.
                public class GenericPerspective<TState> : Whizbang.Core.Perspectives.IPerspectiveFor<TState, TestEvent> {
                }

                public class TestPerspective : Whizbang.Core.Perspectives.IPerspectiveFor<TestModel, TestEvent> {
                    public TestModel Apply(TestModel? model, TestEvent evt) => model ?? new();
                }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveModelDictionaryAnalyzer>(source);

    // Assert - exactly the one real finding, proving the generic class was skipped cleanly
    await Assert.That(diagnostics).Count().IsEqualTo(1)
        .Because("the generic perspective's unresolved model must be skipped, leaving only the real TestModel finding");
    await Assert.That(diagnostics[0].Id).IsEqualTo("WHIZ810");
    await Assert.That(diagnostics[0].GetMessage(CultureInfo.InvariantCulture)).Contains("Attributes");
  }

  #endregion

  #region Non-Named Property Type - Guarded, Not Crashed

  /// <summary>
  /// Verifies that an array-typed property (an <c>IArrayTypeSymbol</c>, not an
  /// <c>INamedTypeSymbol</c>) is skipped by the Dictionary check without crashing the
  /// analyzer. Arrays are this library's own separate hazard
  /// (<c>PerspectiveModelArrayAnalyzer</c>, WHIZ200); if this guard regressed to an
  /// unconditional cast, any perspective model with an array property would make this
  /// analyzer throw (AD0001) instead of silently moving on, breaking the build for code
  /// that has nothing to do with dictionaries.
  /// </summary>
  [Test]
  public async Task PerspectiveModel_WithArrayProperty_NoDiagnosticAsync() {
    // Arrange
    const string source = """
            using System;

            namespace Whizbang.Core.Perspectives {
                public interface IPerspectiveFor<TModel> { }
                public interface IPerspectiveFor<TModel, TEvent1> : IPerspectiveFor<TModel> { }
            }

            namespace TestNamespace {
                public class TestModel {
                    public Guid Id { get; set; }
                    public string[] Tags { get; set; } = Array.Empty<string>();
                }

                public record TestEvent(Guid Id);

                public class TestPerspective : Whizbang.Core.Perspectives.IPerspectiveFor<TestModel, TestEvent> {
                    public TestModel Apply(TestModel? model, TestEvent evt) => model ?? new();
                }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveModelDictionaryAnalyzer>(source);

    // Assert
    await Assert.That(diagnostics).IsEmpty().Because("an array property is not a Dictionary and must not be miscast or flagged");
  }

  #endregion

  #region Newtonsoft.Json Ignore Attribute Variant

  /// <summary>
  /// Verifies that a Dictionary property carrying Newtonsoft.Json's <c>[JsonIgnore]</c> (as
  /// opposed to the System.Text.Json or EF Core variants already covered) is excluded from
  /// the Dictionary check. If this branch regressed, a consumer using Newtonsoft.Json to
  /// exclude a Dictionary property from serialization would get a spurious WHIZ810 warning
  /// for a property that is never actually persisted.
  /// </summary>
  [Test]
  public async Task PerspectiveModel_WithNewtonsoftJsonIgnoredDictionary_NoDiagnosticAsync() {
    // Arrange
    const string source = """
            using System;
            using System.Collections.Generic;

            namespace Whizbang.Core.Perspectives {
                public interface IPerspectiveFor<TModel> { }
                public interface IPerspectiveFor<TModel, TEvent1> : IPerspectiveFor<TModel> { }
            }

            namespace Newtonsoft.Json {
                public sealed class JsonIgnoreAttribute : System.Attribute { }
            }

            namespace TestNamespace {
                public class TestModel {
                    public Guid Id { get; set; }

                    [Newtonsoft.Json.JsonIgnore]
                    public Dictionary<string, string> CachedData { get; set; } = new();
                }

                public record TestEvent(Guid Id);

                public class TestPerspective : Whizbang.Core.Perspectives.IPerspectiveFor<TestModel, TestEvent> {
                    public TestModel Apply(TestModel? model, TestEvent evt) => model ?? new();
                }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveModelDictionaryAnalyzer>(source);

    // Assert
    await Assert.That(diagnostics).IsEmpty().Because("Newtonsoft.Json's [JsonIgnore] means the property is never serialized, so it is not a persistence hazard");
  }

  #endregion
}
