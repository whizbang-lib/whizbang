// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;

namespace Whizbang.RepoAnalyzers.Tests;

/// <summary>
/// WHZ0005: a JsonDocument created and dropped in one expression is never disposed, so the buffers it
/// rented from the shared pool are never returned. The rule stands in for the SDK's CA2026 on builds
/// whose SDK does not ship it yet.
/// </summary>
public class UndisposedJsonDocumentAnalyzerTests {
  private const string USINGS = """
    using System.IO;
    using System.Text.Json;
    using System.Threading.Tasks;

    """;

  [Test]
  public async Task Descriptor_IsTheRepositoryRuleReportedAsAnErrorAsync() {
    var descriptor = new UndisposedJsonDocumentAnalyzer().SupportedDiagnostics.Single();

    await Assert.That(descriptor.Id).IsEqualTo("WHZ0005");
    await Assert.That(descriptor.DefaultSeverity).IsEqualTo(DiagnosticSeverity.Error);
    await Assert.That(descriptor.IsEnabledByDefault).IsTrue();
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Parse_RootElement_ReportsAsync() {
    var diagnostics = await _analyzeAsync("""
      static class C {
        static JsonElement M() => JsonDocument.Parse("1").RootElement;
      }
      """);

    await Assert.That(diagnostics).Count().IsEqualTo(1);
    await Assert.That(diagnostics[0].Id).IsEqualTo("WHZ0005");
    await Assert.That(diagnostics[0].GetMessage(System.Globalization.CultureInfo.InvariantCulture))
      .Contains("JsonDocument.Parse");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Parse_RootElement_Clone_ReportsBecauseTheDocumentIsStillDroppedAsync() {
    var diagnostics = await _analyzeAsync("""
      static class C {
        static JsonElement M() => JsonDocument.Parse("1").RootElement.Clone();
      }
      """);

    await Assert.That(diagnostics).Count().IsEqualTo(1);
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Parse_RootElement_SplitAcrossLines_ReportsAsync() {
    var diagnostics = await _analyzeAsync("""
      static class C {
        static JsonElement M(double seconds) => JsonDocument
          .Parse(seconds.ToString("R", System.Globalization.CultureInfo.InvariantCulture))
          .RootElement.Clone();
      }
      """);

    await Assert.That(diagnostics).Count().IsEqualTo(1);
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task ParseValue_RootElement_ReportsAsync() {
    var diagnostics = await _analyzeAsync("""
      static class C {
        static JsonElement M(ref Utf8JsonReader reader) => JsonDocument.ParseValue(ref reader).RootElement;
      }
      """);

    await Assert.That(diagnostics).Count().IsEqualTo(1);
    await Assert.That(diagnostics[0].GetMessage(System.Globalization.CultureInfo.InvariantCulture))
      .Contains("JsonDocument.ParseValue");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task AwaitedParseAsync_RootElement_ReportsAsync() {
    var diagnostics = await _analyzeAsync("""
      static class C {
        static async Task<JsonElement> M(Stream stream) => (await JsonDocument.ParseAsync(stream)).RootElement;
      }
      """);

    await Assert.That(diagnostics).Count().IsEqualTo(1);
    await Assert.That(diagnostics[0].GetMessage(System.Globalization.CultureInfo.InvariantCulture))
      .Contains("JsonDocument.ParseAsync");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task SerializeToDocument_RootElement_ReportsAsync() {
    var diagnostics = await _analyzeAsync("""
      static class C {
        static JsonElement M(JsonSerializerOptions options) =>
          JsonSerializer.SerializeToDocument(1, typeof(int), options).RootElement;
      }
      """);

    await Assert.That(diagnostics).Count().IsEqualTo(1);
    await Assert.That(diagnostics[0].GetMessage(System.Globalization.CultureInfo.InvariantCulture))
      .Contains("JsonSerializer.SerializeToDocument");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task DisposedDocument_RootElement_DoesNotReportAsync() {
    var diagnostics = await _analyzeAsync("""
      static class C {
        static JsonElement M() {
          using var document = JsonDocument.Parse("1");
          return document.RootElement.Clone();
        }
      }
      """);

    await Assert.That(diagnostics).IsEmpty();
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task JsonElementParse_DoesNotReportAsync() {
    var diagnostics = await _analyzeAsync("""
      static class C {
        static JsonElement M() => JsonElement.Parse("1");
      }
      """);

    await Assert.That(diagnostics).IsEmpty();
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task DocumentReturnedByAnotherMethod_DoesNotReportAsync() {
    // A document handed out by other code may be owned and disposed by that code.
    var diagnostics = await _analyzeAsync("""
      static class C {
        static readonly JsonDocument _cached = JsonDocument.Parse("1");
        static JsonDocument Get() => _cached;
        static JsonElement M() => Get().RootElement;
      }
      """);

    await Assert.That(diagnostics).IsEmpty();
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task DeserializeToDocument_RootElement_ReportsAsync() {
    var diagnostics = await _analyzeAsync("""
      static class C {
        static JsonElement M(JsonSerializerOptions options) =>
          JsonSerializer.Deserialize<JsonDocument>("1", options)!.RootElement;
      }
      """);

    await Assert.That(diagnostics).Count().IsEqualTo(1);
    await Assert.That(diagnostics[0].GetMessage(System.Globalization.CultureInfo.InvariantCulture))
      .Contains("JsonSerializer.Deserialize");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task LookalikeType_WithRootElementAndParse_DoesNotReportAsync() {
    var diagnostics = await _analyzeAsync("""
      sealed class Document {
        public int RootElement => 1;
        public static Document Parse(string json) => new();
      }
      static class C {
        static int M() => Document.Parse("1").RootElement;
      }
      """);

    await Assert.That(diagnostics).IsEmpty();
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task CompilationWithoutSystemTextJson_DoesNotReportAsync() {
    const string source = """
      sealed class JsonDocument {
        public int RootElement => 1;
        public static JsonDocument Parse(string json) => new();
      }
      static class C {
        static int M() => JsonDocument.Parse("1").RootElement;
      }
      """;
    var compilationErrors = RepoAnalyzerTestHelper.CreateCompilation(source, withJsonReference: false)
      .GetDiagnostics()
      .Where(d => d.Severity == DiagnosticSeverity.Error)
      .ToList();

    var diagnostics = await RepoAnalyzerTestHelper.GetDiagnosticsAsync<UndisposedJsonDocumentAnalyzer>(
      source, withJsonReference: false);

    await Assert.That(compilationErrors).IsEmpty();
    await Assert.That(diagnostics).IsEmpty();
  }

  /// <summary>
  /// Runs the analyzer, first proving the snippet compiles: a snippet that does not bind produces no
  /// diagnostic for the wrong reason.
  /// </summary>
  [RequiresAssemblyFiles()]
  private static async Task<IReadOnlyList<Diagnostic>> _analyzeAsync(string body) {
    var source = USINGS + body;
    var compilationErrors = RepoAnalyzerTestHelper.CreateCompilation(source)
      .GetDiagnostics()
      .Where(d => d.Severity == DiagnosticSeverity.Error)
      .Select(d => d.ToString())
      .ToList();
    await Assert.That(compilationErrors).IsEmpty();

    return await RepoAnalyzerTestHelper.GetDiagnosticsAsync<UndisposedJsonDocumentAnalyzer>(source);
  }
}
