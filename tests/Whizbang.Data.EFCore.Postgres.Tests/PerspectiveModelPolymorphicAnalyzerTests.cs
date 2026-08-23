using System.Globalization;
using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.EFCore.Postgres.Generators;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Tests for <see cref="PerspectiveModelPolymorphicAnalyzer"/>.
/// Verifies that abstract-class and [JsonPolymorphic] typed properties in perspective
/// models are detected and reported as WHIZ811, and that non-polymorphic, ignored,
/// static, and non-perspective properties are not flagged.
/// </summary>
[Category("Unit")]
[Category("Shard1")]
public class PerspectiveModelPolymorphicAnalyzerTests {
  /// <summary>
  /// Verifies that a perspective model with an abstract-class property triggers WHIZ811 (Info).
  /// </summary>
  [Test]
  public async Task PerspectiveModel_WithAbstractClassProperty_ReportsWHIZ811Async() {
    // Arrange
    const string source = """
            using System;

            namespace Whizbang.Core.Perspectives {
                public interface IPerspectiveFor<TModel> { }
                public interface IPerspectiveFor<TModel, TEvent1> : IPerspectiveFor<TModel> { }
            }

            namespace TestNamespace {
                public abstract class PaymentMethod {
                    public string Name { get; set; } = string.Empty;
                }

                public class TestModel {
                    public Guid Id { get; set; }
                    public PaymentMethod? Payment { get; set; }
                }

                public record TestEvent(Guid Id);

                public class TestPerspective : Whizbang.Core.Perspectives.IPerspectiveFor<TestModel, TestEvent> {
                    public TestModel Apply(TestModel? model, TestEvent evt) => model ?? new();
                }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveModelPolymorphicAnalyzer>(source);

    // Assert
    await Assert.That(diagnostics).Count().IsEqualTo(1);
    await Assert.That(diagnostics[0].Id).IsEqualTo("WHIZ811");
    await Assert.That(diagnostics[0].Severity).IsEqualTo(DiagnosticSeverity.Info);
    await Assert.That(diagnostics[0].GetMessage(CultureInfo.InvariantCulture)).Contains("Payment");
    await Assert.That(diagnostics[0].GetMessage(CultureInfo.InvariantCulture)).Contains("TestModel");
    await Assert.That(diagnostics[0].GetMessage(CultureInfo.InvariantCulture)).Contains("PaymentMethod");
  }

  /// <summary>
  /// Verifies that concrete (even attributed) property types do NOT trigger WHIZ811.
  /// The [Obsolete] attribute exercises the attribute scan without matching [JsonPolymorphic].
  /// </summary>
  [Test]
  public async Task PerspectiveModel_WithConcreteSealedProperty_NoDiagnosticAsync() {
    // Arrange
    const string source = """
            using System;

            namespace Whizbang.Core.Perspectives {
                public interface IPerspectiveFor<TModel> { }
                public interface IPerspectiveFor<TModel, TEvent1> : IPerspectiveFor<TModel> { }
            }

            namespace TestNamespace {
                [Obsolete("still concrete")]
                public sealed class Address {
                    public string City { get; set; } = string.Empty;
                }

                public class TestModel {
                    public Guid Id { get; set; }
                    public Address? ShippingAddress { get; set; }
                }

                public record TestEvent(Guid Id);

                public class TestPerspective : Whizbang.Core.Perspectives.IPerspectiveFor<TestModel, TestEvent> {
                    public TestModel Apply(TestModel? model, TestEvent evt) => model ?? new();
                }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveModelPolymorphicAnalyzer>(source);

    // Assert
    await Assert.That(diagnostics).IsEmpty();
  }

  /// <summary>
  /// Verifies that a concrete class marked [JsonPolymorphic] triggers WHIZ811.
  /// </summary>
  [Test]
  public async Task PerspectiveModel_WithJsonPolymorphicClassProperty_ReportsWHIZ811Async() {
    // Arrange
    const string source = """
            using System;
            using System.Text.Json.Serialization;

            namespace Whizbang.Core.Perspectives {
                public interface IPerspectiveFor<TModel> { }
                public interface IPerspectiveFor<TModel, TEvent1> : IPerspectiveFor<TModel> { }
            }

            namespace TestNamespace {
                [JsonPolymorphic]
                [JsonDerivedType(typeof(Circle), "circle")]
                public class Shape {
                    public string Color { get; set; } = string.Empty;
                }

                public class Circle : Shape {
                    public double Radius { get; set; }
                }

                public class TestModel {
                    public Guid Id { get; set; }
                    public Shape? MainShape { get; set; }
                }

                public record TestEvent(Guid Id);

                public class TestPerspective : Whizbang.Core.Perspectives.IPerspectiveFor<TestModel, TestEvent> {
                    public TestModel Apply(TestModel? model, TestEvent evt) => model ?? new();
                }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveModelPolymorphicAnalyzer>(source);

    // Assert
    await Assert.That(diagnostics).Count().IsEqualTo(1);
    await Assert.That(diagnostics[0].Id).IsEqualTo("WHIZ811");
    await Assert.That(diagnostics[0].GetMessage(CultureInfo.InvariantCulture)).Contains("MainShape");
    await Assert.That(diagnostics[0].GetMessage(CultureInfo.InvariantCulture)).Contains("Shape");
  }

  /// <summary>
  /// Verifies that a plain interface-typed property does NOT trigger WHIZ811.
  /// The analyzer only flags abstract CLASSES and [JsonPolymorphic] types -
  /// interfaces without the attribute are not reported (TypeKind.Interface fails
  /// the abstract-class check and is not recursed into).
  /// </summary>
  [Test]
  public async Task PerspectiveModel_WithPlainInterfaceProperty_NoDiagnosticAsync() {
    // Arrange
    const string source = """
            using System;

            namespace Whizbang.Core.Perspectives {
                public interface IPerspectiveFor<TModel> { }
                public interface IPerspectiveFor<TModel, TEvent1> : IPerspectiveFor<TModel> { }
            }

            namespace TestNamespace {
                public interface IShape {
                    string Color { get; }
                }

                public class TestModel {
                    public Guid Id { get; set; }
                    public IShape? MainShape { get; set; }
                }

                public record TestEvent(Guid Id);

                public class TestPerspective : Whizbang.Core.Perspectives.IPerspectiveFor<TestModel, TestEvent> {
                    public TestModel Apply(TestModel? model, TestEvent evt) => model ?? new();
                }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveModelPolymorphicAnalyzer>(source);

    // Assert
    await Assert.That(diagnostics).IsEmpty();
  }

  /// <summary>
  /// Verifies that an interface marked [JsonPolymorphic] IS reported (the attribute
  /// check applies to any named type, not just classes).
  /// </summary>
  [Test]
  public async Task PerspectiveModel_WithJsonPolymorphicInterfaceProperty_ReportsWHIZ811Async() {
    // Arrange
    const string source = """
            using System;
            using System.Text.Json.Serialization;

            namespace Whizbang.Core.Perspectives {
                public interface IPerspectiveFor<TModel> { }
                public interface IPerspectiveFor<TModel, TEvent1> : IPerspectiveFor<TModel> { }
            }

            namespace TestNamespace {
                [JsonPolymorphic]
                public interface IShape {
                    string Color { get; }
                }

                public class TestModel {
                    public Guid Id { get; set; }
                    public IShape? MainShape { get; set; }
                }

                public record TestEvent(Guid Id);

                public class TestPerspective : Whizbang.Core.Perspectives.IPerspectiveFor<TestModel, TestEvent> {
                    public TestModel Apply(TestModel? model, TestEvent evt) => model ?? new();
                }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveModelPolymorphicAnalyzer>(source);

    // Assert
    await Assert.That(diagnostics).Count().IsEqualTo(1);
    await Assert.That(diagnostics[0].Id).IsEqualTo("WHIZ811");
    await Assert.That(diagnostics[0].GetMessage(CultureInfo.InvariantCulture)).Contains("IShape");
  }

  /// <summary>
  /// Verifies that List&lt;AbstractType&gt; is flagged via the collection element type check.
  /// </summary>
  [Test]
  public async Task PerspectiveModel_WithListOfAbstract_ReportsWHIZ811Async() {
    // Arrange
    const string source = """
            using System;
            using System.Collections.Generic;

            namespace Whizbang.Core.Perspectives {
                public interface IPerspectiveFor<TModel> { }
                public interface IPerspectiveFor<TModel, TEvent1> : IPerspectiveFor<TModel> { }
            }

            namespace TestNamespace {
                public abstract class LineItem {
                    public decimal Amount { get; set; }
                }

                public class TestModel {
                    public Guid Id { get; set; }
                    public List<LineItem> Items { get; set; } = new();
                }

                public record TestEvent(Guid Id);

                public class TestPerspective : Whizbang.Core.Perspectives.IPerspectiveFor<TestModel, TestEvent> {
                    public TestModel Apply(TestModel? model, TestEvent evt) => model ?? new();
                }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveModelPolymorphicAnalyzer>(source);

    // Assert
    await Assert.That(diagnostics).Count().IsEqualTo(1);
    await Assert.That(diagnostics[0].Id).IsEqualTo("WHIZ811");
    await Assert.That(diagnostics[0].GetMessage(CultureInfo.InvariantCulture)).Contains("Items");
    await Assert.That(diagnostics[0].GetMessage(CultureInfo.InvariantCulture)).Contains("LineItem");
  }

  /// <summary>
  /// Verifies that IReadOnlyList&lt;AbstractType&gt; is also flagged (collection interface variant).
  /// </summary>
  [Test]
  public async Task PerspectiveModel_WithReadOnlyListOfAbstract_ReportsWHIZ811Async() {
    // Arrange
    const string source = """
            using System;
            using System.Collections.Generic;

            namespace Whizbang.Core.Perspectives {
                public interface IPerspectiveFor<TModel> { }
                public interface IPerspectiveFor<TModel, TEvent1> : IPerspectiveFor<TModel> { }
            }

            namespace TestNamespace {
                public abstract class LineItem {
                    public decimal Amount { get; set; }
                }

                public class TestModel {
                    public Guid Id { get; set; }
                    public IReadOnlyList<LineItem> Items { get; set; } = new List<LineItem>();
                }

                public record TestEvent(Guid Id);

                public class TestPerspective : Whizbang.Core.Perspectives.IPerspectiveFor<TestModel, TestEvent> {
                    public TestModel Apply(TestModel? model, TestEvent evt) => model ?? new();
                }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveModelPolymorphicAnalyzer>(source);

    // Assert
    await Assert.That(diagnostics).Count().IsEqualTo(1);
    await Assert.That(diagnostics[0].Id).IsEqualTo("WHIZ811");
    await Assert.That(diagnostics[0].GetMessage(CultureInfo.InvariantCulture)).Contains("Items");
  }

  /// <summary>
  /// Verifies that polymorphic properties nested inside a concrete wrapper type are
  /// detected recursively. The diagnostic is reported against the nested property
  /// and names the wrapper type as the containing type.
  /// </summary>
  [Test]
  public async Task PerspectiveModel_WithNestedAbstractProperty_ReportsWHIZ811Async() {
    // Arrange
    const string source = """
            using System;

            namespace Whizbang.Core.Perspectives {
                public interface IPerspectiveFor<TModel> { }
                public interface IPerspectiveFor<TModel, TEvent1> : IPerspectiveFor<TModel> { }
            }

            namespace TestNamespace {
                public abstract class PaymentMethod {
                    public string Name { get; set; } = string.Empty;
                }

                public class OrderDetails {
                    public PaymentMethod? Payment { get; set; }
                }

                public class TestModel {
                    public Guid Id { get; set; }
                    public OrderDetails? Details { get; set; }
                }

                public record TestEvent(Guid Id);

                public class TestPerspective : Whizbang.Core.Perspectives.IPerspectiveFor<TestModel, TestEvent> {
                    public TestModel Apply(TestModel? model, TestEvent evt) => model ?? new();
                }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveModelPolymorphicAnalyzer>(source);

    // Assert
    await Assert.That(diagnostics).Count().IsEqualTo(1);
    await Assert.That(diagnostics[0].Id).IsEqualTo("WHIZ811");
    await Assert.That(diagnostics[0].GetMessage(CultureInfo.InvariantCulture)).Contains("Payment");
    await Assert.That(diagnostics[0].GetMessage(CultureInfo.InvariantCulture)).Contains("OrderDetails");
  }

  /// <summary>
  /// Verifies that non-perspective classes with abstract properties are NOT flagged.
  /// </summary>
  [Test]
  public async Task NonPerspectiveClass_WithAbstractProperty_NoDiagnosticAsync() {
    // Arrange
    const string source = """
            using System;

            namespace TestNamespace {
                public abstract class PaymentMethod {
                    public string Name { get; set; } = string.Empty;
                }

                public class RegularModel {
                    public Guid Id { get; set; }
                    public PaymentMethod? Payment { get; set; }
                }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveModelPolymorphicAnalyzer>(source);

    // Assert
    await Assert.That(diagnostics).IsEmpty();
  }

  /// <summary>
  /// Verifies that abstract perspective base classes are skipped entirely
  /// (they cannot be instantiated as perspectives).
  /// </summary>
  [Test]
  public async Task AbstractPerspectiveClass_IsSkippedAsync() {
    // Arrange
    const string source = """
            using System;

            namespace Whizbang.Core.Perspectives {
                public interface IPerspectiveFor<TModel> { }
                public interface IPerspectiveFor<TModel, TEvent1> : IPerspectiveFor<TModel> { }
            }

            namespace TestNamespace {
                public abstract class PaymentMethod {
                    public string Name { get; set; } = string.Empty;
                }

                public class TestModel {
                    public Guid Id { get; set; }
                    public PaymentMethod? Payment { get; set; }
                }

                public record TestEvent(Guid Id);

                public abstract class BasePerspective : Whizbang.Core.Perspectives.IPerspectiveFor<TestModel, TestEvent> {
                    public TestModel Apply(TestModel? model, TestEvent evt) => model ?? new();
                }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveModelPolymorphicAnalyzer>(source);

    // Assert
    await Assert.That(diagnostics).IsEmpty();
  }

  /// <summary>
  /// Verifies that [JsonIgnore] abstract properties are NOT flagged.
  /// </summary>
  [Test]
  public async Task PerspectiveModel_WithJsonIgnoredAbstractProperty_NoDiagnosticAsync() {
    // Arrange
    const string source = """
            using System;
            using System.Text.Json.Serialization;

            namespace Whizbang.Core.Perspectives {
                public interface IPerspectiveFor<TModel> { }
                public interface IPerspectiveFor<TModel, TEvent1> : IPerspectiveFor<TModel> { }
            }

            namespace TestNamespace {
                public abstract class PaymentMethod {
                    public string Name { get; set; } = string.Empty;
                }

                public class TestModel {
                    public Guid Id { get; set; }
                    [JsonIgnore]
                    public PaymentMethod? Payment { get; set; }
                }

                public record TestEvent(Guid Id);

                public class TestPerspective : Whizbang.Core.Perspectives.IPerspectiveFor<TestModel, TestEvent> {
                    public TestModel Apply(TestModel? model, TestEvent evt) => model ?? new();
                }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveModelPolymorphicAnalyzer>(source);

    // Assert
    await Assert.That(diagnostics).IsEmpty();
  }

  /// <summary>
  /// Verifies that [NotMapped] abstract properties are NOT flagged.
  /// </summary>
  [Test]
  public async Task PerspectiveModel_WithNotMappedAbstractProperty_NoDiagnosticAsync() {
    // Arrange
    const string source = """
            using System;
            using System.ComponentModel.DataAnnotations.Schema;

            namespace Whizbang.Core.Perspectives {
                public interface IPerspectiveFor<TModel> { }
                public interface IPerspectiveFor<TModel, TEvent1> : IPerspectiveFor<TModel> { }
            }

            namespace TestNamespace {
                public abstract class PaymentMethod {
                    public string Name { get; set; } = string.Empty;
                }

                public class TestModel {
                    public Guid Id { get; set; }
                    [NotMapped]
                    public PaymentMethod? Payment { get; set; }
                }

                public record TestEvent(Guid Id);

                public class TestPerspective : Whizbang.Core.Perspectives.IPerspectiveFor<TestModel, TestEvent> {
                    public TestModel Apply(TestModel? model, TestEvent evt) => model ?? new();
                }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveModelPolymorphicAnalyzer>(source);

    // Assert
    await Assert.That(diagnostics).IsEmpty();
  }

  /// <summary>
  /// Verifies that static and write-only properties of abstract types are skipped.
  /// </summary>
  [Test]
  public async Task PerspectiveModel_WithStaticAndWriteOnlyAbstractProperties_NoDiagnosticAsync() {
    // Arrange
    const string source = """
            using System;

            namespace Whizbang.Core.Perspectives {
                public interface IPerspectiveFor<TModel> { }
                public interface IPerspectiveFor<TModel, TEvent1> : IPerspectiveFor<TModel> { }
            }

            namespace TestNamespace {
                public abstract class PaymentMethod {
                    public string Name { get; set; } = string.Empty;
                }

                public class TestModel {
                    public Guid Id { get; set; }
                    public static PaymentMethod? SharedDefault { get; set; }
                    public PaymentMethod? Sink { set { } }
                }

                public record TestEvent(Guid Id);

                public class TestPerspective : Whizbang.Core.Perspectives.IPerspectiveFor<TestModel, TestEvent> {
                    public TestModel Apply(TestModel? model, TestEvent evt) => model ?? new();
                }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveModelPolymorphicAnalyzer>(source);

    // Assert
    await Assert.That(diagnostics).IsEmpty();
  }

  /// <summary>
  /// Verifies that self-referencing models terminate (cycle detection) without diagnostics.
  /// </summary>
  [Test]
  public async Task PerspectiveModel_SelfReferencing_NoDiagnosticAndTerminatesAsync() {
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
                    public TestModel? Parent { get; set; }
                }

                public record TestEvent(Guid Id);

                public class TestPerspective : Whizbang.Core.Perspectives.IPerspectiveFor<TestModel, TestEvent> {
                    public TestModel Apply(TestModel? model, TestEvent evt) => model ?? new();
                }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveModelPolymorphicAnalyzer>(source);

    // Assert
    await Assert.That(diagnostics).IsEmpty();
  }

  /// <summary>
  /// Documents current behavior: WHIZ811 is an Info-level SUGGESTION to add
  /// [PolymorphicDiscriminator]; the analyzer still reports the abstract property
  /// even when a discriminator property is present. The (string-typed) discriminator
  /// property itself is never flagged. Exactly one diagnostic is expected.
  /// </summary>
  [Test]
  public async Task PerspectiveModel_WithDiscriminatorAndAbstractProperty_StillReportsWHIZ811ForAbstractPropertyOnlyAsync() {
    // Arrange
    const string source = """
            using System;

            namespace Whizbang.Core.Perspectives {
                public interface IPerspectiveFor<TModel> { }
                public interface IPerspectiveFor<TModel, TEvent1> : IPerspectiveFor<TModel> { }

                public sealed class PolymorphicDiscriminatorAttribute : System.Attribute {
                    public string? ColumnName { get; set; }
                }
            }

            namespace TestNamespace {
                public abstract class PaymentMethod {
                    public string Name { get; set; } = string.Empty;
                }

                public class TestModel {
                    public Guid Id { get; set; }
                    public PaymentMethod? Payment { get; set; }

                    [Whizbang.Core.Perspectives.PolymorphicDiscriminator]
                    public string PaymentType { get; set; } = string.Empty;
                }

                public record TestEvent(Guid Id);

                public class TestPerspective : Whizbang.Core.Perspectives.IPerspectiveFor<TestModel, TestEvent> {
                    public TestModel Apply(TestModel? model, TestEvent evt) => model ?? new();
                }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveModelPolymorphicAnalyzer>(source);

    // Assert - only the abstract property is reported, never the discriminator itself
    await Assert.That(diagnostics).Count().IsEqualTo(1);
    await Assert.That(diagnostics[0].Id).IsEqualTo("WHIZ811");
    await Assert.That(diagnostics[0].GetMessage(CultureInfo.InvariantCulture)).Contains("Payment");
    await Assert.That(diagnostics[0].GetMessage(CultureInfo.InvariantCulture)).DoesNotContain("PaymentType");
  }

  /// <summary>
  /// Verifies the IPerspectiveWithActionsFor interface-name variant is also analyzed.
  /// </summary>
  [Test]
  public async Task PerspectiveWithActionsFor_WithAbstractProperty_ReportsWHIZ811Async() {
    // Arrange
    const string source = """
            using System;

            namespace Whizbang.Core.Perspectives {
                public interface IPerspectiveWithActionsFor<TModel, TEvent1> { }
            }

            namespace TestNamespace {
                public abstract class PaymentMethod {
                    public string Name { get; set; } = string.Empty;
                }

                public class TestModel {
                    public Guid Id { get; set; }
                    public PaymentMethod? Payment { get; set; }
                }

                public record TestEvent(Guid Id);

                public class TestPerspective : Whizbang.Core.Perspectives.IPerspectiveWithActionsFor<TestModel, TestEvent> {
                    public TestModel Apply(TestModel? model, TestEvent evt) => model ?? new();
                }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveModelPolymorphicAnalyzer>(source);

    // Assert
    await Assert.That(diagnostics).Count().IsEqualTo(1);
    await Assert.That(diagnostics[0].Id).IsEqualTo("WHIZ811");
    await Assert.That(diagnostics[0].GetMessage(CultureInfo.InvariantCulture)).Contains("Payment");
  }

  /// <summary>
  /// Documents current behavior: array properties are skipped entirely because arrays
  /// are IArrayTypeSymbol, not INamedTypeSymbol (array coverage is owned by
  /// PerspectiveModelArrayAnalyzer, WHIZ812).
  /// </summary>
  [Test]
  public async Task PerspectiveModel_WithArrayOfAbstract_NoDiagnosticAsync() {
    // Arrange
    const string source = """
            using System;

            namespace Whizbang.Core.Perspectives {
                public interface IPerspectiveFor<TModel> { }
                public interface IPerspectiveFor<TModel, TEvent1> : IPerspectiveFor<TModel> { }
            }

            namespace TestNamespace {
                public abstract class LineItem {
                    public decimal Amount { get; set; }
                }

                public class TestModel {
                    public Guid Id { get; set; }
                    public LineItem[]? Items { get; set; }
                }

                public record TestEvent(Guid Id);

                public class TestPerspective : Whizbang.Core.Perspectives.IPerspectiveFor<TestModel, TestEvent> {
                    public TestModel Apply(TestModel? model, TestEvent evt) => model ?? new();
                }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveModelPolymorphicAnalyzer>(source);

    // Assert
    await Assert.That(diagnostics).IsEmpty();
  }

  /// <summary>
  /// Verifies that a model containing only system-type properties (Exception, Uri, string)
  /// produces no diagnostic: non-collection System types are never recursed into, and
  /// system primitives are excluded outright.
  /// </summary>
  [Test]
  public async Task PerspectiveModel_WithSystemTypeProperties_NoDiagnosticAsync() {
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
                    public Exception? LastError { get; set; }
                    public Uri? Link { get; set; }
                    public string Name { get; set; } = string.Empty;
                }

                public record TestEvent(Guid Id);

                public class TestPerspective : Whizbang.Core.Perspectives.IPerspectiveFor<TestModel, TestEvent> {
                    public TestModel Apply(TestModel? model, TestEvent evt) => model ?? new();
                }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveModelPolymorphicAnalyzer>(source);

    // Assert
    await Assert.That(diagnostics).IsEmpty();
  }
}
