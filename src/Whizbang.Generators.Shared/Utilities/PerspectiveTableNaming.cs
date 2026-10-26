// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Whizbang.Generators.Shared.Models;

namespace Whizbang.Generators.Shared.Utilities;

/// <summary>
/// Where a perspective model's table name comes from, read from the model before the build's table-name settings are
/// known: the base name generated from the model type, or the name its <c>[PerspectiveTable]</c> sets.
/// </summary>
/// <param name="BaseName">The model's table base name (<see cref="TypeNameUtilities.GetTableBaseName"/>).</param>
/// <param name="ManualName">The name <c>[PerspectiveTable]</c> sets, as written (empty for a null argument), or null.</param>
/// <docs>fundamentals/perspectives/table-naming</docs>
/// <tests>tests/Whizbang.Generators.Tests/Utilities/PerspectiveTableNamingTests.cs</tests>
public sealed record PerspectiveTableSource(string BaseName, string? ManualName) {
  /// <summary>
  /// The table name: the manual name exactly as written, or the generated one under <paramref name="config"/>.
  /// </summary>
  /// <param name="config">The build's suffix-stripping settings; they never alter a manual name.</param>
  /// <returns>The table name.</returns>
  public string TableName(TableNameConfig config) =>
    ManualName ?? NamingConventionUtilities.GenerateTableName(BaseName, config);

  /// <summary>True when <c>[PerspectiveTable]</c> names the table.</summary>
  public bool IsManual => ManualName is not null;
}

/// <summary>
/// The one rule every generator names a perspective table by (#1719): from the MODEL type, never the perspective class,
/// unless the model carries <c>[PerspectiveTable]</c>. The EF Core and schema generators both read it here so the two
/// drivers cannot name one model differently again, and both report a bad name through the descriptors here.
/// </summary>
/// <docs>fundamentals/perspectives/table-naming</docs>
/// <tests>tests/Whizbang.Generators.Tests/Utilities/PerspectiveTableNamingTests.cs</tests>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveTableNameGoldenTests.cs</tests>
public static class PerspectiveTableNaming {
  /// <summary>The attribute that names a model's table.</summary>
  public const string ATTRIBUTE = "Whizbang.Core.Perspectives.PerspectiveTableAttribute";

  /// <summary>The prefix every perspective table carries, a manual name included.</summary>
  public const string PREFIX = "wh_per_";

  /// <summary>PostgreSQL's identifier limit, in bytes.</summary>
  public const int POSTGRES_MAX_IDENTIFIER_BYTES = 63;

  /// <summary>Reads where the model's table name comes from.</summary>
  /// <param name="modelType">The perspective's model type.</param>
  /// <returns>The base name and the manual name, if any.</returns>
  public static PerspectiveTableSource From(ITypeSymbol modelType) {
    var attribute = modelType.GetAttributes().FirstOrDefault(a => TypeNameUtilities.IsNamed(a.AttributeClass, ATTRIBUTE));
    var manual = attribute is null ? null : (attribute.ConstructorArguments[0].Value as string ?? string.Empty);
    return new PerspectiveTableSource(TypeNameUtilities.GetTableBaseName(modelType), manual);
  }

  /// <summary>
  /// True when <paramref name="name"/> may name a perspective table: <see cref="PREFIX"/> followed by at least one
  /// lowercase ASCII letter, digit or underscore. The generated SQL writes the name unquoted, which PostgreSQL folds to
  /// lowercase, so only a lowercase name reaches every statement unchanged.
  /// </summary>
  /// <param name="name">The name <c>[PerspectiveTable]</c> sets.</param>
  /// <returns>True when valid.</returns>
  public static bool IsValidManualName(string name) =>
    name.Length > PREFIX.Length
    && name.StartsWith(PREFIX, StringComparison.Ordinal)
    && name.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_');

  /// <summary>
  /// The name a released Dapper build gave the perspective's table, as PostgreSQL stored it, or null when it is the
  /// model's table name already. That build named the table from the perspective class (the CLR name past its
  /// namespace, nested names merged) under the same suffix settings, and never wrote the perspective registry, so
  /// this name is the only way to find the table on upgrade (#1719).
  /// </summary>
  /// <param name="perspectiveClrTypeName">The perspective's CLR type name (<c>Outer+Perspective</c> when nested).</param>
  /// <param name="tableName">The model's table name now.</param>
  /// <param name="config">The build's suffix settings.</param>
  /// <returns>The former name, or null.</returns>
  public static string? FormerDapperTableName(string perspectiveClrTypeName, string tableName, TableNameConfig config) {
    var baseName = perspectiveClrTypeName[(perspectiveClrTypeName.LastIndexOf('.') + 1)..].Replace("+", "");
    var former = AsStored(NamingConventionUtilities.GenerateTableName(baseName, config));
    return former == tableName ? null : former;
  }

  /// <summary>
  /// <paramref name="name"/> as PostgreSQL stores it: clipped to <see cref="POSTGRES_MAX_IDENTIFIER_BYTES"/> UTF-8
  /// bytes, on a character boundary, which is what PostgreSQL does with a longer identifier instead of refusing it.
  /// </summary>
  /// <param name="name">The identifier as written.</param>
  /// <returns>The identifier as stored.</returns>
  public static string AsStored(string name) {
    var end = Math.Min(name.Length, POSTGRES_MAX_IDENTIFIER_BYTES);
    while (Encoding.UTF8.GetByteCount(name[..end]) > POSTGRES_MAX_IDENTIFIER_BYTES
        || (end < name.Length && char.IsLowSurrogate(name[end]))) {
      end--;
    }
    return name[..end];
  }

  /// <summary>
  /// One WHIZ825 per table that more than one model resolves to, at the second model's declaration, naming them all.
  /// </summary>
  /// <param name="tables">Each perspective's table, model (display name) and model location, in discovery order.</param>
  /// <returns>The diagnostics.</returns>
  public static IEnumerable<Diagnostic> Collisions(IEnumerable<(string Table, string Model, Location? Location)> tables) =>
    tables
      .GroupBy(t => t.Table, StringComparer.Ordinal)
      .Select(byTable => byTable.GroupBy(t => t.Model, StringComparer.Ordinal).Select(byModel => byModel.First()).ToList())
      .Where(models => models.Count > 1)
      .Select(models => Diagnostic.Create(
        TableNameCollision,
        models[1].Location,
        models[0].Table,
        string.Join(", ", models.Select(m => m.Model).OrderBy(m => m, StringComparer.Ordinal))));

  /// <summary>
  /// WHIZ823: Error - A perspective table name exceeds the database's identifier limit (schema generator).
  /// </summary>
  /// <docs>operations/diagnostics/whiz823</docs>
  /// <tests>tests/Whizbang.Generators.Tests/PerspectiveTableNamingDiagnosticsTests.cs:SchemaGenerator_WithAGeneratedNameOverTheLimit_ReportsWHIZ823AndCreatesNoTableAsync</tests>
  public static readonly DiagnosticDescriptor TableNameExceedsLimit = new(
      id: "WHIZ823",
      title: "Perspective table name exceeds the database limit",
      messageFormat: "Perspective model '{0}' has table name '{1}' ({2} bytes), over the PostgreSQL limit of {3} bytes, "
          + "which PostgreSQL would truncate. Shorten the model name, configure suffix stripping, or name the table with [PerspectiveTable].",
      category: "Whizbang.SourceGeneration",
      defaultSeverity: DiagnosticSeverity.Error,
      isEnabledByDefault: true,
      description: "PostgreSQL truncates an identifier past 63 bytes without an error, so two long names can become one "
          + "table. The schema generator (the Dapper driver's DDL) refuses the name instead; the EF Core generator reports "
          + "the same condition as WHIZ820.");

  /// <summary>
  /// WHIZ824: Error - A name set with [PerspectiveTable] is not a valid perspective table name.
  /// </summary>
  /// <docs>operations/diagnostics/whiz824</docs>
  /// <tests>tests/Whizbang.Generators.Tests/PerspectiveTableNamingDiagnosticsTests.cs:SchemaGenerator_WithAnInvalidManualName_ReportsWHIZ824Async</tests>
  /// <tests>tests/Whizbang.Generators.Tests/PerspectiveTableNamingDiagnosticsTests.cs:EFCoreGenerator_WithAnInvalidManualName_ReportsWHIZ824AndMapsNoTableAsync</tests>
  public static readonly DiagnosticDescriptor InvalidManualTableName = new(
      id: "WHIZ824",
      title: "Invalid perspective table name",
      messageFormat: "[PerspectiveTable(\"{1}\")] on perspective model '{0}' is not a valid table name: it must start with "
          + "'wh_per_' followed by lowercase letters, digits and underscores",
      category: "Whizbang.SourceGeneration",
      defaultSeverity: DiagnosticSeverity.Error,
      isEnabledByDefault: true,
      description: "The generated SQL writes a perspective table's name unquoted, which PostgreSQL folds to lowercase, and "
          + "the framework recognizes its perspective tables by the 'wh_per_' prefix. A name outside those rules would "
          + "create one table and read another.");

  /// <summary>
  /// WHIZ825: Error - More than one perspective model resolves to the same table.
  /// </summary>
  /// <docs>operations/diagnostics/whiz825</docs>
  /// <tests>tests/Whizbang.Generators.Tests/PerspectiveTableNamingDiagnosticsTests.cs:SchemaGenerator_WithTwoModelsResolvingToOneTable_ReportsWHIZ825Async</tests>
  /// <tests>tests/Whizbang.Generators.Tests/PerspectiveTableNamingDiagnosticsTests.cs:EFCoreGenerator_WithTwoModelsResolvingToOneTable_ReportsWHIZ825Async</tests>
  public static readonly DiagnosticDescriptor TableNameCollision = new(
      id: "WHIZ825",
      title: "Two perspective models share one table name",
      messageFormat: "Perspective table '{0}' is the table of more than one model ({1}). Rename a model, or name its table with [PerspectiveTable].",
      category: "Whizbang.SourceGeneration",
      defaultSeverity: DiagnosticSeverity.Error,
      isEnabledByDefault: true,
      description: "Each model has one table, shared by every perspective of that model. Two different models in one "
          + "table would overwrite each other's rows, so the build stops instead.");
}
