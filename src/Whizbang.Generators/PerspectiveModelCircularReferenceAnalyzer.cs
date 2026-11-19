// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Whizbang.Generators.Shared.Models;
using Whizbang.Generators.Shared.Utilities;

namespace Whizbang.Generators;

/// <summary>
/// Reports a perspective model whose recursive types form a circular reference, which cannot be saved (WHIZ201, owner
/// decision on #2112).
/// </summary>
/// <remarks>
/// <para>
/// A tree or a linked list is writable: each instance has one container. A recursion that stores a collection and also a
/// single reference back up the document (<c>Node.Children</c> with <c>Node.Parent</c>, <c>Order.Lines</c> with
/// <c>Line.Order</c>) lets an element reach its own container, so writing the document has no end. Both drivers write
/// the document whole, so the error does not depend on the driver. The rule and what it cannot see are on
/// <see cref="CircularReferenceDiscovery"/>; an object cycle it cannot see fails the write loudly instead.
/// </para>
/// <para>
/// Reported at the back-reference, once per property however many perspectives project its model, or at the
/// perspective when the model is another assembly's. A reference left out of the document (<c>[JsonIgnore]</c>) is not
/// judged.
/// </para>
/// </remarks>
/// <docs>operations/diagnostics/whiz201</docs>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveModelCircularReferenceAnalyzerTests.cs</tests>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class PerspectiveModelCircularReferenceAnalyzer : DiagnosticAnalyzer {
  private const string CATEGORY = "Whizbang.ModelValidation";

  /// <summary>WHIZ201: Error - a perspective model holds a circular reference and cannot be saved.</summary>
  /// <docs>operations/diagnostics/whiz201</docs>
  /// <tests>tests/Whizbang.Generators.Tests/PerspectiveModelCircularReferenceAnalyzerTests.cs:AParentBackPointer_IsAnErrorAtTheBackReferenceAsync</tests>
  public static readonly DiagnosticDescriptor CircularReferenceInPerspectiveModel = new(
      id: "WHIZ201",
      title: "A perspective model holds a circular reference and cannot be saved",
      messageFormat: "Property '{0}' of perspective model '{1}' refers back to '{2}', which holds it through the collection '{3}', so an instance can reach its own container and the document cannot be saved. Leave the reference out of the stored model ([JsonIgnore]) and restore it after reading, or store the container's identifier instead.",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Error,
      isEnabledByDefault: true,
      description: "A perspective document is written whole. A tree (a type holding a collection of itself) and a linked list (a single reference to the next) are writable, because every instance has one container. A recursion that stores a collection and also a single-valued reference back up the document (Node.Children with Node.Parent, Order.Lines with Line.Order) lets an element reach its container, so the document has no end. An object cycle this cannot see fails the write with the serializer's cycle detection.");

  /// <inheritdoc/>
  public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [CircularReferenceInPerspectiveModel];

  /// <inheritdoc/>
  public override void Initialize(AnalysisContext context) {
    context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
    context.EnableConcurrentExecution();
    context.RegisterCompilationStartAction(start => {
      // One report per property, however many perspectives project its model.
      var reported = new ConcurrentDictionary<ISymbol, byte>(SymbolEqualityComparer.Default);
      start.RegisterSymbolAction(symbolContext => _analyzePerspective(symbolContext, reported), SymbolKind.NamedType);
    });
  }

  /// <summary>Reports each back-reference in the models a perspective projects, once per property.</summary>
  /// <tests>tests/Whizbang.Generators.Tests/PerspectiveModelCircularReferenceAnalyzerTests.cs:AModelTwoPerspectivesProject_IsReportedOnceAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/PerspectiveModelCircularReferenceAnalyzerTests.cs:AModelFromAReferencedAssembly_IsReportedAtThePerspectiveAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/PerspectiveModelCircularReferenceAnalyzerTests.cs:AnInterfaceExtendingAPerspectiveInterface_IsNotAPerspectiveAsync</tests>
  private static void _analyzePerspective(SymbolAnalysisContext context, ConcurrentDictionary<ISymbol, byte> reported) {
    var perspective = (INamedTypeSymbol)context.Symbol;
    if (perspective.TypeKind is not (TypeKind.Class or TypeKind.Struct)) {
      return;
    }

    foreach (var model in PerspectiveModelArrayAnalyzer.ModelsProjectedBy(perspective)) {
      foreach (var circular in CircularReferenceDiscovery.Find(model)) {
        if (!reported.TryAdd(circular.BackReference, 0)) {
          continue;
        }
        var location = circular.BackReference.Locations
            .FirstOrDefault(l => l.SourceTree is { } tree && context.Compilation.ContainsSyntaxTree(tree))
          ?? perspective.Locations[0];
        context.ReportDiagnostic(Diagnostic.Create(
            CircularReferenceInPerspectiveModel, location, circular.Path, TypeNameUtilities.SimpleName(model), TypeNameUtilities.SimpleName(circular.Target), circular.ContainerPath));
      }
    }
  }
}
