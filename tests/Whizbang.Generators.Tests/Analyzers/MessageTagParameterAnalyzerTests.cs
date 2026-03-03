using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Whizbang.Generators.Analyzers;

namespace Whizbang.Generators.Tests.Analyzers;

/// <summary>
/// Tests for MessageTagParameterAnalyzer WHIZ090.
/// Verifies that constructor parameters in MessageTagAttribute subclasses match property names.
/// </summary>
/// <docs>diagnostics/whiz090</docs>
[Category("Analyzers")]
public class MessageTagParameterAnalyzerTests {
  // ========================================
  // WHIZ090: Parameter Name Mismatch Tests
  // ========================================

  /// <summary>
  /// Test that constructor parameter matching property name (case-insensitive) produces no diagnostic.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_ParameterMatchesProperty_NoDiagnosticAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Attributes;

            namespace TestApp;

            [AttributeUsage(AttributeTargets.Class)]
            public class MyTagAttribute : MessageTagAttribute {
              public MyTagAttribute(string tag) {
                Tag = tag;
              }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<MessageTagParameterAnalyzer>(source);

    // Assert
    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ090")).IsEmpty();
  }

  /// <summary>
  /// Test that constructor parameter NOT matching any property produces WHIZ090.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_ParameterDoesNotMatchProperty_ReportsDiagnosticAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Attributes;

            namespace TestApp;

            [AttributeUsage(AttributeTargets.Class)]
            public class MyTagAttribute : MessageTagAttribute {
              public MyTagAttribute(string tagName) {
                Tag = tagName;
              }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<MessageTagParameterAnalyzer>(source);

    // Assert
    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ090")).Count().IsEqualTo(1);
    var diagnostic = diagnostics.First(d => d.Id == "WHIZ090");
    await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Error);
    await Assert.That(diagnostic.GetMessage(CultureInfo.InvariantCulture)).Contains("tagName");
  }

  /// <summary>
  /// Test that parameter matching with different casing works (case-insensitive).
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_ParameterMatchesCaseInsensitive_NoDiagnosticAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Attributes;

            namespace TestApp;

            [AttributeUsage(AttributeTargets.Class)]
            public class MyTagAttribute : MessageTagAttribute {
              public MyTagAttribute(string TAG) {
                Tag = TAG;
              }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<MessageTagParameterAnalyzer>(source);

    // Assert
    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ090")).IsEmpty();
  }

  /// <summary>
  /// Test that multiple parameters each get checked.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_MultipleParameters_AllMustMatchAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Attributes;

            namespace TestApp;

            [AttributeUsage(AttributeTargets.Class)]
            public class MyTagAttribute : MessageTagAttribute {
              public bool IncludeDetails { get; set; }

              public MyTagAttribute(string tagName, bool includeDetails) {
                Tag = tagName;
                IncludeDetails = includeDetails;
              }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<MessageTagParameterAnalyzer>(source);

    // Assert - only 'tagName' should fail, 'includeDetails' matches IncludeDetails property
    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ090")).Count().IsEqualTo(1);
    var diagnostic = diagnostics.First(d => d.Id == "WHIZ090");
    await Assert.That(diagnostic.GetMessage(CultureInfo.InvariantCulture)).Contains("tagName");
  }

  /// <summary>
  /// Test that base MessageTagAttribute class itself is not analyzed (only subclasses).
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_BaseClassMessageTagAttribute_NotAnalyzedAsync() {
    // Arrange - test code that uses MessageTagAttribute directly
    var source = """
            using System;
            using Whizbang.Core.Attributes;

            namespace TestApp;

            // Just using the base attribute, not creating a subclass
            [MessageTag(Tag = "test")]
            public class TestEvent { }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<MessageTagParameterAnalyzer>(source);

    // Assert - no WHIZ090 since we're not creating a subclass
    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ090")).IsEmpty();
  }

  /// <summary>
  /// Test that parameterless constructor produces no diagnostic.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_ParameterlessConstructor_NoDiagnosticAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Attributes;

            namespace TestApp;

            [AttributeUsage(AttributeTargets.Class)]
            public class MyTagAttribute : MessageTagAttribute {
              public MyTagAttribute() { }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<MessageTagParameterAnalyzer>(source);

    // Assert
    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ090")).IsEmpty();
  }

  /// <summary>
  /// Test that parameter matching inherited property (Tag from base class) works.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_ParameterMatchesInheritedProperty_NoDiagnosticAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Attributes;

            namespace TestApp;

            [AttributeUsage(AttributeTargets.Class)]
            public class MyTagAttribute : MessageTagAttribute {
              public MyTagAttribute(string tag, bool includeEvent) {
                Tag = tag;
                IncludeEvent = includeEvent;
              }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<MessageTagParameterAnalyzer>(source);

    // Assert - both parameters match inherited properties
    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ090")).IsEmpty();
  }

  /// <summary>
  /// Test that non-MessageTagAttribute subclass is not analyzed.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_NonMessageTagSubclass_NotAnalyzedAsync() {
    // Arrange
    var source = """
            using System;

            namespace TestApp;

            [AttributeUsage(AttributeTargets.Class)]
            public class MyCustomAttribute : Attribute {
              public string Value { get; }

              public MyCustomAttribute(string somethingElse) {
                Value = somethingElse;
              }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<MessageTagParameterAnalyzer>(source);

    // Assert - not a MessageTagAttribute subclass, should not be analyzed
    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ090")).IsEmpty();
  }

  /// <summary>
  /// Test that multiple constructors are all checked.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_MultipleConstructors_EachCheckedAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Attributes;

            namespace TestApp;

            [AttributeUsage(AttributeTargets.Class)]
            public class MyTagAttribute : MessageTagAttribute {
              public string? Category { get; set; }

              public MyTagAttribute(string tag) {
                Tag = tag;
              }

              public MyTagAttribute(string tag, string categoryName) {
                Tag = tag;
                Category = categoryName;
              }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<MessageTagParameterAnalyzer>(source);

    // Assert - 'categoryName' doesn't match 'Category' (case-insensitive)
    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ090")).Count().IsEqualTo(1);
    var diagnostic = diagnostics.First(d => d.Id == "WHIZ090");
    await Assert.That(diagnostic.GetMessage(CultureInfo.InvariantCulture)).Contains("categoryName");
  }

  /// <summary>
  /// Test diagnostic message suggests the correct property to match.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_DiagnosticMessage_SuggestsCorrectPropertyAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Attributes;

            namespace TestApp;

            [AttributeUsage(AttributeTargets.Class)]
            public class MyTagAttribute : MessageTagAttribute {
              public MyTagAttribute(string tagName) {
                Tag = tagName;
              }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<MessageTagParameterAnalyzer>(source);

    // Assert
    var diagnostic = diagnostics.First(d => d.Id == "WHIZ090");
    var message = diagnostic.GetMessage(CultureInfo.InvariantCulture);
    // Should suggest renaming to 'tag' to match 'Tag'
    await Assert.That(message).Contains("tag");
    await Assert.That(message).Contains("Tag");
  }

  /// <summary>
  /// Test that abstract MessageTagAttribute subclass is also analyzed.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_AbstractSubclass_IsAnalyzedAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Attributes;

            namespace TestApp;

            [AttributeUsage(AttributeTargets.Class)]
            public abstract class BaseTagAttribute : MessageTagAttribute {
              protected BaseTagAttribute(string tagName) {
                Tag = tagName;
              }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<MessageTagParameterAnalyzer>(source);

    // Assert - abstract classes should also be analyzed
    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ090")).Count().IsEqualTo(1);
  }
}
