using System.Globalization;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.EFCore.Postgres.Generators;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Tests for <see cref="PerspectiveModelDictionaryAnalyzer"/>.
/// Verifies that Dictionary properties in perspective models are detected and reported.
/// </summary>
[Category("Unit")]
public class PerspectiveModelDictionaryAnalyzerTests {
  /// <summary>
  /// Verifies that a perspective model with Dictionary property triggers WHIZ810.
  /// </summary>
  [Test]
  public async Task PerspectiveModel_WithDictionary_ReportsDiagnosticAsync() {
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

                public class TestPerspective : Whizbang.Core.Perspectives.IPerspectiveFor<TestModel, TestEvent> {
                    public TestModel Apply(TestModel? model, TestEvent evt) => model ?? new();
                }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveModelDictionaryAnalyzer>(source);

    // Assert
    await Assert.That(diagnostics).Count().IsEqualTo(1);
    await Assert.That(diagnostics[0].Id).IsEqualTo("WHIZ810");
    await Assert.That(diagnostics[0].GetMessage(CultureInfo.InvariantCulture)).Contains("Attributes");
    await Assert.That(diagnostics[0].GetMessage(CultureInfo.InvariantCulture)).Contains("Dictionary");
  }

  /// <summary>
  /// Verifies that a perspective model with List&lt;T&gt; does NOT trigger diagnostic.
  /// </summary>
  [Test]
  public async Task PerspectiveModel_WithList_NoDiagnosticAsync() {
    // Arrange
    const string source = """
            using System;
            using System.Collections.Generic;

            namespace Whizbang.Core.Perspectives {
                public interface IPerspectiveFor<TModel> { }
                public interface IPerspectiveFor<TModel, TEvent1> : IPerspectiveFor<TModel> { }
            }

            namespace TestNamespace {
                public class ScopeExtension {
                    public string Key { get; set; } = string.Empty;
                    public string Value { get; set; } = string.Empty;
                }

                public class TestModel {
                    public Guid Id { get; set; }
                    public List<ScopeExtension> Extensions { get; set; } = new();
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
    await Assert.That(diagnostics).IsEmpty();
  }

  /// <summary>
  /// Verifies that a nested type with Dictionary is also detected.
  /// </summary>
  [Test]
  public async Task PerspectiveModel_WithNestedDictionary_ReportsDiagnosticAsync() {
    // Arrange
    const string source = """
            using System;
            using System.Collections.Generic;

            namespace Whizbang.Core.Perspectives {
                public interface IPerspectiveFor<TModel> { }
                public interface IPerspectiveFor<TModel, TEvent1> : IPerspectiveFor<TModel> { }
            }

            namespace TestNamespace {
                public class NestedType {
                    public Dictionary<string, int> Metadata { get; set; } = new();
                }

                public class TestModel {
                    public Guid Id { get; set; }
                    public NestedType Nested { get; set; } = new();
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
    await Assert.That(diagnostics).Count().IsEqualTo(1);
    await Assert.That(diagnostics[0].Id).IsEqualTo("WHIZ810");
    await Assert.That(diagnostics[0].GetMessage(CultureInfo.InvariantCulture)).Contains("Metadata");
  }

  /// <summary>
  /// Verifies that List&lt;Dictionary&gt; is also detected.
  /// </summary>
  [Test]
  public async Task PerspectiveModel_WithListOfDictionary_ReportsDiagnosticAsync() {
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
                    public List<Dictionary<string, string>> Records { get; set; } = new();
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
    await Assert.That(diagnostics).Count().IsEqualTo(1);
    await Assert.That(diagnostics[0].Id).IsEqualTo("WHIZ810");
    await Assert.That(diagnostics[0].GetMessage(CultureInfo.InvariantCulture)).Contains("Records");
  }

  /// <summary>
  /// Verifies that non-perspective classes with Dictionary do NOT trigger diagnostic.
  /// </summary>
  [Test]
  public async Task NonPerspectiveClass_WithDictionary_NoDiagnosticAsync() {
    // Arrange
    const string source = """
            using System;
            using System.Collections.Generic;

            namespace TestNamespace {
                public class RegularModel {
                    public Guid Id { get; set; }
                    public Dictionary<string, string> Attributes { get; set; } = new();
                }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveModelDictionaryAnalyzer>(source);

    // Assert
    await Assert.That(diagnostics).IsEmpty();
  }

  /// <summary>
  /// Verifies that IDictionary is also detected (not just Dictionary).
  /// </summary>
  [Test]
  public async Task PerspectiveModel_WithIDictionary_ReportsDiagnosticAsync() {
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
                    public IDictionary<string, object> Data { get; set; } = new Dictionary<string, object>();
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
    await Assert.That(diagnostics).Count().IsEqualTo(1);
    await Assert.That(diagnostics[0].Id).IsEqualTo("WHIZ810");
  }

  /// <summary>
  /// Verifies that [NotMapped] Dictionary properties are NOT flagged.
  /// </summary>
  [Test]
  public async Task PerspectiveModel_WithNotMappedDictionary_NoDiagnosticAsync() {
    // Arrange
    const string source = """
            using System;
            using System.Collections.Generic;
            using System.ComponentModel.DataAnnotations.Schema;

            namespace Whizbang.Core.Perspectives {
                public interface IPerspectiveFor<TModel> { }
                public interface IPerspectiveFor<TModel, TEvent1> : IPerspectiveFor<TModel> { }
            }

            namespace TestNamespace {
                public class TestModel {
                    public Guid Id { get; set; }
                    [NotMapped]
                    public Dictionary<string, string> Fields { get; set; } = new();
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
    await Assert.That(diagnostics).IsEmpty();
  }

  /// <summary>
  /// Verifies that [JsonIgnore] Dictionary properties are NOT flagged.
  /// </summary>
  [Test]
  public async Task PerspectiveModel_WithJsonIgnoreDictionary_NoDiagnosticAsync() {
    // Arrange
    const string source = """
            using System;
            using System.Collections.Generic;
            using System.Text.Json.Serialization;

            namespace Whizbang.Core.Perspectives {
                public interface IPerspectiveFor<TModel> { }
                public interface IPerspectiveFor<TModel, TEvent1> : IPerspectiveFor<TModel> { }
            }

            namespace TestNamespace {
                public class TestModel {
                    public Guid Id { get; set; }
                    [JsonIgnore]
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
    await Assert.That(diagnostics).IsEmpty();
  }
}
