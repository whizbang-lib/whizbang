using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Tests for GuidInterceptorGenerator interception of third-party GUID libraries.
/// Verifies that popular GUID generation libraries are intercepted and wrapped
/// with TrackedGuid with appropriate source metadata.
/// </summary>
[Category("Generators")]
[Category("Interceptors")]
[Category("ThirdParty")]
public class ThirdPartyGuidInterceptionTests {
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
  // Marten CombGuid Tests
  // ========================================

  /// <summary>
  /// Test that Marten CombGuidIdGeneration.NewGuid() is intercepted.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_MartenCombGuid_InterceptsAndAddsSourceMartenMetadataAsync() {
    // Arrange - Simulated Marten code structure
    var source = """
            using System;

            // Simulating Marten's CombGuidIdGeneration
            namespace Marten.Schema.Identity {
              public static class CombGuidIdGeneration {
                public static Guid NewGuid() => Guid.CreateVersion7();
              }
            }

            namespace TestApp;

            public class MyService {
              public Guid CreateMartenId() {
                return Marten.Schema.Identity.CombGuidIdGeneration.NewGuid();
              }
            }
            """;

    // Act
    var result = _runGenerator(source);

    // Assert
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "GuidInterceptors.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("InterceptsLocation");
    await Assert.That(generatedSource).Contains("SourceMarten");
  }

  // ========================================
  // Medo.Uuid7 Tests (Direct Usage)
  // ========================================

  /// <summary>
  /// Test that direct Medo.Uuid7.NewUuid7() usage is intercepted.
  /// Note: Internal Whizbang use via TrackedGuid.NewMedo() should NOT be intercepted.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_MedoUuid7Direct_InterceptsAsync() {
    // Arrange - Simulated Medo code structure
    var source = """
            using System;

            // Simulating Medo.Uuid7
            namespace Medo {
              public readonly struct Uuid7 {
                public static Uuid7 NewUuid7() => default;
                public Guid ToGuid() => Guid.Empty;
              }
            }

            namespace TestApp;

            public class MyService {
              public Guid CreateMedoId() {
                return Medo.Uuid7.NewUuid7().ToGuid();
              }
            }
            """;

    // Act
    var result = _runGenerator(source);

    // Assert - Should intercept the NewUuid7() call
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "GuidInterceptors.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("InterceptsLocation");
    await Assert.That(generatedSource).Contains("SourceMedo");
  }

  // ========================================
  // UUIDNext Tests
  // ========================================

  /// <summary>
  /// Test that UUIDNext.Uuid.NewDatabaseFriendly() is intercepted.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_UuidNextDatabaseFriendly_InterceptsAsync() {
    // Arrange - Simulated UUIDNext code structure
    var source = """
            using System;

            // Simulating UUIDNext library
            namespace UUIDNext {
              public enum Database { SqlServer, PostgreSql, MySql }

              public static class Uuid {
                public static Guid NewDatabaseFriendly(Database db) => Guid.CreateVersion7();
                public static Guid NewSequential() => Guid.CreateVersion7();
              }
            }

            namespace TestApp;

            public class MyService {
              public Guid CreateUuidNextId() {
                return UUIDNext.Uuid.NewDatabaseFriendly(UUIDNext.Database.PostgreSql);
              }
            }
            """;

    // Act
    var result = _runGenerator(source);

    // Assert
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "GuidInterceptors.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("InterceptsLocation");
    await Assert.That(generatedSource).Contains("SourceUuidNext");
  }

  /// <summary>
  /// Test that UUIDNext.Uuid.NewSequential() is intercepted.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_UuidNextSequential_InterceptsAsync() {
    // Arrange
    var source = """
            using System;

            namespace UUIDNext {
              public static class Uuid {
                public static Guid NewSequential() => Guid.CreateVersion7();
              }
            }

            namespace TestApp;

            public class MyService {
              public Guid CreateSequentialId() {
                return UUIDNext.Uuid.NewSequential();
              }
            }
            """;

    // Act
    var result = _runGenerator(source);

    // Assert
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "GuidInterceptors.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("SourceUuidNext");
  }

  // ========================================
  // Multiple Libraries in Same File
  // ========================================

  /// <summary>
  /// Test that multiple third-party libraries in the same file are all intercepted.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_MultipleLibraries_AllInterceptedWithCorrectMetadataAsync() {
    // Arrange - Simulated libraries return default to avoid internal Guid calls being intercepted
    var source = """
            using System;

            // Simulated libraries (return default to avoid internal Guid calls)
            namespace Marten.Schema.Identity {
              public static class CombGuidIdGeneration {
                public static Guid NewGuid() => default;
              }
            }

            namespace UUIDNext {
              public static class Uuid {
                public static Guid NewSequential() => default;
              }
            }

            namespace TestApp;

            public class MyService {
              public Guid CreateMartenId() => Marten.Schema.Identity.CombGuidIdGeneration.NewGuid();
              public Guid CreateUuidNextId() => UUIDNext.Uuid.NewSequential();
              public Guid CreateSystemId() => Guid.NewGuid();
            }
            """;

    // Act
    var result = _runGenerator(source);

    // Assert - Should have 3 interceptors with different source metadata
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "GuidInterceptors.g.cs");
    await Assert.That(generatedSource).IsNotNull();

    var interceptCount = generatedSource!.Split("[global::System.Runtime.CompilerServices.InterceptsLocation(").Length - 1;
    await Assert.That(interceptCount).IsEqualTo(3);

    // Each should have appropriate source metadata
    await Assert.That(generatedSource).Contains("SourceMarten");
    await Assert.That(generatedSource).Contains("SourceUuidNext");
    await Assert.That(generatedSource).Contains("SourceMicrosoft");
  }

  // ========================================
  // Suppression Works for Third-Party Too
  // ========================================

  /// <summary>
  /// Test that [SuppressGuidInterception] also suppresses third-party library interception.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_SuppressOnThirdPartyUsage_NoInterceptionAsync() {
    // Arrange - Simulated library returns default to avoid internal Guid calls
    var source = """
            using System;
            using Whizbang.Core;

            namespace Marten.Schema.Identity {
              public static class CombGuidIdGeneration {
                public static Guid NewGuid() => default;
              }
            }

            namespace TestApp;

            public class MyService {
              [SuppressGuidInterception]
              public Guid CreateMartenId() {
                return Marten.Schema.Identity.CombGuidIdGeneration.NewGuid();
              }
            }
            """;

    // Act
    var result = _runGenerator(source);

    // Assert - Should not intercept due to suppression
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "GuidInterceptors.g.cs");

    if (generatedSource != null) {
      await Assert.That(generatedSource).DoesNotContain("CreateMartenId");
    }

    // Should report WHIZ059 suppression diagnostic
    var diagnostics = result.Diagnostics.Where(d => d.Id == "WHIZ059").ToList();
    await Assert.That(diagnostics).Count().IsEqualTo(1);
  }

  // ========================================
  // Version Detection Tests
  // ========================================

  /// <summary>
  /// Test that Marten CombGuid is detected as Version7 (time-ordered).
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_MartenCombGuid_DetectedAsVersion7Async() {
    // Arrange - Simulated library returns default to avoid internal Guid calls
    var source = """
            using System;

            namespace Marten.Schema.Identity {
              public static class CombGuidIdGeneration {
                public static Guid NewGuid() => default;
              }
            }

            namespace TestApp;

            public class MyService {
              public Guid CreateMartenId() {
                return Marten.Schema.Identity.CombGuidIdGeneration.NewGuid();
              }
            }
            """;

    // Act
    var result = _runGenerator(source);

    // Assert - Marten CombGuid should be marked as Version7
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "GuidInterceptors.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("Version7");
  }

  // ========================================
  // Edge Cases
  // ========================================

  /// <summary>
  /// Test that custom classes named similar to third-party libraries are not intercepted.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_SimilarClassName_NotInterceptedAsync() {
    // Arrange - User has their own class with similar name (returns default to avoid internal Guid calls)
    var source = """
            using System;

            namespace MyApp.Schema.Identity {
              // NOT Marten - user's own class
              public static class CombGuidIdGeneration {
                public static Guid NewGuid() => default;
              }
            }

            namespace TestApp;

            public class MyService {
              public Guid CreateId() {
                // This should NOT be intercepted as Marten source
                return MyApp.Schema.Identity.CombGuidIdGeneration.NewGuid();
              }
            }
            """;

    // Act
    var result = _runGenerator(source);

    // Assert - Should NOT have SourceMarten metadata (it's not really Marten)
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "GuidInterceptors.g.cs");

    if (generatedSource != null) {
      await Assert.That(generatedSource).DoesNotContain("SourceMarten");
    }
  }

  /// <summary>
  /// Test that extension methods named "NewGuid" are not intercepted as Guid.NewGuid().
  /// Note: The internal Guid.NewGuid() inside the extension method IS intercepted (correct behavior).
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_GuidExtensionMethod_NotInterceptedAsync() {
    // Arrange - Extension method returns default to avoid internal Guid.NewGuid() being intercepted
    var source = """
            using System;

            namespace TestApp;

            public static class GuidExtensions {
              public static Guid NewGuid(this string prefix) => default;
            }

            public class MyService {
              public Guid CreateId() {
                // This is an extension method, not Guid.NewGuid()
                return "test".NewGuid();
              }
            }
            """;

    // Act
    var result = _runGenerator(source);

    // Assert - Extension method should not be intercepted (no generated file or no interceptors)
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "GuidInterceptors.g.cs");

    if (generatedSource != null) {
      // The extension method call should not be intercepted
      var interceptCount = generatedSource.Split("[global::System.Runtime.CompilerServices.InterceptsLocation(").Length - 1;
      await Assert.That(interceptCount).IsEqualTo(0);
    }
  }
}
