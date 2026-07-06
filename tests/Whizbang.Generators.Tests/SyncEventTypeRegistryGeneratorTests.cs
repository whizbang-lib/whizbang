using System.Diagnostics.CodeAnalysis;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Tests for SyncEventTypeRegistryGenerator.
/// Verifies discovery of [AwaitPerspectiveSync] attributes and generation of the
/// SyncEventTypeRegistry auto-registration module initializer.
/// </summary>
[Category("SourceGenerators")]
public class SyncEventTypeRegistryGeneratorTests {
  private const string GENERATED_FILE_NAME = "SyncEventTypeRegistry.g.cs";
  private const string REGISTER_CALL = "global::Whizbang.Core.Perspectives.Sync.SyncEventTypeRegistrations.Register(";

  /// <summary>
  /// Counts Register(...) invocations in the generated source.
  /// </summary>
  private static int _countRegistrations(string generatedSource) {
    return generatedSource.Split(REGISTER_CALL).Length - 1;
  }

  // ========================================
  // Happy Path
  // ========================================

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_SingleAttributeSingleEvent_GeneratesRegistrationAsync() {
    // Arrange
    const string source = """
        using System;
        using Whizbang.Core.Perspectives.Sync;

        namespace TestApp {
          public class OrderCreatedEvent {
            public string OrderId { get; set; } = "";
          }

          public class OrderPerspective {
          }

          [AwaitPerspectiveSync(typeof(OrderPerspective), EventTypes = new[] { typeof(OrderCreatedEvent) })]
          public class NotificationReceptor {
          }
        }
        """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<SyncEventTypeRegistryGenerator>(source);

    // Assert
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, GENERATED_FILE_NAME);
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).Contains("Register(typeof(global::TestApp.OrderCreatedEvent), \"TestApp.OrderPerspective\")");
    await Assert.That(generatedSource).Contains("[ModuleInitializer]");
    await Assert.That(generatedSource).Contains("1 event type(s) mapped to 1 perspective(s)");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_UsesAssemblyNameForNamespaceAsync() {
    // Arrange
    const string source = """
        using System;
        using Whizbang.Core.Perspectives.Sync;

        namespace TestApp {
          public class SomeEvent {
          }

          public class SomePerspective {
          }

          [AwaitPerspectiveSync(typeof(SomePerspective), EventTypes = new[] { typeof(SomeEvent) })]
          public class SomeReceptor {
          }
        }
        """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<SyncEventTypeRegistryGenerator>(source);

    // Assert - Namespace derives from assembly name (helper uses "TestAssembly")
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, GENERATED_FILE_NAME);
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).Contains("namespace TestAssembly.Generated;");
  }

  // ========================================
  // Empty / No-Match Input
  // ========================================

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_NoAttributedClasses_GeneratesEmptyRegistryAsync() {
    // Arrange - no [AwaitPerspectiveSync] anywhere
    const string source = """
        namespace TestApp {
          public class PlainClass {
            public void DoWork() { }
          }
        }
        """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<SyncEventTypeRegistryGenerator>(source);

    // Assert - Registry file still generated, with zero registrations
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, GENERATED_FILE_NAME);
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).Contains("0 event type(s) mapped to 0 perspective(s)");
    await Assert.That(generatedSource).DoesNotContain("SyncEventTypeRegistrations.Register(");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_OtherAttributeOnClass_GeneratesEmptyRegistryAsync() {
    // Arrange - class has attributes, but none is [AwaitPerspectiveSync]
    const string source = """
        using System;

        namespace TestApp {
          [Obsolete("legacy")]
          public class LegacyClass {
          }
        }
        """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<SyncEventTypeRegistryGenerator>(source);

    // Assert
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, GENERATED_FILE_NAME);
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).Contains("0 event type(s) mapped to 0 perspective(s)");
    await Assert.That(generatedSource).DoesNotContain("SyncEventTypeRegistrations.Register(");
  }

  // ========================================
  // Multiple Events / Perspectives
  // ========================================

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_MultipleEventTypesInOneAttribute_RegistersEachAsync() {
    // Arrange
    const string source = """
        using System;
        using Whizbang.Core.Perspectives.Sync;

        namespace TestApp {
          public class OrderCreatedEvent {
          }

          public class OrderShippedEvent {
          }

          public class OrderPerspective {
          }

          [AwaitPerspectiveSync(typeof(OrderPerspective), EventTypes = new[] { typeof(OrderCreatedEvent), typeof(OrderShippedEvent) })]
          public class OrderReceptor {
          }
        }
        """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<SyncEventTypeRegistryGenerator>(source);

    // Assert - two event types, one perspective
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, GENERATED_FILE_NAME);
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).Contains("Register(typeof(global::TestApp.OrderCreatedEvent), \"TestApp.OrderPerspective\")");
    await Assert.That(generatedSource).Contains("Register(typeof(global::TestApp.OrderShippedEvent), \"TestApp.OrderPerspective\")");
    await Assert.That(generatedSource).Contains("2 event type(s) mapped to 1 perspective(s)");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_MultipleAttributesOnOneClass_RegistersEachPerspectiveAsync() {
    // Arrange - [AwaitPerspectiveSync] has AllowMultiple = true
    const string source = """
        using System;
        using Whizbang.Core.Perspectives.Sync;

        namespace TestApp {
          public class OrderCreatedEvent {
          }

          public class OrderPerspective {
          }

          public class AuditPerspective {
          }

          [AwaitPerspectiveSync(typeof(OrderPerspective), EventTypes = new[] { typeof(OrderCreatedEvent) })]
          [AwaitPerspectiveSync(typeof(AuditPerspective), EventTypes = new[] { typeof(OrderCreatedEvent) })]
          public class OrderReceptor {
          }
        }
        """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<SyncEventTypeRegistryGenerator>(source);

    // Assert - one event type mapped to two perspectives
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, GENERATED_FILE_NAME);
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).Contains("\"TestApp.OrderPerspective\"");
    await Assert.That(generatedSource).Contains("\"TestApp.AuditPerspective\"");
    await Assert.That(generatedSource).Contains("1 event type(s) mapped to 2 perspective(s)");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_MultipleClassesDifferentEvents_RegistersAllAsync() {
    // Arrange - separate receptor classes each map their own event
    const string source = """
        using System;
        using Whizbang.Core.Perspectives.Sync;

        namespace TestApp {
          public class OrderCreatedEvent {
          }

          public class InvoiceIssuedEvent {
          }

          public class OrderPerspective {
          }

          public class InvoicePerspective {
          }

          [AwaitPerspectiveSync(typeof(OrderPerspective), EventTypes = new[] { typeof(OrderCreatedEvent) })]
          public class OrderReceptor {
          }

          [AwaitPerspectiveSync(typeof(InvoicePerspective), EventTypes = new[] { typeof(InvoiceIssuedEvent) })]
          public class InvoiceReceptor {
          }
        }
        """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<SyncEventTypeRegistryGenerator>(source);

    // Assert
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, GENERATED_FILE_NAME);
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).Contains("Register(typeof(global::TestApp.OrderCreatedEvent), \"TestApp.OrderPerspective\")");
    await Assert.That(generatedSource).Contains("Register(typeof(global::TestApp.InvoiceIssuedEvent), \"TestApp.InvoicePerspective\")");
    await Assert.That(generatedSource).Contains("2 event type(s) mapped to 2 perspective(s)");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_DuplicateMappingAcrossClasses_DeduplicatesAsync() {
    // Arrange - two receptors declare the exact same event -> perspective mapping
    const string source = """
        using System;
        using Whizbang.Core.Perspectives.Sync;

        namespace TestApp {
          public class OrderCreatedEvent {
          }

          public class OrderPerspective {
          }

          [AwaitPerspectiveSync(typeof(OrderPerspective), EventTypes = new[] { typeof(OrderCreatedEvent) })]
          public class FirstReceptor {
          }

          [AwaitPerspectiveSync(typeof(OrderPerspective), EventTypes = new[] { typeof(OrderCreatedEvent) })]
          public class SecondReceptor {
          }
        }
        """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<SyncEventTypeRegistryGenerator>(source);

    // Assert - HashSet deduplication yields a single registration
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, GENERATED_FILE_NAME);
    await Assert.That(generatedSource).IsNotNull();
    var registrationCount = _countRegistrations(generatedSource!);
    await Assert.That(registrationCount).IsEqualTo(1);
    await Assert.That(generatedSource).Contains("1 event type(s) mapped to 1 perspective(s)");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_SameEventDifferentPerspectives_GroupsUnderOneEventAsync() {
    // Arrange - same event tracked by two different perspectives (separate classes)
    const string source = """
        using System;
        using Whizbang.Core.Perspectives.Sync;

        namespace TestApp {
          public class OrderCreatedEvent {
          }

          public class OrderPerspective {
          }

          public class ReportingPerspective {
          }

          [AwaitPerspectiveSync(typeof(OrderPerspective), EventTypes = new[] { typeof(OrderCreatedEvent) })]
          public class OrderReceptor {
          }

          [AwaitPerspectiveSync(typeof(ReportingPerspective), EventTypes = new[] { typeof(OrderCreatedEvent) })]
          public class ReportingReceptor {
          }
        }
        """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<SyncEventTypeRegistryGenerator>(source);

    // Assert - one event type, two perspectives, two Register calls
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, GENERATED_FILE_NAME);
    await Assert.That(generatedSource).IsNotNull();
    var registrationCount = _countRegistrations(generatedSource!);
    await Assert.That(registrationCount).IsEqualTo(2);
    await Assert.That(generatedSource).Contains("1 event type(s) mapped to 2 perspective(s)");
  }

  // ========================================
  // Namespaces and Name Handling
  // ========================================

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_TypesInDifferentNamespaces_UsesFullyQualifiedNamesAsync() {
    // Arrange - event and perspective live in different namespaces
    const string source = """
        using System;
        using Whizbang.Core.Perspectives.Sync;

        namespace TestApp.Events {
          public class ItemAddedEvent {
          }
        }

        namespace TestApp.Perspectives {
          public class CartPerspective {
          }
        }

        namespace TestApp.Receptors {
          [AwaitPerspectiveSync(typeof(TestApp.Perspectives.CartPerspective), EventTypes = new[] { typeof(TestApp.Events.ItemAddedEvent) })]
          public class CartReceptor {
          }
        }
        """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<SyncEventTypeRegistryGenerator>(source);

    // Assert
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, GENERATED_FILE_NAME);
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).Contains("typeof(global::TestApp.Events.ItemAddedEvent)");
    await Assert.That(generatedSource).Contains("\"TestApp.Perspectives.CartPerspective\"");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_NestedPerspectiveType_UsesClrPlusSeparatorAsync() {
    // Arrange - perspective is a nested type; CLR name uses '+' separator
    const string source = """
        using System;
        using Whizbang.Core.Perspectives.Sync;

        namespace TestApp {
          public class OrderCreatedEvent {
          }

          public class Order {
            public class Projection {
            }
          }

          [AwaitPerspectiveSync(typeof(Order.Projection), EventTypes = new[] { typeof(OrderCreatedEvent) })]
          public class OrderReceptor {
          }
        }
        """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<SyncEventTypeRegistryGenerator>(source);

    // Assert
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, GENERATED_FILE_NAME);
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).Contains("\"TestApp.Order+Projection\"");
  }

  // ========================================
  // Malformed Attribute Edge Cases
  // ========================================

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_AttributeWithoutConstructorArgs_SkipsMappingAsync() {
    // Arrange - missing required perspectiveType argument (compile error in user code,
    // but the generator must handle the malformed attribute gracefully)
    const string source = """
        using System;
        using Whizbang.Core.Perspectives.Sync;

        namespace TestApp {
          [AwaitPerspectiveSync]
          public class BrokenReceptor {
          }
        }
        """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<SyncEventTypeRegistryGenerator>(source);

    // Assert - no registrations from the malformed attribute
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, GENERATED_FILE_NAME);
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).DoesNotContain("SyncEventTypeRegistrations.Register(");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_NullPerspectiveType_SkipsMappingAsync() {
    // Arrange - null perspective type constructor argument
    const string source = """
        using System;
        using Whizbang.Core.Perspectives.Sync;

        namespace TestApp {
          public class OrderCreatedEvent {
          }

          [AwaitPerspectiveSync(null, EventTypes = new[] { typeof(OrderCreatedEvent) })]
          public class NullPerspectiveReceptor {
          }
        }
        """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<SyncEventTypeRegistryGenerator>(source);

    // Assert
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, GENERATED_FILE_NAME);
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).DoesNotContain("SyncEventTypeRegistrations.Register(");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_NoEventTypesNamedArgument_SkipsMappingAsync() {
    // Arrange - EventTypes named argument omitted entirely
    const string source = """
        using System;
        using Whizbang.Core.Perspectives.Sync;

        namespace TestApp {
          public class OrderPerspective {
          }

          [AwaitPerspectiveSync(typeof(OrderPerspective))]
          public class NoEventsReceptor {
          }
        }
        """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<SyncEventTypeRegistryGenerator>(source);

    // Assert
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, GENERATED_FILE_NAME);
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).DoesNotContain("SyncEventTypeRegistrations.Register(");
    await Assert.That(generatedSource).Contains("0 event type(s) mapped to 0 perspective(s)");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_NullEventTypes_SkipsMappingAsync() {
    // Arrange - EventTypes explicitly set to null
    const string source = """
        using System;
        using Whizbang.Core.Perspectives.Sync;

        namespace TestApp {
          public class OrderPerspective {
          }

          [AwaitPerspectiveSync(typeof(OrderPerspective), EventTypes = null)]
          public class NullEventsReceptor {
          }
        }
        """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<SyncEventTypeRegistryGenerator>(source);

    // Assert
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, GENERATED_FILE_NAME);
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).DoesNotContain("SyncEventTypeRegistrations.Register(");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_ArrayTypeInEventTypes_SkipsNonNamedTypeAsync() {
    // Arrange - typeof(int[]) resolves to an IArrayTypeSymbol, not INamedTypeSymbol;
    // it must be skipped while the valid entry is still registered
    const string source = """
        using System;
        using Whizbang.Core.Perspectives.Sync;

        namespace TestApp {
          public class OrderCreatedEvent {
          }

          public class OrderPerspective {
          }

          [AwaitPerspectiveSync(typeof(OrderPerspective), EventTypes = new[] { typeof(OrderCreatedEvent), typeof(int[]) })]
          public class MixedReceptor {
          }
        }
        """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<SyncEventTypeRegistryGenerator>(source);

    // Assert - only the named type is registered
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, GENERATED_FILE_NAME);
    await Assert.That(generatedSource).IsNotNull();
    var registrationCount = _countRegistrations(generatedSource!);
    await Assert.That(registrationCount).IsEqualTo(1);
    await Assert.That(generatedSource).Contains("typeof(global::TestApp.OrderCreatedEvent)");
  }
}
