using Whizbang.Migrate.Transformers;

namespace Whizbang.Migrate.Tests.Transformers;

/// <summary>
/// Tests for the GuidToIdProviderTransformer that converts Guid.NewGuid()/Guid.CreateVersion7()
/// to idProvider.NewGuid() calls and injects IWhizbangIdProvider into primary constructors.
/// </summary>
/// <tests>Whizbang.Migrate/Transformers/GuidToIdProviderTransformer.cs:*</tests>
public class GuidToIdProviderTransformerTests {
  // ============================================================
  // G01: Basic Guid.NewGuid() transformation
  // ============================================================

  [Test]
  public async Task TransformAsync_G01_GuidNewGuid_TransformsToIdProviderNewGuidAsync() {
    // Arrange
    var transformer = new GuidToIdProviderTransformer();
    var sourceCode = """
      using System;

      public class OrderService(ILogger logger) {
        public Guid CreateOrder() {
          return Guid.NewGuid();
        }
      }
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "OrderService.cs");

    // Assert
    await Assert.That(result.TransformedCode).Contains("idProvider.NewGuid()");
    await Assert.That(result.TransformedCode).DoesNotContain("Guid.NewGuid()");
    await Assert.That(result.Changes.Any(c => c.ChangeType == ChangeType.MethodCallReplacement)).IsTrue();
  }

  [Test]
  public async Task TransformAsync_G01_GuidCreateVersion7_TransformsToIdProviderNewGuidAsync() {
    // Arrange
    var transformer = new GuidToIdProviderTransformer();
    var sourceCode = """
      using System;

      public class OrderService(ILogger logger) {
        public Guid CreateOrder() {
          return Guid.CreateVersion7();
        }
      }
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "OrderService.cs");

    // Assert
    await Assert.That(result.TransformedCode).Contains("idProvider.NewGuid()");
    await Assert.That(result.TransformedCode).DoesNotContain("Guid.CreateVersion7()");
  }

  [Test]
  public async Task TransformAsync_G01_SystemGuidNewGuid_TransformsToIdProviderNewGuidAsync() {
    // Arrange
    var transformer = new GuidToIdProviderTransformer();
    var sourceCode = """
      public class OrderService(ILogger logger) {
        public System.Guid CreateOrder() {
          return System.Guid.NewGuid();
        }
      }
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "OrderService.cs");

    // Assert
    await Assert.That(result.TransformedCode).Contains("idProvider.NewGuid()");
    await Assert.That(result.TransformedCode).DoesNotContain("System.Guid.NewGuid()");
  }

  [Test]
  public async Task TransformAsync_G01_SystemGuidCreateVersion7_TransformsToIdProviderNewGuidAsync() {
    // Arrange
    var transformer = new GuidToIdProviderTransformer();
    var sourceCode = """
      public class OrderService(ILogger logger) {
        public System.Guid CreateOrder() {
          return System.Guid.CreateVersion7();
        }
      }
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "OrderService.cs");

    // Assert
    await Assert.That(result.TransformedCode).Contains("idProvider.NewGuid()");
    await Assert.That(result.TransformedCode).DoesNotContain("System.Guid.CreateVersion7()");
  }

  // ============================================================
  // Constructor Injection Tests
  // ============================================================

  [Test]
  public async Task TransformAsync_ClassWithPrimaryConstructor_AddsIdProviderParameterAsync() {
    // Arrange
    var transformer = new GuidToIdProviderTransformer();
    var sourceCode = """
      using System;

      public class OrderService(ILogger logger) {
        public Guid CreateOrder() {
          return Guid.NewGuid();
        }
      }
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "OrderService.cs");

    // Assert
    await Assert.That(result.TransformedCode).Contains("IWhizbangIdProvider idProvider");
    await Assert.That(result.Changes.Any(c =>
        c.ChangeType == ChangeType.MethodSignatureChange &&
        c.Description.Contains("IWhizbangIdProvider"))).IsTrue();
  }

  [Test]
  public async Task TransformAsync_RecordWithPrimaryConstructor_AddsIdProviderParameterAsync() {
    // Arrange
    var transformer = new GuidToIdProviderTransformer();
    var sourceCode = """
      using System;

      public record OrderService(ILogger Logger) {
        public Guid CreateOrder() {
          return Guid.NewGuid();
        }
      }
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "OrderService.cs");

    // Assert
    await Assert.That(result.TransformedCode).Contains("IWhizbangIdProvider idProvider");
    await Assert.That(result.Changes.Any(c =>
        c.ChangeType == ChangeType.MethodSignatureChange)).IsTrue();
  }

  [Test]
  public async Task TransformAsync_ClassWithoutPrimaryConstructor_EmitsWarningAsync() {
    // Arrange
    var transformer = new GuidToIdProviderTransformer();
    var sourceCode = """
      using System;

      public class OrderService {
        private readonly ILogger _logger;

        public OrderService(ILogger logger) {
          _logger = logger;
        }

        public Guid CreateOrder() {
          return Guid.NewGuid();
        }
      }
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "OrderService.cs");

    // Assert - should still transform calls but emit warning
    await Assert.That(result.TransformedCode).Contains("idProvider.NewGuid()");
    await Assert.That(result.Warnings.Any(w =>
        w.Contains("OrderService") &&
        w.Contains("no primary constructor"))).IsTrue();
  }

  [Test]
  public async Task TransformAsync_ClassAlreadyHasIdProvider_DoesNotDuplicateAsync() {
    // Arrange
    var transformer = new GuidToIdProviderTransformer();
    var sourceCode = """
      using System;
      using Whizbang.Core;

      public class OrderService(ILogger logger, IWhizbangIdProvider idProvider) {
        public Guid CreateOrder() {
          return Guid.NewGuid();
        }
      }
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "OrderService.cs");

    // Assert - should transform calls but NOT add another IWhizbangIdProvider
    await Assert.That(result.TransformedCode).Contains("idProvider.NewGuid()");
    var idProviderCount = result.TransformedCode.Split("IWhizbangIdProvider").Length - 1;
    await Assert.That(idProviderCount).IsEqualTo(1);
  }

  // ============================================================
  // Using Directive Tests
  // ============================================================

  [Test]
  public async Task TransformAsync_AddsWhizbangCoreUsingDirectiveAsync() {
    // Arrange
    var transformer = new GuidToIdProviderTransformer();
    var sourceCode = """
      using System;

      public class OrderService(ILogger logger) {
        public Guid CreateOrder() {
          return Guid.NewGuid();
        }
      }
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "OrderService.cs");

    // Assert
    await Assert.That(result.TransformedCode).Contains("using Whizbang.Core;");
    await Assert.That(result.Changes.Any(c => c.ChangeType == ChangeType.UsingAdded)).IsTrue();
  }

  [Test]
  public async Task TransformAsync_ExistingWhizbangCoreUsing_DoesNotDuplicateAsync() {
    // Arrange
    var transformer = new GuidToIdProviderTransformer();
    var sourceCode = """
      using System;
      using Whizbang.Core;

      public class OrderService(ILogger logger) {
        public Guid CreateOrder() {
          return Guid.NewGuid();
        }
      }
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "OrderService.cs");

    // Assert
    var usingCount = result.TransformedCode.Split("using Whizbang.Core;").Length - 1;
    await Assert.That(usingCount).IsEqualTo(1);
  }

  // ============================================================
  // No-Op Tests
  // ============================================================

  [Test]
  public async Task TransformAsync_NoGuidGeneration_ReturnsUnchangedAsync() {
    // Arrange
    var transformer = new GuidToIdProviderTransformer();
    var sourceCode = """
      using System;

      public class OrderService(ILogger logger) {
        public string GetMessage() {
          return "Hello";
        }
      }
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "OrderService.cs");

    // Assert
    await Assert.That(result.OriginalCode).IsEqualTo(result.TransformedCode);
    await Assert.That(result.Changes).IsEmpty();
  }

  [Test]
  public async Task TransformAsync_GuidParseNotTransformed_OnlyGenerationCallsAsync() {
    // Arrange
    var transformer = new GuidToIdProviderTransformer();
    var sourceCode = """
      using System;

      public class OrderService(ILogger logger) {
        public Guid ParseId(string id) {
          return Guid.Parse(id);
        }
      }
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "OrderService.cs");

    // Assert - Guid.Parse should NOT be transformed
    await Assert.That(result.TransformedCode).Contains("Guid.Parse(id)");
    await Assert.That(result.OriginalCode).IsEqualTo(result.TransformedCode);
  }

  // ============================================================
  // Multiple Guid Calls Tests
  // ============================================================

  [Test]
  public async Task TransformAsync_MultipleGuidCalls_TransformsAllAsync() {
    // Arrange
    var transformer = new GuidToIdProviderTransformer();
    var sourceCode = """
      using System;

      public class OrderService(ILogger logger) {
        public Guid CreateOrder() {
          var orderId = Guid.NewGuid();
          var correlationId = Guid.CreateVersion7();
          return orderId;
        }
      }
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "OrderService.cs");

    // Assert
    await Assert.That(result.TransformedCode).DoesNotContain("Guid.NewGuid()");
    await Assert.That(result.TransformedCode).DoesNotContain("Guid.CreateVersion7()");
    var idProviderCallCount = result.TransformedCode.Split("idProvider.NewGuid()").Length - 1;
    await Assert.That(idProviderCallCount).IsEqualTo(2);
  }

  // ============================================================
  // Change Tracking Tests
  // ============================================================

  [Test]
  public async Task TransformAsync_TracksAllChangesAsync() {
    // Arrange
    var transformer = new GuidToIdProviderTransformer();
    var sourceCode = """
      using System;

      public class OrderService(ILogger logger) {
        public Guid CreateOrder() {
          return Guid.NewGuid();
        }
      }
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "OrderService.cs");

    // Assert - should have 3 changes: using added, constructor param, method call
    await Assert.That(result.Changes.Any(c => c.ChangeType == ChangeType.UsingAdded)).IsTrue();
    await Assert.That(result.Changes.Any(c => c.ChangeType == ChangeType.MethodSignatureChange)).IsTrue();
    await Assert.That(result.Changes.Any(c => c.ChangeType == ChangeType.MethodCallReplacement)).IsTrue();
  }

  // ============================================================
  // Edge Cases
  // ============================================================

  [Test]
  public async Task TransformAsync_GuidInFieldInitializer_TransformsAsync() {
    // Arrange
    var transformer = new GuidToIdProviderTransformer();
    var sourceCode = """
      using System;

      public class OrderService(ILogger logger) {
        private readonly Guid _instanceId = Guid.NewGuid();

        public Guid GetInstanceId() => _instanceId;
      }
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "OrderService.cs");

    // Assert
    await Assert.That(result.TransformedCode).Contains("idProvider.NewGuid()");
    await Assert.That(result.TransformedCode).DoesNotContain("Guid.NewGuid()");
  }

  [Test]
  public async Task TransformAsync_GuidInLambda_TransformsAsync() {
    // Arrange
    var transformer = new GuidToIdProviderTransformer();
    var sourceCode = """
      using System;
      using System.Collections.Generic;
      using System.Linq;

      public class OrderService(ILogger logger) {
        public List<Guid> CreateBatch(int count) {
          return Enumerable.Range(0, count)
            .Select(_ => Guid.NewGuid())
            .ToList();
        }
      }
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "OrderService.cs");

    // Assert
    await Assert.That(result.TransformedCode).Contains("idProvider.NewGuid()");
    await Assert.That(result.TransformedCode).DoesNotContain("Guid.NewGuid()");
  }

  [Test]
  public async Task TransformAsync_PreservesNamespaceAsync() {
    // Arrange
    var transformer = new GuidToIdProviderTransformer();
    var sourceCode = """
      using System;

      namespace MyApp.Services;

      public class OrderService(ILogger logger) {
        public Guid CreateOrder() {
          return Guid.NewGuid();
        }
      }
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "OrderService.cs");

    // Assert
    await Assert.That(result.TransformedCode).Contains("namespace MyApp.Services;");
  }

  [Test]
  public async Task TransformAsync_EmptyPrimaryConstructor_AddsFirstParameterAsync() {
    // Arrange
    var transformer = new GuidToIdProviderTransformer();
    var sourceCode = """
      using System;

      public class OrderService() {
        public Guid CreateOrder() {
          return Guid.NewGuid();
        }
      }
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "OrderService.cs");

    // Assert
    await Assert.That(result.TransformedCode).Contains("OrderService(IWhizbangIdProvider idProvider)");
    await Assert.That(result.TransformedCode).Contains("idProvider.NewGuid()");
  }

  [Test]
  public async Task TransformAsync_NestedClass_TransformsCorrectlyAsync() {
    // Arrange
    var transformer = new GuidToIdProviderTransformer();
    var sourceCode = """
      using System;

      public static class OrderHandlers {
        public class CreateHandler(ILogger logger) {
          public Guid Handle() {
            return Guid.NewGuid();
          }
        }
      }
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "OrderHandlers.cs");

    // Assert
    await Assert.That(result.TransformedCode).Contains("idProvider.NewGuid()");
    await Assert.That(result.TransformedCode).Contains("IWhizbangIdProvider idProvider");
  }

  [Test]
  public async Task TransformAsync_MultipleClasses_TransformsBothAsync() {
    // Arrange
    var transformer = new GuidToIdProviderTransformer();
    var sourceCode = """
      using System;

      public class OrderService(ILogger logger) {
        public Guid CreateOrder() => Guid.NewGuid();
      }

      public class CustomerService(ILogger logger) {
        public Guid CreateCustomer() => Guid.NewGuid();
      }
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "Services.cs");

    // Assert
    await Assert.That(result.TransformedCode).DoesNotContain("Guid.NewGuid()");
    var idProviderCount = result.TransformedCode.Split("IWhizbangIdProvider idProvider").Length - 1;
    await Assert.That(idProviderCount).IsEqualTo(2);
  }
}
