// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Whizbang.Generators.Shared.Models;
using Whizbang.Generators.Shared.Utilities;

namespace Whizbang.Generators.Analyzers;

/// <summary>
/// Reports a <c>[PerspectiveTableStorage]</c> value outside the range PostgreSQL accepts (WHIZ804, #1592): a
/// <c>FillFactor</c> outside 10 to 100, a <c>ToastTupleTarget</c> outside 128 to 8160, or an autovacuum scale factor
/// above 100. The schema generators leave such a value out, so the schema pass does not fail on the server at startup;
/// this says so where the value is declared. A negative value keeps the server's setting and is not reported.
/// </summary>
/// <docs>operations/diagnostics/whiz804</docs>
/// <tests>tests/Whizbang.Generators.Tests/Analyzers/PerspectiveTableStorageAnalyzerTests.cs</tests>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PerspectiveTableStorageAnalyzer : DiagnosticAnalyzer {
  private const string ATTRIBUTE = "Whizbang.Core.Perspectives.PerspectiveTableStorageAttribute";

  /// <inheritdoc/>
  public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [DiagnosticDescriptors.TableStorageValueOutOfRange];

  /// <inheritdoc/>
  public override void Initialize(AnalysisContext context) {
    context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
    context.EnableConcurrentExecution();
    context.RegisterSymbolAction(_analyzeType, SymbolKind.NamedType);
  }

  private static void _analyzeType(SymbolAnalysisContext context) {
    var type = (INamedTypeSymbol)context.Symbol;
    var attribute = type.GetAttributes().FirstOrDefault(a => TypeNameUtilities.IsNamed(a.AttributeClass, ATTRIBUTE));
    if (attribute is null) {
      return;
    }

    var location = LocationUtilities.ApplicationOrFallback(attribute, Location.None, context.CancellationToken);
    foreach (var argument in attribute.NamedArguments) {
      var range = TableStorageRange.All.FirstOrDefault(r => r.Property == argument.Key);
      if (range is null || !range.IsOutOfRange(argument.Value.Value)) {
        continue;
      }

      context.ReportDiagnostic(Diagnostic.Create(
        DiagnosticDescriptors.TableStorageValueOutOfRange, location, type.Name, range.Property,
        System.Convert.ToString(argument.Value.Value, CultureInfo.InvariantCulture),
        range.Minimum.ToString(CultureInfo.InvariantCulture), range.Maximum.ToString(CultureInfo.InvariantCulture)));
    }
  }
}
