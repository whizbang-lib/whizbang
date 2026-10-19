// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using Microsoft.CodeAnalysis;

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
      DiagnosticDescriptors.CommandStreamIdDiscovered,
      DiagnosticDescriptors.MultipleStreamIdAttributes,
      DiagnosticDescriptors.PerspectiveDiscovered,
      DiagnosticDescriptors.PerspectiveSizeWarning,
      DiagnosticDescriptors.MissingStreamIdAttribute,
      DiagnosticDescriptors.StreamIdDiscovered,
      DiagnosticDescriptors.JsonSerializableTypeDiscovered,
      DiagnosticDescriptors.PerspectiveInvokerGenerated,
      DiagnosticDescriptors.WhizbangIdMustBePartial,
      DiagnosticDescriptors.TopicFilterDiscovered,
      DiagnosticDescriptors.WhizbangIdDuplicateName,
      DiagnosticDescriptors.TopicFilterOnNonCommand,
      DiagnosticDescriptors.NoTopicFiltersFound,
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
      DiagnosticDescriptors.CommandStreamIdDiscovered,
      DiagnosticDescriptors.MultipleStreamIdAttributes,
      DiagnosticDescriptors.PerspectiveDiscovered,
      DiagnosticDescriptors.PerspectiveSizeWarning,
      DiagnosticDescriptors.MissingStreamIdAttribute,
      DiagnosticDescriptors.StreamIdDiscovered,
      DiagnosticDescriptors.JsonSerializableTypeDiscovered,
      DiagnosticDescriptors.PerspectiveInvokerGenerated,
      DiagnosticDescriptors.WhizbangIdMustBePartial,
      DiagnosticDescriptors.TopicFilterDiscovered,
      DiagnosticDescriptors.WhizbangIdDuplicateName,
      DiagnosticDescriptors.TopicFilterOnNonCommand,
      DiagnosticDescriptors.NoTopicFiltersFound,
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
      DiagnosticDescriptors.CommandStreamIdDiscovered,
      DiagnosticDescriptors.MultipleStreamIdAttributes,
      DiagnosticDescriptors.PerspectiveDiscovered,
      DiagnosticDescriptors.PerspectiveSizeWarning,
      DiagnosticDescriptors.MissingStreamIdAttribute,
      DiagnosticDescriptors.StreamIdDiscovered,
      DiagnosticDescriptors.JsonSerializableTypeDiscovered,
      DiagnosticDescriptors.PerspectiveInvokerGenerated,
      DiagnosticDescriptors.WhizbangIdMustBePartial,
      DiagnosticDescriptors.TopicFilterDiscovered,
      DiagnosticDescriptors.WhizbangIdDuplicateName,
      DiagnosticDescriptors.TopicFilterOnNonCommand,
      DiagnosticDescriptors.NoTopicFiltersFound,
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
      DiagnosticDescriptors.CommandStreamIdDiscovered,
      DiagnosticDescriptors.MultipleStreamIdAttributes,
      DiagnosticDescriptors.PerspectiveDiscovered,
      DiagnosticDescriptors.PerspectiveSizeWarning,
      DiagnosticDescriptors.MissingStreamIdAttribute,
      DiagnosticDescriptors.StreamIdDiscovered,
      DiagnosticDescriptors.JsonSerializableTypeDiscovered,
      DiagnosticDescriptors.PerspectiveInvokerGenerated,
      DiagnosticDescriptors.WhizbangIdMustBePartial,
      DiagnosticDescriptors.TopicFilterDiscovered,
      DiagnosticDescriptors.WhizbangIdDuplicateName,
      DiagnosticDescriptors.TopicFilterOnNonCommand,
      DiagnosticDescriptors.NoTopicFiltersFound,
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
    await Assert.That(descriptor.Title.ToString(CultureInfo.InvariantCulture)).IsEqualTo("Receptor Discovered");
    await Assert.That(descriptor.DefaultSeverity).IsEqualTo(DiagnosticSeverity.Info);
  }

  [Test]
  public async Task RetiredDiagnosticIds_AreNeverDeclaredAgainAsync() {
    // #1427: an ID retired because nothing reported it keeps no meaning, so a suppression or a search for it
    // can never match a different diagnostic. Reads every descriptor the generators declare (all are public).
    var declared = typeof(DiagnosticDescriptors).Assembly.GetTypes()
      .SelectMany(t => t.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
      .Where(f => f.FieldType == typeof(DiagnosticDescriptor))
      .Select(f => ((DiagnosticDescriptor)f.GetValue(null)!).Id)
      .ToHashSet();

    await Assert.That(DiagnosticDescriptors.RETIRED_IDS.Where(declared.Contains).ToArray()).IsEmpty();
    await Assert.That(DiagnosticDescriptors.RETIRED_IDS.Length).IsEqualTo(10);
  }

  [Test]
  public async Task MultipleStreamIdAttributes_HasWarningSeverityAsync() {
    // Arrange & Act
    var descriptor = DiagnosticDescriptors.MultipleStreamIdAttributes;

    // Assert
    await Assert.That(descriptor.Id).IsEqualTo("WHIZ006");
    await Assert.That(descriptor.DefaultSeverity).IsEqualTo(DiagnosticSeverity.Warning);
  }

  // FUTURE: Add tests for message format validation (ensure placeholder counts match)
  // FUTURE: Add tests for diagnostic ID range allocation (WHIZ001-003, WHIZ004-006, etc.)
}
