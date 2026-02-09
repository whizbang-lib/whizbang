using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Generators;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Tests for StreamKeyGenerator source generator.
/// Verifies zero-reflection stream key extraction for AOT compatibility.
/// </summary>
[Category("SourceGenerators")]
[Category("StreamKey")]
public class StreamKeyGeneratorTests {

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithPropertyAttribute_GeneratesExtractorAsync() {
    // Arrange
    var source = @"
using Whizbang.Core;

namespace MyApp.Events;

public record OrderCreated([StreamKey] string OrderId, string CustomerName) : IEvent;
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<StreamKeyGenerator>(source);

    // Assert
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "StreamKeyExtractors.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("namespace TestAssembly.Generated");
    await Assert.That(code).Contains("public static partial class StreamKeyExtractors");
    await Assert.That(code).Contains("MyApp.Events.OrderCreated");
    await Assert.That(code).Contains("Extract stream key from OrderCreated");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithMultipleEvents_GeneratesAllExtractorsAsync() {
    // Arrange
    var source = @"
using Whizbang.Core;

namespace MyApp.Events;

public record OrderCreated([StreamKey] string OrderId, string CustomerName) : IEvent;
public record OrderShipped([StreamKey] string OrderId, string TrackingNumber) : IEvent;
public record UserRegistered([StreamKey] System.Guid UserId, string Email) : IEvent;
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<StreamKeyGenerator>(source);

    // Assert
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "StreamKeyExtractors.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("OrderCreated");
    await Assert.That(code).Contains("OrderShipped");
    await Assert.That(code).Contains("UserRegistered");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithNoEvents_GeneratesEmptyExtractorAsync() {
    // Arrange
    var source = @"
namespace MyApp;

public class SomeClass {
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<StreamKeyGenerator>(source);

    // Assert - Should still generate file, just empty
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "StreamKeyExtractors.g.cs");
    await Assert.That(code).IsNotNull();
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithClassProperty_GeneratesExtractorAsync() {
    // Arrange - Class (not record) with [StreamKey] property
    var source = @"
using Whizbang.Core;

namespace MyApp.Events;

public class LegacyOrderCreated : IEvent {
  [StreamKey]
  public string OrderId { get; set; } = string.Empty;
  public string CustomerName { get; set; } = string.Empty;
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<StreamKeyGenerator>(source);

    // Assert
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "StreamKeyExtractors.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("LegacyOrderCreated");
    await Assert.That(code).Contains("OrderId");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_ReportsDiagnostic_ForEventWithNoStreamKeyAsync() {
    // Arrange - Event without [StreamKey] attribute
    var source = @"
using Whizbang.Core;

namespace MyApp.Events;

public record InvalidEvent(string Data) : IEvent;
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<StreamKeyGenerator>(source);

    // Assert - Should report warning about missing [StreamKey]
    var warnings = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Warning).ToArray();
    await Assert.That(warnings).Count().IsGreaterThanOrEqualTo(1);

    var streamKeyWarning = warnings.FirstOrDefault(d => d.Id.StartsWith("WHIZ", StringComparison.Ordinal));
    await Assert.That(streamKeyWarning).IsNotNull();
    await Assert.That(streamKeyWarning!.GetMessage(CultureInfo.InvariantCulture)).Contains("StreamKey");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithNonPublicEvent_SkipsAsync() {
    // Arrange - Tests DeclaredAccessibility != Public branch
    var source = @"
using Whizbang.Core;

namespace MyApp.Events;

public record PublicEvent([StreamKey] string Id) : IEvent;

internal record InternalEvent([StreamKey] string Id) : IEvent;
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<StreamKeyGenerator>(source);

    // Assert - Should only generate for public event
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "StreamKeyExtractors.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("PublicEvent");
    await Assert.That(code).DoesNotContain("InternalEvent");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithAbstractEvent_ProcessesAsync() {
    // Arrange - Abstract event with [StreamKey]
    var source = @"
using Whizbang.Core;

namespace MyApp.Events;

public abstract record BaseEvent([StreamKey] string Id) : IEvent;
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<StreamKeyGenerator>(source);

    // Assert - Should generate extractor for abstract event
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "StreamKeyExtractors.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("BaseEvent");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithRecordAndClassProperties_GeneratesForBothAsync() {
    // Arrange - Tests both property and constructor parameter branches
    var source = @"
using Whizbang.Core;

namespace MyApp.Events;

public record RecordEvent([StreamKey] string RecordId, string Data) : IEvent;

public class ClassEvent : IEvent {
  [StreamKey]
  public string ClassId { get; set; } = string.Empty;
  public string Data { get; set; } = string.Empty;
}
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<StreamKeyGenerator>(source);

    // Assert - Should generate for both record and class
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "StreamKeyExtractors.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("RecordEvent");
    await Assert.That(code).Contains("RecordId");
    await Assert.That(code).Contains("ClassEvent");
    await Assert.That(code).Contains("ClassId");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithNonEventType_SkipsAsync() {
    // Arrange - Tests !implementsIEvent branch
    var source = @"
using Whizbang.Core;

namespace MyApp.Events;

public record NotAnEvent([StreamKey] string Id);  // No IEvent interface
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<StreamKeyGenerator>(source);

    // Assert - Should skip non-event type
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "StreamKeyExtractors.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).DoesNotContain("NotAnEvent");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithStructEvent_SkipsAsync() {
    // Arrange - Struct implementing IEvent (generator only processes records and classes)
    // Tests syntactic predicate filtering - only RecordDeclarationSyntax and ClassDeclarationSyntax
    var source = @"
using Whizbang.Core;

namespace MyApp.Events;

public struct StructEvent : IEvent {
  [StreamKey]
  public string Id { get; set; }
  public string Data { get; set; }
}

public record RecordEvent([StreamKey] string Id) : IEvent;
";

    // Act
    var result = GeneratorTestHelper.RunGenerator<StreamKeyGenerator>(source);

    // Assert - Should skip struct, process record
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "StreamKeyExtractors.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).DoesNotContain("StructEvent");  // Struct is skipped
    await Assert.That(code).Contains("RecordEvent");  // Record is processed
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task StreamKeyGenerator_NullableValueTypeKey_GeneratesNullableExtractorAsync() {
    // Arrange - Tests line 234-249: isNullable detection for types ending with ?
    var source = """
using Whizbang.Core;

namespace MyApp;

public record NullableKeyEvent([StreamKey] int? OrderId, string Data) : IEvent;
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<StreamKeyGenerator>(source);

    // Assert - Should generate nullable extractor with null check
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "StreamKeyExtractors.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("NullableKeyEvent");
    // Nullable extractor uses if-null-throw pattern
    await Assert.That(code).Contains("if (key is null)");
    await Assert.That(code).Contains("key.ToString()!");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task StreamKeyGenerator_NullableGuidKey_GeneratesNullableExtractorAsync() {
    // Arrange - Tests line 234-249: Nullable<Guid> detection
    var source = """
using Whizbang.Core;
using System;

namespace MyApp;

public record OptionalGuidEvent([StreamKey] Guid? Id, string Data) : IEvent;
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<StreamKeyGenerator>(source);

    // Assert - Should generate nullable extractor with null check
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "StreamKeyExtractors.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("OptionalGuidEvent");
    // Nullable extractor uses if-null-throw pattern
    await Assert.That(code).Contains("if (key is null)");
    await Assert.That(code).Contains("key.ToString()!");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task StreamKeyGenerator_TypeNotImplementingIEvent_SkipsAsync() {
    // Arrange - Tests line 64: !implementsIEvent branch
    var source = """
using Whizbang.Core;

namespace MyApp;

// Has [StreamKey] but doesn't implement IEvent
public record NotAnEvent([StreamKey] string Id, string Data);
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<StreamKeyGenerator>(source);

    // Assert - Should skip type that doesn't implement IEvent
    var code = GeneratorTestHelper.GetGeneratedSource(result, "StreamKeyExtractors.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).DoesNotContain("NotAnEvent");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task StreamKeyGenerator_ClassWithStreamKeyProperty_GeneratesExtractorAsync() {
    // Arrange - Tests line 48 (ClassDeclarationSyntax branch in switch)
    var source = """
using Whizbang.Core;

namespace MyApp;

public class ClassBasedEvent : IEvent {
  [StreamKey]
  public string EventId { get; set; } = "";
  public string Data { get; set; } = "";
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<StreamKeyGenerator>(source);

    // Assert - Should generate extractor for class-based event
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "StreamKeyExtractors.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("ClassBasedEvent");
    await Assert.That(code).Contains("EventId");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task StreamKeyGenerator_InheritedStreamKey_GeneratesExtractorAsync() {
    // Arrange - Tests inherited [StreamKey] detection from base class
    var source = """
using Whizbang.Core;
using System;

namespace MyApp.Events;

// Base class with [StreamKey] on StreamId property
public abstract class BaseEvent : IEvent {
  [StreamKey]
  public virtual Guid StreamId { get; set; }
  public string? CorrelationId { get; set; }
}

// Derived event - should inherit [StreamKey] from base
public class OrderCreatedEvent : BaseEvent {
  public string OrderName { get; set; } = "";
}

// Another derived event - also inherits [StreamKey]
public class OrderShippedEvent : BaseEvent {
  public string TrackingNumber { get; set; } = "";
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<StreamKeyGenerator>(source);

    // Assert - Should NOT report WHIZ009 (missing StreamKey) for derived classes
    var whiz009Warnings = result.Diagnostics.Where(d =>
        d.Id == "WHIZ009" &&
        (d.GetMessage(CultureInfo.InvariantCulture).Contains("OrderCreatedEvent") ||
         d.GetMessage(CultureInfo.InvariantCulture).Contains("OrderShippedEvent")));
    await Assert.That(whiz009Warnings).IsEmpty();

    // Assert - Should generate extractors for all three event types
    var code = GeneratorTestHelper.GetGeneratedSource(result, "StreamKeyExtractors.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("BaseEvent");
    await Assert.That(code).Contains("OrderCreatedEvent");
    await Assert.That(code).Contains("OrderShippedEvent");
    await Assert.That(code).Contains("StreamId"); // All use inherited StreamId property
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task StreamKeyGenerator_InheritedStreamKey_NoFalsePositiveWarningsAsync() {
    // Arrange - Verify derived classes don't trigger WHIZ009 false positives
    var source = """
using Whizbang.Core;
using System;

namespace MyApp;

public class BaseJdxEvent : IEvent {
  [StreamKey]
  public virtual Guid StreamId { get; set; }
}

public class DerivedEvent : BaseJdxEvent {
  public string Data { get; set; } = "";
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<StreamKeyGenerator>(source);

    // Assert - No WHIZ009 warning for DerivedEvent (it inherits [StreamKey])
    var derivedWarnings = result.Diagnostics.Where(d =>
        d.Id == "WHIZ009" &&
        d.GetMessage(CultureInfo.InvariantCulture).Contains("DerivedEvent"));
    await Assert.That(derivedWarnings).IsEmpty();

    // Assert - Extractor generated for derived event
    var code = GeneratorTestHelper.GetGeneratedSource(result, "StreamKeyExtractors.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("DerivedEvent");
  }
}
