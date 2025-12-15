using Microsoft.CodeAnalysis;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Tests for <see cref="DiagnosticDescriptors"/>.
/// Validates diagnostic ID uniqueness, message formats, and metadata.
/// </summary>
/// <remarks>
/// NOTE: These tests currently have compilation issues due to ModuleInitializerAttribute conflict
/// between PolySharp and System.Runtime. This is a known issue that needs resolution.
/// Tests are stubs pending fix.
/// </remarks>
public class DiagnosticDescriptorsTests {

  [Test]
  public async Task AllDiagnosticIds_AreUniqueAsync() {
    // Arrange
    var descriptors = new[] {
      DiagnosticDescriptors.ReceptorDiscovered,
      DiagnosticDescriptors.NoReceptorsFound,
      DiagnosticDescriptors.InvalidReceptor,
      DiagnosticDescriptors.AggregateIdPropertyDiscovered,
      DiagnosticDescriptors.AggregateIdMustBeGuid,
      DiagnosticDescriptors.MultipleAggregateIdAttributes,
      DiagnosticDescriptors.PerspectiveDiscovered,
      DiagnosticDescriptors.PerspectiveSizeWarning,
      DiagnosticDescriptors.MissingStreamKeyAttribute,
      DiagnosticDescriptors.StreamKeyDiscovered,
      DiagnosticDescriptors.JsonSerializableTypeDiscovered,
      DiagnosticDescriptors.PerspectiveInvokerGenerated,
      DiagnosticDescriptors.WhizbangIdDiscovered,
      DiagnosticDescriptors.WhizbangIdMustBePartial,
      DiagnosticDescriptors.TopicFilterDiscovered,
      DiagnosticDescriptors.EnumFilterNoDescription,
      DiagnosticDescriptors.WhizbangIdDuplicateName,
      DiagnosticDescriptors.TopicFilterOnNonCommand,
      DiagnosticDescriptors.NoTopicFiltersFound,
      DiagnosticDescriptors.PublicApiMissingTests,
      DiagnosticDescriptors.InvalidTestReference,
      DiagnosticDescriptors.TestLinkDiscovered,
      DiagnosticDescriptors.FailedToLoadDocsMap,
      DiagnosticDescriptors.FailedToLoadTestsMap
    };

    // Act
    var ids = descriptors.Select(d => d.Id).ToList();
    var duplicates = ids.GroupBy(id => id).Where(g => g.Count() > 1).ToList();

    // Assert
    await Assert.That(duplicates).IsEmpty();
  }

  [Test]
  public async Task AllDiagnosticIds_FollowWhizPrefixConventionAsync() {
    // Arrange
    var descriptors = new[] {
      DiagnosticDescriptors.ReceptorDiscovered,
      DiagnosticDescriptors.NoReceptorsFound,
      DiagnosticDescriptors.InvalidReceptor,
      DiagnosticDescriptors.AggregateIdPropertyDiscovered,
      DiagnosticDescriptors.AggregateIdMustBeGuid,
      DiagnosticDescriptors.MultipleAggregateIdAttributes,
      DiagnosticDescriptors.PerspectiveDiscovered,
      DiagnosticDescriptors.PerspectiveSizeWarning,
      DiagnosticDescriptors.MissingStreamKeyAttribute,
      DiagnosticDescriptors.StreamKeyDiscovered,
      DiagnosticDescriptors.JsonSerializableTypeDiscovered,
      DiagnosticDescriptors.PerspectiveInvokerGenerated,
      DiagnosticDescriptors.WhizbangIdDiscovered,
      DiagnosticDescriptors.WhizbangIdMustBePartial,
      DiagnosticDescriptors.TopicFilterDiscovered,
      DiagnosticDescriptors.EnumFilterNoDescription,
      DiagnosticDescriptors.WhizbangIdDuplicateName,
      DiagnosticDescriptors.TopicFilterOnNonCommand,
      DiagnosticDescriptors.NoTopicFiltersFound,
      DiagnosticDescriptors.PublicApiMissingTests,
      DiagnosticDescriptors.InvalidTestReference,
      DiagnosticDescriptors.TestLinkDiscovered,
      DiagnosticDescriptors.FailedToLoadDocsMap,
      DiagnosticDescriptors.FailedToLoadTestsMap
    };

    // Act & Assert
    foreach (var descriptor in descriptors) {
      await Assert.That(descriptor.Id).StartsWith("WHIZ");
    }
  }

  [Test]
  public async Task AllDiagnostics_HaveCategoryAsync() {
    // Arrange
    var descriptors = new[] {
      DiagnosticDescriptors.ReceptorDiscovered,
      DiagnosticDescriptors.NoReceptorsFound,
      DiagnosticDescriptors.InvalidReceptor,
      DiagnosticDescriptors.AggregateIdPropertyDiscovered,
      DiagnosticDescriptors.AggregateIdMustBeGuid,
      DiagnosticDescriptors.MultipleAggregateIdAttributes,
      DiagnosticDescriptors.PerspectiveDiscovered,
      DiagnosticDescriptors.PerspectiveSizeWarning,
      DiagnosticDescriptors.MissingStreamKeyAttribute,
      DiagnosticDescriptors.StreamKeyDiscovered,
      DiagnosticDescriptors.JsonSerializableTypeDiscovered,
      DiagnosticDescriptors.PerspectiveInvokerGenerated,
      DiagnosticDescriptors.WhizbangIdDiscovered,
      DiagnosticDescriptors.WhizbangIdMustBePartial,
      DiagnosticDescriptors.TopicFilterDiscovered,
      DiagnosticDescriptors.EnumFilterNoDescription,
      DiagnosticDescriptors.WhizbangIdDuplicateName,
      DiagnosticDescriptors.TopicFilterOnNonCommand,
      DiagnosticDescriptors.NoTopicFiltersFound,
      DiagnosticDescriptors.PublicApiMissingTests,
      DiagnosticDescriptors.InvalidTestReference,
      DiagnosticDescriptors.TestLinkDiscovered,
      DiagnosticDescriptors.FailedToLoadDocsMap,
      DiagnosticDescriptors.FailedToLoadTestsMap
    };

    // Act & Assert
    foreach (var descriptor in descriptors) {
      await Assert.That(descriptor.Category).IsEqualTo("Whizbang.SourceGeneration");
    }
  }

  [Test]
  public async Task AllDiagnostics_AreEnabledByDefaultAsync() {
    // Arrange
    var descriptors = new[] {
      DiagnosticDescriptors.ReceptorDiscovered,
      DiagnosticDescriptors.NoReceptorsFound,
      DiagnosticDescriptors.InvalidReceptor,
      DiagnosticDescriptors.AggregateIdPropertyDiscovered,
      DiagnosticDescriptors.AggregateIdMustBeGuid,
      DiagnosticDescriptors.MultipleAggregateIdAttributes,
      DiagnosticDescriptors.PerspectiveDiscovered,
      DiagnosticDescriptors.PerspectiveSizeWarning,
      DiagnosticDescriptors.MissingStreamKeyAttribute,
      DiagnosticDescriptors.StreamKeyDiscovered,
      DiagnosticDescriptors.JsonSerializableTypeDiscovered,
      DiagnosticDescriptors.PerspectiveInvokerGenerated,
      DiagnosticDescriptors.WhizbangIdDiscovered,
      DiagnosticDescriptors.WhizbangIdMustBePartial,
      DiagnosticDescriptors.TopicFilterDiscovered,
      DiagnosticDescriptors.EnumFilterNoDescription,
      DiagnosticDescriptors.WhizbangIdDuplicateName,
      DiagnosticDescriptors.TopicFilterOnNonCommand,
      DiagnosticDescriptors.NoTopicFiltersFound,
      DiagnosticDescriptors.PublicApiMissingTests,
      DiagnosticDescriptors.InvalidTestReference,
      DiagnosticDescriptors.TestLinkDiscovered,
      DiagnosticDescriptors.FailedToLoadDocsMap,
      DiagnosticDescriptors.FailedToLoadTestsMap
    };

    // Act & Assert
    foreach (var descriptor in descriptors) {
      await Assert.That(descriptor.IsEnabledByDefault).IsTrue();
    }
  }

  [Test]
  public async Task ReceptorDiscovered_HasCorrectMetadataAsync() {
    // Arrange & Act
    var descriptor = DiagnosticDescriptors.ReceptorDiscovered;

    // Assert
    await Assert.That(descriptor.Id).IsEqualTo("WHIZ001");
    await Assert.That(descriptor.Title.ToString()).IsEqualTo("Receptor Discovered");
    await Assert.That(descriptor.DefaultSeverity).IsEqualTo(DiagnosticSeverity.Info);
  }

  [Test]
  public async Task InvalidReceptor_HasErrorSeverityAsync() {
    // Arrange & Act
    var descriptor = DiagnosticDescriptors.InvalidReceptor;

    // Assert
    await Assert.That(descriptor.Id).IsEqualTo("WHIZ003");
    await Assert.That(descriptor.DefaultSeverity).IsEqualTo(DiagnosticSeverity.Error);
  }

  [Test]
  public async Task AggregateIdMustBeGuid_HasErrorSeverityAsync() {
    // Arrange & Act
    var descriptor = DiagnosticDescriptors.AggregateIdMustBeGuid;

    // Assert
    await Assert.That(descriptor.Id).IsEqualTo("WHIZ005");
    await Assert.That(descriptor.DefaultSeverity).IsEqualTo(DiagnosticSeverity.Error);
  }

  [Test]
  public async Task MultipleAggregateIdAttributes_HasWarningSeverityAsync() {
    // Arrange & Act
    var descriptor = DiagnosticDescriptors.MultipleAggregateIdAttributes;

    // Assert
    await Assert.That(descriptor.Id).IsEqualTo("WHIZ006");
    await Assert.That(descriptor.DefaultSeverity).IsEqualTo(DiagnosticSeverity.Warning);
  }

  // TODO: Add tests for message format validation (ensure placeholder counts match)
  // TODO: Add tests for diagnostic ID range allocation (WHIZ001-003, WHIZ004-006, etc.)
}
