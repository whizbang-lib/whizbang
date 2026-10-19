// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Tests for GuidUsageAnalyzer WHIZ055-057.
/// Verifies detection of incorrect Guid usage patterns.
/// </summary>
[Category("Analyzers")]
public class GuidUsageAnalyzerTests {
  // ========================================
  // WHIZ055: Guid.NewGuid() Detection Tests
  // ========================================

  /// <summary>
  /// Test that Guid.NewGuid() usage is detected and reports WHIZ055 (a warning).
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_GuidNewGuid_ReportsWHIZ055WarningAsync() {
    // Arrange
    const string source = """
            using System;

            namespace TestApp;

            public class MyService {
              public Guid CreateId() {
                return Guid.NewGuid();
              }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<GuidUsageAnalyzer>(source);

    // Assert
    await Assert.That(diagnostics).Count().IsEqualTo(1);
    await Assert.That(diagnostics[0].Id).IsEqualTo("WHIZ055");
    await Assert.That(diagnostics[0].Severity).IsEqualTo(DiagnosticSeverity.Warning);
  }

  /// <summary>
  /// Test that TrackedGuid.New() does not trigger any diagnostics.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_TrackedGuidNew_NoErrorAsync() {
    // Arrange: TrackedGuid lives in Whizbang.Core.ValueObjects. Without that using the call does not bind, and the
    // analyzer has nothing to inspect, so "no diagnostic" would prove nothing (#1431).
    const string source = """
            using Whizbang.Core.ValueObjects;

            namespace TestApp;

            public class MyService {
              public TrackedGuid CreateId() {
                return TrackedGuid.New();
              }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<GuidUsageAnalyzer>(source);

    // Assert
    await Assert.That(_compileErrors(source)).IsEmpty().Because("the call must bind for the analyzer to inspect it");
    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ055" || d.Id == "WHIZ056")).IsEmpty();
  }

  /// <summary>
  /// Test that a [WhizbangId] type's New() does not trigger any diagnostics.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_WhizbangIdNew_NoErrorAsync() {
    // Arrange
    // Arrange: there is no WhizbangId type to call; [WhizbangId] marks an id type the generator gives a New().
    // The id type is written out here because this compilation runs no generators, and the call has to bind for
    // "no diagnostic" to mean anything (#1431).
    const string source = """
            using Whizbang.Core;

            namespace TestApp;

            [WhizbangId]
            public readonly partial struct OrderId {
              public static OrderId New() => default;
            }

            public class MyService {
              public void CreateId() {
                var id = OrderId.New();
              }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<GuidUsageAnalyzer>(source);

    // Assert
    await Assert.That(_compileErrors(source)).IsEmpty().Because("the call must bind for the analyzer to inspect it");
    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ055" || d.Id == "WHIZ056")).IsEmpty();
  }

  /// <summary>
  /// A call that does not bind has no method to recognize, so it is not reported. The WhizbangId test used to
  /// exercise this by accident, calling a type that does not exist (#1431).
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_AnUnboundCall_IsNotReportedAsync() {
    const string source = """
            namespace TestApp;

            public class MyService {
              public void CreateId() {
                var id = Missing.NewGuid();
              }
            }
            """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<GuidUsageAnalyzer>(source);

    await Assert.That(_compileErrors(source)).IsNotEmpty().Because("the call must not bind for this to test the unbound path");
    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ055" || d.Id == "WHIZ056")).IsEmpty();
  }

  private static List<string> _compileErrors(string source) =>
    [.. AnalyzerTestHelper.CreateCompilationWithFrameworkReferences(source).GetDiagnostics()
      .Where(static d => d.Severity == DiagnosticSeverity.Error)
      .Select(static d => d.ToString())];

  // ========================================
  // WHIZ056: Guid.CreateVersion7() Detection Tests
  // ========================================

  /// <summary>
  /// Test that Guid.CreateVersion7() usage is detected and reports WHIZ056 (a warning).
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_GuidCreateVersion7_ReportsWHIZ056WarningAsync() {
    // Arrange
    const string source = """
            using System;

            namespace TestApp;

            public class MyService {
              public Guid CreateV7Id() {
                return Guid.CreateVersion7();
              }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<GuidUsageAnalyzer>(source);

    // Assert
    await Assert.That(diagnostics).Count().IsEqualTo(1);
    await Assert.That(diagnostics[0].Id).IsEqualTo("WHIZ056");
    await Assert.That(diagnostics[0].Severity).IsEqualTo(DiagnosticSeverity.Warning);
  }

  // ========================================
  // Suppression Tests
  // ========================================

  /// <summary>
  /// Test that analyzer can be suppressed with pragma.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_WithPragmaSuppress_NoErrorAsync() {
    // Arrange
    const string source = """
            using System;

            namespace TestApp;

            public class MyService {
            #pragma warning disable WHIZ055
              public Guid CreateId() {
                return Guid.NewGuid();
              }
            #pragma warning restore WHIZ055
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<GuidUsageAnalyzer>(source);

    // Assert - No visible WHIZ055 errors (suppressed)
    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ055" && !d.IsSuppressed)).IsEmpty();
  }

  /// <summary>
  /// Test that multiple Guid.NewGuid() calls are each reported.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_MultipleGuidNewGuid_ReportsMultipleErrorsAsync() {
    // Arrange
    const string source = """
            using System;

            namespace TestApp;

            public class MyService {
              public void DoWork() {
                var id1 = Guid.NewGuid();
                var id2 = Guid.NewGuid();
                var id3 = Guid.NewGuid();
              }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<GuidUsageAnalyzer>(source);

    // Assert
    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ055")).Count().IsEqualTo(3);
  }

  // ========================================
  // Edge Cases
  // ========================================

  /// <summary>
  /// Test that Guid.Parse() does not trigger diagnostics (valid use case).
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_GuidParse_NoErrorAsync() {
    // Arrange
    const string source = """
            using System;

            namespace TestApp;

            public class MyService {
              public Guid ParseId(string input) {
                return Guid.Parse(input);
              }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<GuidUsageAnalyzer>(source);

    // Assert
    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ055" || d.Id == "WHIZ056")).IsEmpty();
  }

  /// <summary>
  /// Test that Guid.Empty does not trigger diagnostics.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_GuidEmpty_NoErrorAsync() {
    // Arrange
    const string source = """
            using System;

            namespace TestApp;

            public class MyService {
              public Guid DefaultId => Guid.Empty;
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<GuidUsageAnalyzer>(source);

    // Assert
    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ055" || d.Id == "WHIZ056")).IsEmpty();
  }
}
