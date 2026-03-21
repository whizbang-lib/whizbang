using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Tests for GuidInterceptorGenerator - compile-time interception of Guid creation.
/// Verifies that Guid.NewGuid() and Guid.CreateVersion7() calls are intercepted
/// and wrapped with TrackedGuid for metadata tracking.
/// </summary>
[Category("Generators")]
[Category("Interceptors")]
public class GuidInterceptorGeneratorTests {
  /// <summary>
  /// Options to enable GUID interception for tests.
  /// </summary>
  private static readonly Dictionary<string, string> _interceptionEnabledOptions = new() {
    ["build_property.WhizbangGuidInterceptionEnabled"] = "true"
  };

  /// <summary>
  /// Runs the GuidInterceptorGenerator with interception enabled.
  /// </summary>
  private static GeneratorDriverRunResult _runGenerator(string source) =>
      GeneratorTestHelper.RunGenerator<GuidInterceptorGenerator>(source, _interceptionEnabledOptions);

  // ========================================
  // Basic Interception Tests
  // ========================================

  /// <summary>
  /// Test that generator produces interceptor file for Guid.NewGuid() call.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_GuidNewGuid_GeneratesInterceptorAsync() {
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
    var result = _runGenerator(source);

    // Assert - Should generate an interceptor file
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "GuidInterceptors.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).Contains("InterceptsLocation");
    await Assert.That(generatedSource).Contains("TrackedGuid");
  }

  /// <summary>
  /// Test that generator produces interceptor file for Guid.CreateVersion7() call.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_GuidCreateVersion7_GeneratesInterceptorAsync() {
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
    var result = _runGenerator(source);

    // Assert - Should generate an interceptor file
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "GuidInterceptors.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).Contains("InterceptsLocation");
    await Assert.That(generatedSource).Contains("TrackedGuid");
    await Assert.That(generatedSource).Contains("Version7");
  }

  /// <summary>
  /// Test that multiple Guid creation calls generate multiple interceptors.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_MultipleGuidCalls_GeneratesMultipleInterceptorsAsync() {
    // Arrange
    const string source = """
            using System;

            namespace TestApp;

            public class MyService {
              public void DoWork() {
                var id1 = Guid.NewGuid();
                var id2 = Guid.NewGuid();
                var id3 = Guid.CreateVersion7();
              }
            }
            """;

    // Act
    var result = _runGenerator(source);

    // Assert - Should have 3 interceptor methods
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "GuidInterceptors.g.cs");
    await Assert.That(generatedSource).IsNotNull();

    // Count occurrences of InterceptsLocation attribute usage (not the class definition)
    var interceptCount = generatedSource!.Split("[global::System.Runtime.CompilerServices.InterceptsLocation(").Length - 1;
    await Assert.That(interceptCount).IsEqualTo(3);
  }

  // ========================================
  // Suppression Tests
  // ========================================

  /// <summary>
  /// Test that [SuppressGuidInterception] on method prevents interception.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_SuppressOnMethod_NoInterceptionAsync() {
    // Arrange
    const string source = """
            using System;
            using Whizbang.Core;

            namespace TestApp;

            public class MyService {
              [SuppressGuidInterception]
              public Guid CreateId() {
                return Guid.NewGuid();
              }
            }
            """;

    // Act
    var result = _runGenerator(source);

    // Assert - Should not generate interceptor for suppressed method
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "GuidInterceptors.g.cs");

    // Either no file generated, or file exists but doesn't intercept this call
    if (generatedSource != null) {
      // If file exists, verify it doesn't intercept the suppressed location
      await Assert.That(generatedSource).DoesNotContain("CreateId");
    }
  }

  /// <summary>
  /// Test that [SuppressGuidInterception] on class prevents interception for all methods.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_SuppressOnClass_NoInterceptionAsync() {
    // Arrange
    const string source = """
            using System;
            using Whizbang.Core;

            namespace TestApp;

            [SuppressGuidInterception]
            public class MyService {
              public Guid CreateId() {
                return Guid.NewGuid();
              }

              public Guid CreateV7Id() {
                return Guid.CreateVersion7();
              }
            }
            """;

    // Act
    var result = _runGenerator(source);

    // Assert - No interceptors for suppressed class
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "GuidInterceptors.g.cs");

    if (generatedSource != null) {
      await Assert.That(generatedSource).DoesNotContain("MyService");
    }
  }

  // ========================================
  // Generated Code Quality Tests
  // ========================================

  /// <summary>
  /// Test that generated code uses fully qualified names (no using statements needed).
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_GeneratesFullyQualifiedNamesAsync() {
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
    var result = _runGenerator(source);

    // Assert
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "GuidInterceptors.g.cs");
    await Assert.That(generatedSource).IsNotNull();

    // Should use global:: for all type references
    await Assert.That(generatedSource).Contains("global::System.Guid");
    await Assert.That(generatedSource).Contains("global::Whizbang.Core.ValueObjects.TrackedGuid");
    await Assert.That(generatedSource).Contains("global::Whizbang.Core.ValueObjects.GuidMetadata");
  }

  /// <summary>
  /// Test that generated code includes correct metadata for v4 GUIDs.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_NewGuid_IncludesV4MetadataAsync() {
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
    var result = _runGenerator(source);

    // Assert
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "GuidInterceptors.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).Contains("Version4");
    await Assert.That(generatedSource).Contains("SourceMicrosoft");
  }

  /// <summary>
  /// Test that generated code includes correct metadata for v7 GUIDs.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_CreateVersion7_IncludesV7MetadataAsync() {
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
    var result = _runGenerator(source);

    // Assert
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "GuidInterceptors.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).Contains("Version7");
    await Assert.That(generatedSource).Contains("SourceMicrosoft");
  }

  // ========================================
  // Diagnostic Tests
  // ========================================

  /// <summary>
  /// Test that WHIZ058 diagnostic is reported for intercepted calls.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_InterceptedCall_ReportsWHIZ058DiagnosticAsync() {
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
    var result = _runGenerator(source);

    // Assert - Should report WHIZ058 info diagnostic
    var diagnostics = result.Diagnostics.Where(d => d.Id == "WHIZ058").ToList();
    await Assert.That(diagnostics).Count().IsEqualTo(1);
    await Assert.That(diagnostics[0].Severity).IsEqualTo(DiagnosticSeverity.Info);
  }

  /// <summary>
  /// Test that WHIZ059 diagnostic is reported for suppressed calls.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_SuppressedCall_ReportsWHIZ059DiagnosticAsync() {
    // Arrange
    const string source = """
            using System;
            using Whizbang.Core;

            namespace TestApp;

            public class MyService {
              [SuppressGuidInterception]
              public Guid CreateId() {
                return Guid.NewGuid();
              }
            }
            """;

    // Act
    var result = _runGenerator(source);

    // Assert - Should report WHIZ059 info diagnostic
    var diagnostics = result.Diagnostics.Where(d => d.Id == "WHIZ059").ToList();
    await Assert.That(diagnostics).Count().IsEqualTo(1);
    await Assert.That(diagnostics[0].Severity).IsEqualTo(DiagnosticSeverity.Info);
  }

  // ========================================
  // Edge Cases
  // ========================================

  /// <summary>
  /// Test that Guid.Empty is NOT intercepted.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_GuidEmpty_NotInterceptedAsync() {
    // Arrange
    const string source = """
            using System;

            namespace TestApp;

            public class MyService {
              public Guid DefaultId => Guid.Empty;
            }
            """;

    // Act
    var result = _runGenerator(source);

    // Assert - Should not generate any interceptors
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "GuidInterceptors.g.cs");

    // Either no file or empty interceptor file
    if (generatedSource != null) {
      await Assert.That(generatedSource).DoesNotContain("InterceptsLocation");
    }
  }

  /// <summary>
  /// Test that Guid.Parse() is NOT intercepted.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_GuidParse_NotInterceptedAsync() {
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
    var result = _runGenerator(source);

    // Assert - Should not generate interceptors for Parse
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "GuidInterceptors.g.cs");

    if (generatedSource != null) {
      await Assert.That(generatedSource).DoesNotContain("InterceptsLocation");
    }
  }

  /// <summary>
  /// Test interception works in nested classes.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_NestedClass_InterceptsCorrectlyAsync() {
    // Arrange
    const string source = """
            using System;

            namespace TestApp;

            public class OuterClass {
              public class InnerClass {
                public Guid CreateId() {
                  return Guid.NewGuid();
                }
              }
            }
            """;

    // Act
    var result = _runGenerator(source);

    // Assert
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "GuidInterceptors.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).Contains("InterceptsLocation");
  }

  /// <summary>
  /// Test interception in lambda expressions.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_LambdaExpression_InterceptsCorrectlyAsync() {
    // Arrange
    const string source = """
            using System;
            using System.Linq;

            namespace TestApp;

            public class MyService {
              public Guid[] CreateIds(int count) {
                return Enumerable.Range(0, count)
                  .Select(_ => Guid.NewGuid())
                  .ToArray();
              }
            }
            """;

    // Act
    var result = _runGenerator(source);

    // Assert
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "GuidInterceptors.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).Contains("InterceptsLocation");
  }

  /// <summary>
  /// Test interception in async methods.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_AsyncMethod_InterceptsCorrectlyAsync() {
    // Arrange
    const string source = """
            using System;
            using System.Threading.Tasks;

            namespace TestApp;

            public class MyService {
              public async Task<Guid> CreateIdAsync() {
                await Task.Delay(1);
                return Guid.NewGuid();
              }
            }
            """;

    // Act
    var result = _runGenerator(source);

    // Assert
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "GuidInterceptors.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).Contains("InterceptsLocation");
  }

  // ========================================
  // No Source Code Tests
  // ========================================

  /// <summary>
  /// Test that empty source produces no output.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_EmptySource_NoOutputAsync() {
    // Arrange
    const string source = """
            namespace TestApp;

            public class MyService {
            }
            """;

    // Act
    var result = _runGenerator(source);

    // Assert
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "GuidInterceptors.g.cs");

    // Either no file or file without interceptors
    if (generatedSource != null) {
      await Assert.That(generatedSource).DoesNotContain("InterceptsLocation");
    }
  }
}
