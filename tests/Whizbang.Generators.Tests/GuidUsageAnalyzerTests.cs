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
  /// Test that Guid.NewGuid() usage is detected and reports WHIZ055 error.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_GuidNewGuid_ReportsWHIZ055ErrorAsync() {
    // Arrange
    var source = """
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
  /// Test that TrackedGuid.NewMedo() does not trigger any diagnostics.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_TrackedGuidNewMedo_NoErrorAsync() {
    // Arrange
    var source = """
            using Whizbang.Core;

            namespace TestApp;

            public class MyService {
              public TrackedGuid CreateId() {
                return TrackedGuid.NewMedo();
              }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<GuidUsageAnalyzer>(source);

    // Assert
    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ055" || d.Id == "WHIZ056")).IsEmpty();
  }

  /// <summary>
  /// Test that WhizbangId.New() does not trigger any diagnostics.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_WhizbangIdNew_NoErrorAsync() {
    // Arrange
    var source = """
            using Whizbang.Core;

            namespace TestApp;

            public class MyService {
              public void CreateId() {
                var id = WhizbangId.New();
              }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<GuidUsageAnalyzer>(source);

    // Assert
    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ055" || d.Id == "WHIZ056")).IsEmpty();
  }

  // ========================================
  // WHIZ056: Guid.CreateVersion7() Detection Tests
  // ========================================

  /// <summary>
  /// Test that Guid.CreateVersion7() usage is detected and reports WHIZ056 error.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_GuidCreateVersion7_ReportsWHIZ056ErrorAsync() {
    // Arrange
    var source = """
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
    var source = """
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
    var source = """
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
    var source = """
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
    var source = """
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
