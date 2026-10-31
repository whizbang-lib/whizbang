// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Whizbang.Generators.Shared.Models;
using Whizbang.Generators.Shared.Utilities;

namespace Whizbang.Generators;

/// <summary>
/// Reports an array of records in a perspective model: on the Entity Framework driver it makes the generator store the
/// model as one opaque document, in which no field can be indexed (#1910).
/// </summary>
/// <remarks>
/// <para>
/// Entity Framework cannot read an array of a complex element back on the mapped path, so
/// <see cref="MappedPathDiscovery"/> routes such a model to the opaque form, which reads and writes it correctly at the
/// cost of its indexing. The warning names each such array where it is declared, at any depth of the model, and the
/// collection shapes that keep the document mapped. An array of primitives is a mapped value that reads back, so it is
/// not reported.
/// </para>
/// <para>
/// A perspective model is a type a perspective in the compilation projects (<c>IPerspectiveBase&lt;TModel&gt;</c>, which
/// every perspective interface extends, or <c>IGlobalPerspectiveFor&lt;TModel, TPartitionKey&gt;</c>). The opaque form
/// is the Entity Framework driver's, so a compilation that does not reference it is not analyzed: the Dapper driver
/// serializes every model whole and makes no such choice. The warning is turned off for a whole build with
/// <c>WhizbangOpaqueArrayWarning=false</c>, and for one property with <c>#pragma warning disable WHIZ200</c> or a
/// <c>[SuppressMessage]</c>, which apply because it is reported at the property.
/// </para>
/// </remarks>
/// <docs>operations/diagnostics/whiz200</docs>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveModelArrayAnalyzerTests.cs</tests>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class PerspectiveModelArrayAnalyzer : DiagnosticAnalyzer {
  // Diagnostic IDs: WHIZ200-299 reserved for model validation
  private const string CATEGORY = "Whizbang.ModelValidation";

  /// <summary>The build property that turns the warning off for a whole compilation when <c>false</c>.</summary>
  internal const string SWITCH_PROPERTY = "build_property.WhizbangOpaqueArrayWarning";

  /// <summary>The assembly of the Entity Framework driver, whose generator stores a model opaquely.</summary>
  private const string ENTITY_FRAMEWORK_DRIVER = "Whizbang.Data.EFCore.Postgres";

  private const string PERSPECTIVE_BASE = "Whizbang.Core.Perspectives.IPerspectiveBase<TModel>";
  private const string GLOBAL_PERSPECTIVE = "Whizbang.Core.Perspectives.IGlobalPerspectiveFor<TModel, TPartitionKey>";

  /// <summary>
  /// WHIZ200: Warning - an array of records stores the perspective model as one opaque document.
  /// </summary>
  /// <docs>operations/diagnostics/whiz200</docs>
  /// <tests>tests/Whizbang.Generators.Tests/PerspectiveModelArrayAnalyzerTests.cs:AnArrayOfRecords_IsReportedAtItsProperty_SayingTheModelIsStoredOpaquelyAsync</tests>
  public static readonly DiagnosticDescriptor ArrayPropertyInPerspectiveModel = new(
      id: "WHIZ200",
      title: "An array of records stores the perspective model as one opaque document",
      messageFormat: "Property '{0}' of perspective model '{1}' is an array of records ('{2}'), so the model is stored as one opaque document and none of its fields can be indexed. Declare it as List<{3}>, IList<{3}> or ImmutableList<{3}> to keep the document mapped and indexable.",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Warning,
      isEnabledByDefault: true,
      description: "Entity Framework cannot read an array of a complex element back from a mapped JSON document, so a perspective model holding one, at any depth, is stored as one serialized document, which reads and writes correctly but has no field an index can serve. List<T>, IList<T> and ImmutableList<T> keep the document mapped. Arrays of primitives are not affected. Turn the warning off for a project with WhizbangOpaqueArrayWarning=false.");

  /// <inheritdoc/>
  public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
      [ArrayPropertyInPerspectiveModel];

  /// <inheritdoc/>
  public override void Initialize(AnalysisContext context) {
    context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
    context.EnableConcurrentExecution();
    context.RegisterCompilationStartAction(_start);
  }

  /// <summary>
  /// Analyzes nothing in a build that turned the warning off or that does not reference the Entity Framework driver;
  /// otherwise looks at each type for the models it projects.
  /// </summary>
  /// <tests>tests/Whizbang.Generators.Tests/PerspectiveModelArrayAnalyzerTests.cs:TheBuildProperty_TurnsTheWarningOffAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/PerspectiveModelArrayAnalyzerTests.cs:AProjectWithoutTheEntityFrameworkDriver_IsNotReportedAsync</tests>
  private static void _start(CompilationStartAnalysisContext context) {
    if (_isTurnedOff(context.Options.AnalyzerConfigOptionsProvider.GlobalOptions)
        || !context.Compilation.ReferencedAssemblyNames.Any(a => a.Name == ENTITY_FRAMEWORK_DRIVER)) {
      return;
    }

    // One report per property, however many perspectives project its model.
    var reported = new ConcurrentDictionary<ISymbol, byte>(SymbolEqualityComparer.Default);
    context.RegisterSymbolAction(symbolContext => _analyzePerspective(symbolContext, reported), SymbolKind.NamedType);
  }

  private static bool _isTurnedOff(AnalyzerConfigOptions options) =>
    options.TryGetValue(SWITCH_PROPERTY, out var value)
    && string.Equals(value.Trim(), "false", System.StringComparison.OrdinalIgnoreCase);

  /// <summary>Reports each array of records in the models a perspective projects, once per property.</summary>
  /// <tests>tests/Whizbang.Generators.Tests/PerspectiveModelArrayAnalyzerTests.cs:AModelTwoPerspectivesProject_IsReportedOnceAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/PerspectiveModelArrayAnalyzerTests.cs:AModelFromAnotherAssembly_IsReportedAtThePerspectiveAsync</tests>
  private static void _analyzePerspective(SymbolAnalysisContext context, ConcurrentDictionary<ISymbol, byte> reported) {
    var perspective = (INamedTypeSymbol)context.Symbol;
    if (perspective.TypeKind is not (TypeKind.Class or TypeKind.Struct)) {
      return;
    }

    foreach (var model in _modelsProjectedBy(perspective)) {
      foreach (var (property, path) in MappedPathDiscovery.ArraysOfRecords(model)) {
        if (!reported.TryAdd(property, 0)) {
          continue;
        }
        var array = (IArrayTypeSymbol)property.Type;
        // At the property, so a pragma or a SuppressMessage on it applies. A property of another project has no source
        // in this compilation (a metadata reference has none, and an IDE's project reference has another
        // compilation's, which cannot be reported here), so it is reported at the perspective that projects its model.
        var location = property.Locations.FirstOrDefault(l => l.SourceTree is { } tree && context.Compilation.ContainsSyntaxTree(tree))
          ?? perspective.Locations[0];
        context.ReportDiagnostic(Diagnostic.Create(
            ArrayPropertyInPerspectiveModel,
            location,
            path,
            model.Name,
            TypeNameUtilities.MinimallyQualified(array),
            TypeNameUtilities.MinimallyQualified(array.ElementType)));
      }
    }
  }

  /// <summary>The models a type projects: the first type argument of each perspective interface it implements.</summary>
  /// <tests>tests/Whizbang.Generators.Tests/PerspectiveModelArrayAnalyzerTests.cs:AGlobalPerspectivesModel_IsReportedAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/PerspectiveModelArrayAnalyzerTests.cs:ATypeNoPerspectiveProjects_IsNotReported_WhateverItsNameAsync</tests>
  private static IEnumerable<INamedTypeSymbol> _modelsProjectedBy(INamedTypeSymbol perspective) =>
    perspective.AllInterfaces
      .Where(i => TypeNameUtilities.Display(i.OriginalDefinition) is PERSPECTIVE_BASE or GLOBAL_PERSPECTIVE)
      .Select(i => i.TypeArguments[0])
      .OfType<INamedTypeSymbol>()
      .Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default);
}
