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
/// Reports an array of records, or a recursive type, in a perspective model: on the Entity Framework driver either makes
/// the generator store the model as one opaque document, in which no field can be indexed (#1910, #2112).
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
/// <c>WhizbangOpaqueArrayWarning=false</c> (which turns off WHIZ202 too), and for one property with
/// <c>#pragma warning disable WHIZ200</c> (or WHIZ202) or a
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

  /// <summary>
  /// WHIZ202: Info - a recursive type stores the perspective model as one opaque document (#2112).
  /// </summary>
  /// <remarks>
  /// The same consequence as the array warning (no field can be indexed), reported by the same analyzer under the same
  /// switch, with its own id because it means something else: there is nothing to change. A recursive type stays opaque
  /// in every collection shape, because the mapped path stores only its first level and drops every level below it with
  /// no error, so recommending a <c>List</c> here would lead an author from a whole tree into a truncated one.
  /// </remarks>
  /// <docs>operations/diagnostics/whiz202</docs>
  /// <tests>tests/Whizbang.Generators.Tests/PerspectiveModelArrayAnalyzerTests.cs:AnArrayOfItself_IsReportedAsRecursive_WithoutTheListAdviceAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/PerspectiveModelArrayAnalyzerTests.cs:ACollectionOfItself_IsReportedAsRecursiveAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/PerspectiveModelArrayAnalyzerTests.cs:AnArrayOfARecursiveType_IsReportedAsRecursiveAsync</tests>
  public static readonly DiagnosticDescriptor RecursiveTypeInPerspectiveModel = new(
      id: "WHIZ202",
      title: "A recursive type stores the perspective model as one opaque document",
      messageFormat: "Property '{0}' of perspective model '{1}' holds '{2}', which is recursive (a type in it holds itself), so the model is stored as one opaque document and none of its fields can be indexed. Keep it opaque: the mapped path would store only the first level of a recursive type, in any collection shape, and drop the rest.",
      category: CATEGORY,
      // Info, not Warning (owner decision on #2112): the model is stored correctly and there is nothing to change; the
      // report explains why it has no indexes.
      defaultSeverity: DiagnosticSeverity.Info,
      isEnabledByDefault: true,
      description: "Entity Framework maps a recursive complex type (a tree node holding its children, or two types holding each other) one level deep and drops every level below it on write, with no error at model build, save or query. A perspective model holding one is therefore stored as one serialized document, which keeps every level. Rows an earlier release wrote on the mapped path hold only the first level; the rest was never written, and re-projecting the perspective from its events restores it. Turn the warning off for a project with WhizbangOpaqueArrayWarning=false.");

  /// <inheritdoc/>
  public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
      [ArrayPropertyInPerspectiveModel, RecursiveTypeInPerspectiveModel];

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
  /// <tests>tests/Whizbang.Generators.Tests/PerspectiveModelArrayAnalyzerTests.cs:AModelFromAReferencedAssembly_IsReportedAtThePerspectiveAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/PerspectiveModelArrayAnalyzerTests.cs:AnInterfaceExtendingAPerspectiveInterface_IsNotAPerspectiveAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/PerspectiveModelArrayAnalyzerTests.cs:AStructPerspectivesModel_IsReportedAsync</tests>
  private static void _analyzePerspective(SymbolAnalysisContext context, ConcurrentDictionary<ISymbol, byte> reported) {
    var perspective = (INamedTypeSymbol)context.Symbol;
    if (perspective.TypeKind is not (TypeKind.Class or TypeKind.Struct)) {
      return;
    }

    foreach (var model in ModelsProjectedBy(perspective)) {
      // Recursion first: an array that closes a cycle is reported once, as recursive, never with the List advice.
      foreach (var (property, path, _) in MappedPathDiscovery.RecursiveMembers(model)) {
        if (reported.TryAdd(property, 0)) {
          _report(context, perspective, RecursiveTypeInPerspectiveModel, property, path, model, _declared(property.Type));
        }
      }

      foreach (var (property, path) in MappedPathDiscovery.ArraysOfRecords(model)) {
        if (!reported.TryAdd(property, 0)) {
          continue;
        }
        var array = (IArrayTypeSymbol)property.Type;
        // An array of a type that is recursive further down would be opaque as a List too, so the List advice would
        // change nothing; it is told why instead.
        if (array.ElementType is INamedTypeSymbol element && MappedPathDiscovery.IsRecursive(element)) {
          _report(context, perspective, RecursiveTypeInPerspectiveModel, property, path, model, _declared(array));
        } else {
          _report(context, perspective, ArrayPropertyInPerspectiveModel, property, path, model,
            TypeNameUtilities.MinimallyQualified(array), TypeNameUtilities.MinimallyQualified(array.ElementType));
        }
      }
    }
  }

  /// <summary>The type as declared, without the nullable annotation, which the advice is not about.</summary>
  private static string _declared(ITypeSymbol type) =>
    TypeNameUtilities.MinimallyQualified(type.WithNullableAnnotation(NullableAnnotation.NotAnnotated));

  /// <summary>Reports one property of a model with the descriptor's message arguments after the path and model name.</summary>
  private static void _report(
      SymbolAnalysisContext context, INamedTypeSymbol perspective, DiagnosticDescriptor descriptor,
      IPropertySymbol property, string path, INamedTypeSymbol model, params string[] types) {
    // At the property, so a pragma or a SuppressMessage on it applies. A property of another project has no source in
    // this compilation (a metadata reference has none, and an IDE's project reference has another compilation's, which
    // cannot be reported here), so it is reported at the perspective that projects its model.
    var location = property.Locations.FirstOrDefault(l => l.SourceTree is { } tree && context.Compilation.ContainsSyntaxTree(tree))
      ?? perspective.Locations[0];
    context.ReportDiagnostic(Diagnostic.Create(descriptor, location, [path, model.Name, .. types]));
  }

  /// <summary>The models a type projects: the first type argument of each perspective interface it implements.</summary>
  /// <tests>tests/Whizbang.Generators.Tests/PerspectiveModelArrayAnalyzerTests.cs:AGlobalPerspectivesModel_IsReportedAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/PerspectiveModelArrayAnalyzerTests.cs:ATypeNoPerspectiveProjects_IsNotReported_WhateverItsNameAsync</tests>
  internal static IEnumerable<INamedTypeSymbol> ModelsProjectedBy(INamedTypeSymbol perspective) =>
    perspective.AllInterfaces
      .Where(i => TypeNameUtilities.Display(i.OriginalDefinition) is PERSPECTIVE_BASE or GLOBAL_PERSPECTIVE)
      .Select(i => i.TypeArguments[0])
      .OfType<INamedTypeSymbol>()
      .Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default);
}
