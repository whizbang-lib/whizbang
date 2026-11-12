// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Whizbang.Generators.Shared.Utilities;
using Whizbang.Generators.Utilities;

namespace Whizbang.Generators.Analyzers;

/// <summary>
/// Checks the stream a strong-id <c>[StreamId]</c> inherits from a source message (#2072). <c>InheritFrom</c> on
/// <c>[StreamId]</c> names the strong-id types, beyond the property's own, whose stream a cascaded message may take: a listed
/// type that is not a strong id is WHIZ018, the option on a stream id that is not a strong id is WHIZ019, and listing
/// the stream id's own type, which is always accepted, is WHIZ034. A message that implements <c>IHasStreamId</c> is
/// written through that interface, never through the strong-id <c>[StreamId]</c>, which is WHIZ035.
/// </summary>
/// <remarks>
/// An analyzer rather than the stream-id generator reports these, because it sees the compilation with the strong-id
/// generator's output in it: an id that generator emits from a property's <c>[WhizbangId]</c> is a complete strong id
/// here, and an unresolved type to a sibling generator.
/// </remarks>
/// <docs>extending/attributes/streamid#inheriting-a-stream-of-another-id-type</docs>
/// <tests>tests/Whizbang.Generators.Tests/Analyzers/StreamIdInheritFromAnalyzerTests.cs</tests>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class StreamIdInheritFromAnalyzer : DiagnosticAnalyzer {
  private const string I_HAS_STREAM_ID = "global::Whizbang.Core.IHasStreamId";

  /// <inheritdoc/>
  public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [
    DiagnosticDescriptors.InheritFromNotAStrongId,
    DiagnosticDescriptors.InheritFromOnAStreamIdThatIsNotAStrongId,
    DiagnosticDescriptors.InheritFromListsTheStreamIdsOwnType,
    DiagnosticDescriptors.HasStreamIdOverStrongIdStreamId,
  ];

  /// <inheritdoc/>
  public override void Initialize(AnalysisContext context) {
    context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
    context.EnableConcurrentExecution();
    context.RegisterSymbolAction(_analyzeType, SymbolKind.NamedType);
  }

  private static void _analyzeType(SymbolAnalysisContext context) {
    var type = (INamedTypeSymbol)context.Symbol;
    if (StreamIdSelection.Select(type) is not { } choice) {
      return;
    }
    // Reported where the [StreamId] is declared, not again on every type that inherits it
    var declaredHere = SymbolEqualityComparer.Default.Equals(choice.Property.ContainingType, type);
    var strongId = StrongIdStreamIds.Resolve(choice.Property, choice.Parameter);
    if (declaredHere) {
      _checkInheritFrom(context, type, choice, strongId);
    }
    if (strongId is not null && TypeNameHelper.ImplementsInterface(type, I_HAS_STREAM_ID)) {
      _checkHasStreamId(context, type, choice, declaredHere);
    }
  }

  /// <summary>WHIZ019, or WHIZ018 and WHIZ034 for each listed type, on the <c>[StreamId]</c> that lists them.</summary>
  private static void _checkInheritFrom(
      SymbolAnalysisContext context, INamedTypeSymbol type, StreamIdSelection.Choice choice, StrongIdStreamIds.StrongId? strongId) {
    var listed = StrongIdStreamIds.InheritFrom(choice.Attribute);
    if (listed.IsEmpty) {
      return;
    }
    var location = LocationUtilities.ApplicationOrFallback(choice.Attribute, Location.None, context.CancellationToken);
    if (strongId is null) {
      context.ReportDiagnostic(Diagnostic.Create(
        DiagnosticDescriptors.InheritFromOnAStreamIdThatIsNotAStrongId, location,
        type.Name, choice.Property.Name, choice.Property.Type.Name));
      return;
    }
    foreach (var listedType in listed) {
      if (TypeNameUtilities.FullyQualified(listedType) == strongId.TypeName) {
        context.ReportDiagnostic(Diagnostic.Create(
          DiagnosticDescriptors.InheritFromListsTheStreamIdsOwnType, location, type.Name, choice.Property.Name, listedType.Name));
      } else if (!StrongIdStreamIds.IsStrongIdType(listedType)) {
        context.ReportDiagnostic(Diagnostic.Create(
          DiagnosticDescriptors.InheritFromNotAStrongId, location, listedType.Name, type.Name, choice.Property.Name));
      }
    }
  }

  /// <summary>
  /// WHIZ035, once: on the strong-id <c>[StreamId]</c> when this type declares it, else on the type when it is the one
  /// that lists <c>IHasStreamId</c> over an inherited strong-id stream id.
  /// </summary>
  private static void _checkHasStreamId(
      SymbolAnalysisContext context, INamedTypeSymbol type, StreamIdSelection.Choice choice, bool declaredHere) {
    if (!declaredHere && !type.Interfaces.Any(i => TypeNameUtilities.IsFullyQualifiedNamed(i, I_HAS_STREAM_ID))) {
      return;
    }
    var location = declaredHere
      ? LocationUtilities.ApplicationOrFallback(choice.Attribute, Location.None, context.CancellationToken)
      : LocationUtilities.FirstOrNone(type);
    context.ReportDiagnostic(Diagnostic.Create(
      DiagnosticDescriptors.HasStreamIdOverStrongIdStreamId, location, type.Name, choice.Property.Name));
  }
}
