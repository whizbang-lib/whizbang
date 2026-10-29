// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.CodeAnalysis;
using Whizbang.Generators.Shared.Utilities;

namespace Whizbang.Generators.Shared.Models;

/// <summary>
/// A perspective table's storage options, from <c>[PerspectiveTableStorage]</c> on the model.
/// </summary>
/// <param name="DataCompression">The <c>data</c> column's compression method (<c>pglz</c> or <c>lz4</c>), or null.</param>
/// <param name="ToastTupleTarget">The table's <c>toast_tuple_target</c>, or null.</param>
/// <param name="AutovacuumVacuumThreshold">The table's <c>autovacuum_vacuum_threshold</c>, or null.</param>
/// <param name="AutovacuumVacuumScaleFactor">The table's <c>autovacuum_vacuum_scale_factor</c>, or null.</param>
/// <param name="AutovacuumInsertThreshold">The table's <c>autovacuum_vacuum_insert_threshold</c>, or null.</param>
/// <param name="AutovacuumInsertScaleFactor">The table's <c>autovacuum_vacuum_insert_scale_factor</c>, or null.</param>
/// <param name="ToastAutovacuumVacuumThreshold">The TOAST relation's <c>autovacuum_vacuum_threshold</c>, or null.</param>
/// <param name="ToastAutovacuumVacuumScaleFactor">The TOAST relation's <c>autovacuum_vacuum_scale_factor</c>, or null.</param>
/// <param name="FillFactor">The table's <c>fillfactor</c>, or null.</param>
/// <docs>fundamentals/perspectives/physical-fields#jsonb-storage</docs>
public sealed record TableStorageInfo(
    string? DataCompression,
    int? ToastTupleTarget,
    int? AutovacuumVacuumThreshold = null,
    double? AutovacuumVacuumScaleFactor = null,
    int? AutovacuumInsertThreshold = null,
    double? AutovacuumInsertScaleFactor = null,
    int? ToastAutovacuumVacuumThreshold = null,
    double? ToastAutovacuumVacuumScaleFactor = null,
    int? FillFactor = null) {
  private const string ATTRIBUTE = "Whizbang.Core.Perspectives.PerspectiveTableStorageAttribute";

  /// <summary>The model's declared table storage, or null when it declares none.</summary>
  /// <param name="model">The perspective's model type.</param>
  /// <returns>The options, or null.</returns>
  public static TableStorageInfo? From(ITypeSymbol? model) {
    var attribute = model?.GetAttributes().FirstOrDefault(a => TypeNameUtilities.IsNamed(a.AttributeClass, ATTRIBUTE));
    if (attribute is null) {
      return null;
    }

    var arguments = attribute.NamedArguments;
    object? Argument(string name) => arguments.FirstOrDefault(a => a.Key == name).Value.Value;

    // Only a value PostgreSQL accepts is read: a negative one is the attribute's "server default", one out of range
    // is reported by the analyzer (WHIZ804) rather than failing the schema pass, and one of another type is ignored.
    return new TableStorageInfo(
      ColumnStorageSql.CompressionName(Argument("DataCompression")),
      _whole(Argument, TableStorageRange.ToastTupleTarget),
      AutovacuumVacuumThreshold: _whole(Argument, TableStorageRange.AutovacuumVacuumThreshold),
      AutovacuumVacuumScaleFactor: _factor(Argument, TableStorageRange.AutovacuumVacuumScaleFactor),
      AutovacuumInsertThreshold: _whole(Argument, TableStorageRange.AutovacuumInsertThreshold),
      AutovacuumInsertScaleFactor: _factor(Argument, TableStorageRange.AutovacuumInsertScaleFactor),
      ToastAutovacuumVacuumThreshold: _whole(Argument, TableStorageRange.ToastAutovacuumVacuumThreshold),
      ToastAutovacuumVacuumScaleFactor: _factor(Argument, TableStorageRange.ToastAutovacuumVacuumScaleFactor),
      FillFactor: _whole(Argument, TableStorageRange.FillFactor));
  }

  private static int? _whole(System.Func<string, object?> argument, TableStorageRange range) =>
    argument(range.Property) is int number && range.Accepts(number) ? number : null;

  private static double? _factor(System.Func<string, object?> argument, TableStorageRange range) =>
    argument(range.Property) is double factor && range.Accepts(factor) ? factor : null;
}

/// <summary>
/// The schema-pass statements that apply a column's or a table's declared storage options: <c>SET STORAGE</c>,
/// <c>SET COMPRESSION</c>, <c>toast_tuple_target</c>, <c>fillfactor</c>, the autovacuum settings, and a size budget
/// as a check constraint; and the <c>RESET</c> of a fillfactor or autovacuum setting the model no longer declares.
/// </summary>
/// <remarks>
/// <para>
/// Every statement compares the catalog first and alters only what differs, so a restart issues no
/// <c>ALTER TABLE</c> and takes no lock. Both drivers' schema generators emit the same statements.
/// </para>
/// <para>
/// None of these rewrites existing rows: storage and compression apply to values written from then on, and the
/// size constraint is added <c>NOT VALID</c>, so rows already stored are not scanned. A changed fillfactor queues a
/// rewrite request for the maintenance worker instead of rewriting the table during the pass.
/// </para>
/// <para>
/// The model is the authority for the fillfactor and the six autovacuum parameters the attribute can set: one it
/// does not declare is reset when the table holds it (WS-4 decision 3). Nothing records who set a parameter, so a
/// value set by hand is reset too; a parameter the attribute cannot set is never touched.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/physical-fields#jsonb-storage</docs>
/// <tests>tests/Whizbang.Generators.Tests/ColumnStorageSqlTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/JsonbColumnStorageIntegrationTests.cs</tests>
public static class ColumnStorageSql {
  private const string DO_BEGIN = "DO $$ BEGIN\n";
  private const string FILLFACTOR = "fillfactor";

  /// <summary>Stub (RED, #1747).</summary>
  public const string STORAGE_MARKER = "";
  private const string END_IF = "  END IF;\n";
  private const string DO_END = "END $$;";

  /// <summary>The storage strategy's SQL name and its <c>pg_attribute.attstorage</c> code, for a <c>ColumnStorage</c> value.</summary>
  /// <param name="value">The attribute argument's value.</param>
  /// <returns>The name, or null for <c>Default</c> or an unknown value.</returns>
  public static string? StorageName(object? value) => value switch {
    1 => "PLAIN",
    2 => "MAIN",
    3 => "EXTERNAL",
    4 => "EXTENDED",
    _ => null,
  };

  /// <summary>The compression method's SQL name for a <c>ColumnCompression</c> value.</summary>
  /// <param name="value">The attribute argument's value.</param>
  /// <returns>The name, or null for <c>Default</c> or an unknown value.</returns>
  public static string? CompressionName(object? value) => value switch {
    1 => "pglz",
    2 => "lz4",
    _ => null,
  };

  /// <summary>Copies a <c>[PhysicalField]</c>'s storage arguments onto the field.</summary>
  /// <param name="field">The field as discovered.</param>
  /// <param name="attribute">Its <c>[PhysicalField]</c>.</param>
  /// <returns>The field with its storage options.</returns>
  public static PhysicalFieldInfo WithStorage(PhysicalFieldInfo field, AttributeData attribute) {
    foreach (var argument in attribute.NamedArguments) {
      switch (argument.Key) {
        case "Storage":
          field = field with { Storage = StorageName(argument.Value.Value) };
          break;
        case "Compression":
          field = field with { Compression = CompressionName(argument.Value.Value) };
          break;
        case "MaxBytes" when argument.Value.Value is int bytes && bytes > 0:
          field = field with { MaxBytes = bytes };
          break;
      }
    }

    return field;
  }

  /// <summary>The statements for one promoted column's storage options, in the order they apply.</summary>
  /// <param name="qualifiedTable">The table as the statements name it.</param>
  /// <param name="tableName">The bare table name, which constraint names are derived from.</param>
  /// <param name="field">The promoted field.</param>
  /// <returns>Zero or more statements.</returns>
  public static IEnumerable<string> ForColumn(string qualifiedTable, string tableName, PhysicalFieldInfo field) {
    if (field.Storage is { } storage) {
      yield return SetStorage(qualifiedTable, field.ColumnName, storage);
    }

    if (field.Compression is { } compression) {
      yield return SetCompression(qualifiedTable, field.ColumnName, compression);
    }

    if (field.MaxBytes is { } bytes) {
      yield return SizeBudget(qualifiedTable, tableName, field.ColumnName, bytes);
    }
  }

  /// <summary>The statements for a table's storage options.</summary>
  /// <param name="qualifiedTable">The table as the statements name it.</param>
  /// <param name="storage">The table's options, or null.</param>
  /// <returns>Zero or more statements.</returns>
  public static IEnumerable<string> ForTable(string qualifiedTable, TableStorageInfo? storage) {
    if (storage?.DataCompression is { } compression) {
      yield return SetCompression(qualifiedTable, "data", compression);
    }

    if (storage?.ToastTupleTarget is { } target) {
      yield return SetToastTupleTarget(qualifiedTable, target);
    }

    if (storage?.FillFactor is { } fillFactor) {
      yield return SetFillFactor(qualifiedTable, fillFactor);
    }

    var autovacuum = _autovacuumParameters(storage).ToList();
    foreach (var (name, value) in autovacuum) {
      if (value is not null) {
        yield return SetStorageParameter(qualifiedTable, name, value);
      }
    }

    // The governed parameters the model does not declare: reset, so the table matches the code (#1747).
    var undeclared = autovacuum.Prepend((Name: FILLFACTOR, Value: _render(storage?.FillFactor)))
      .Where(p => p.Value is null).Select(p => p.Name).ToList();
    if (undeclared.Count > 0) {
      yield return ResetStorageParameters(qualifiedTable, undeclared);
    }
  }

  // The autovacuum parameters in emission order, each rendered as PostgreSQL stores it, or null when not declared.
  private static IEnumerable<(string Name, string? Value)> _autovacuumParameters(TableStorageInfo? storage) {
    yield return ("autovacuum_vacuum_threshold", _render(storage?.AutovacuumVacuumThreshold));
    yield return ("autovacuum_vacuum_scale_factor", _render(storage?.AutovacuumVacuumScaleFactor));
    yield return ("autovacuum_vacuum_insert_threshold", _render(storage?.AutovacuumInsertThreshold));
    yield return ("autovacuum_vacuum_insert_scale_factor", _render(storage?.AutovacuumInsertScaleFactor));
    yield return ("toast.autovacuum_vacuum_threshold", _render(storage?.ToastAutovacuumVacuumThreshold));
    yield return ("toast.autovacuum_vacuum_scale_factor", _render(storage?.ToastAutovacuumVacuumScaleFactor));
  }

  private static string? _render(int? value) => value?.ToString(CultureInfo.InvariantCulture);

  private static string? _render(double? value) => value?.ToString("R", CultureInfo.InvariantCulture);

  /// <summary><c>SET STORAGE</c>, when the column's strategy differs.</summary>
  public static string SetStorage(string qualifiedTable, string column, string storage) =>
    DO_BEGIN
    + $"  IF EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid = '{qualifiedTable}'::regclass AND attname = '{column}'\n"
    + $"    AND attstorage <> '{_storageCode(storage)}') THEN\n"
    + $"    ALTER TABLE {qualifiedTable} ALTER COLUMN {column} SET STORAGE {storage};\n"
    + END_IF
    + DO_END;

  /// <summary>The <c>pg_attribute.attstorage</c> code of a storage strategy.</summary>
  private static char _storageCode(string storage) => storage == "EXTENDED" ? 'x' : char.ToLowerInvariant(storage[0]);

  /// <summary>
  /// <c>SET COMPRESSION</c>, when the column's method differs. A server built without the method keeps the
  /// column as it is and says so in a warning, rather than failing the schema pass.
  /// </summary>
  public static string SetCompression(string qualifiedTable, string column, string compression) =>
    DO_BEGIN
    + $"  IF EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid = '{qualifiedTable}'::regclass AND attname = '{column}'\n"
    + $"    AND attcompression IS DISTINCT FROM '{compression[0]}') THEN\n"
    + $"    ALTER TABLE {qualifiedTable} ALTER COLUMN {column} SET COMPRESSION {compression};\n"
    + END_IF
    + "EXCEPTION WHEN feature_not_supported THEN\n"
    + $"  RAISE WARNING 'Whizbang: {compression} compression is not available on this server; {column} keeps its compression';\n"
    + DO_END;

  /// <summary>The table's <c>toast_tuple_target</c>, when its storage parameters do not already say so.</summary>
  public static string SetToastTupleTarget(string qualifiedTable, int target) =>
    SetStorageParameter(qualifiedTable, "toast_tuple_target", target.ToString(CultureInfo.InvariantCulture));

  /// <summary>
  /// One storage parameter, when the catalog does not already hold that value. A <c>toast.</c> parameter is
  /// compared against the TOAST relation, where PostgreSQL keeps it without the prefix.
  /// </summary>
  /// <param name="qualifiedTable">The table as the statement names it.</param>
  /// <param name="name">The parameter, such as <c>autovacuum_vacuum_threshold</c> or <c>toast.autovacuum_vacuum_threshold</c>.</param>
  /// <param name="value">The value, rendered as PostgreSQL stores it.</param>
  /// <returns>The statement.</returns>
  public static string SetStorageParameter(string qualifiedTable, string name, string value) =>
    _setStorageParameter(qualifiedTable, name, value, whenChanged: "");

  /// <summary>
  /// The table's <c>fillfactor</c>, when its storage parameters do not already say so, and then a rewrite request
  /// for a table that has pages: a changed fillfactor applies only to the pages written from then on, so the rows
  /// already stored stay packed to the old value until the table is rewritten.
  /// </summary>
  /// <remarks>
  /// The request goes through <c>wh_request_table_rewrite</c> in the table's own schema and names the bare table, as
  /// the rewriter lists the tables it measures. A restart that finds the value in place queues nothing, and neither
  /// does a table created empty.
  /// </remarks>
  /// <param name="qualifiedTable">The table as the statement names it, with or without its schema.</param>
  /// <param name="fillFactor">The fillfactor, 10 to 100.</param>
  /// <returns>The statement.</returns>
  /// <docs>fundamentals/perspectives/physical-fields#perspective-fillfactor</docs>
  /// <tests>tests/Whizbang.Generators.Tests/ColumnStorageSqlTests.cs:SetFillFactor_QueuesARewriteOnlyWhenTheValueChangesOnATableWithPagesAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/JsonbColumnStorageIntegrationTests.cs:AChangedFillFactor_OnATableWithRows_QueuesOneRewriteAsync</tests>
  public static string SetFillFactor(string qualifiedTable, int fillFactor) =>
    _setStorageParameter(qualifiedTable, FILLFACTOR, fillFactor.ToString(CultureInfo.InvariantCulture),
      whenChanged: _rewriteRequest(qualifiedTable, condition: ""));

  /// <summary>
  /// Resets the named storage parameters the table holds, in one <c>ALTER TABLE … RESET</c>, and nothing when it holds
  /// none of them. A <c>toast.</c> parameter is looked for on the TOAST relation, where PostgreSQL keeps it without the
  /// prefix. When the fillfactor is reset on a table that has pages, a rewrite is queued, as for a changed fillfactor.
  /// </summary>
  /// <remarks>
  /// The names are compared against the catalog's, so only parameters the table holds reach the statement, which is
  /// built inside the block from those names: a restart that finds none issues no <c>ALTER TABLE</c> and takes no lock.
  /// </remarks>
  /// <param name="qualifiedTable">The table as the statement names it, with or without its schema.</param>
  /// <param name="names">The parameters to reset, such as <c>fillfactor</c> or <c>toast.autovacuum_vacuum_threshold</c>.</param>
  /// <returns>The statement.</returns>
  /// <docs>fundamentals/perspectives/physical-fields#removed-storage-settings</docs>
  /// <tests>tests/Whizbang.Generators.Tests/ColumnStorageSqlTests.cs:ResetStorageParameters_AltersOnlyWhatTheCatalogHoldsAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/ColumnStorageSqlTests.cs:ResetStorageParameters_WithoutTheFillFactor_QueuesNoRewriteAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/JsonbColumnStorageIntegrationTests.cs:AnUndeclaredSetting_IsReset_AndTheRestAreLeftAloneAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/JsonbColumnStorageIntegrationTests.cs:ARemovedFillFactor_IsResetOnce_AndQueuesARewriteForATableWithRowsAsync</tests>
  /// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/Perspectives/DapperTableStorageResetTests.cs:AnUndeclaredSetting_IsReset_AndTheRestAreLeftAloneAsync</tests>
  public static string ResetStorageParameters(string qualifiedTable, IReadOnlyList<string> names) {
    var held = $"'{qualifiedTable}'::regclass";
    return "DO $$\nDECLARE\n  v_reset text[];\nBEGIN\n"
      + "  SELECT array_agg(p.name) INTO v_reset FROM (\n"
      + $"    SELECT split_part(o, '=', 1) AS name FROM pg_class c, unnest(c.reloptions) o WHERE c.oid = {held}\n"
      + "    UNION ALL\n"
      + "    SELECT 'toast.' || split_part(o, '=', 1) FROM pg_class c JOIN pg_class toast ON toast.oid = c.reltoastrelid,\n"
      + $"      unnest(toast.reloptions) o WHERE c.oid = {held}\n"
      + $"  ) p WHERE p.name IN ({string.Join(", ", names.Select(n => $"'{n}'"))});\n"
      + "  IF v_reset IS NOT NULL THEN\n"
      + $"    EXECUTE 'ALTER TABLE {qualifiedTable} RESET (' || array_to_string(v_reset, ', ') || ')';\n"
      + (names.Contains(FILLFACTOR) ? _rewriteRequest(qualifiedTable, $"'{FILLFACTOR}' = ANY (v_reset) AND ") : "")
      + END_IF
      + DO_END;
  }

  // A rewrite request for a table with pages, under an extra condition, through wh_request_table_rewrite in the table's
  // own schema. The schema prefix, if any, ends at the last dot: generated table names never contain one.
  private static string _rewriteRequest(string qualifiedTable, string condition) {
    var split = qualifiedTable.LastIndexOf('.') + 1;
    return $"    IF {condition}pg_relation_size('{qualifiedTable}'::regclass) > 0 THEN\n"
      + $"      PERFORM {qualifiedTable[..split]}wh_request_table_rewrite('{qualifiedTable[split..]}');\n"
      + "    END IF;\n";
  }

  private static string _setStorageParameter(string qualifiedTable, string name, string value, string whenChanged) {
    const string TOAST_PREFIX = "toast.";
    var held = name.StartsWith(TOAST_PREFIX, System.StringComparison.Ordinal)
      ? $"  IF NOT EXISTS (SELECT 1 FROM pg_class c JOIN pg_class toast ON toast.oid = c.reltoastrelid WHERE c.oid = '{qualifiedTable}'::regclass\n"
        + $"    AND '{name[TOAST_PREFIX.Length..]}={value}' = ANY (coalesce(toast.reloptions, ARRAY[]::text[]))) THEN\n"
      : $"  IF NOT EXISTS (SELECT 1 FROM pg_class WHERE oid = '{qualifiedTable}'::regclass\n"
        + $"    AND '{name}={value}' = ANY (coalesce(reloptions, ARRAY[]::text[]))) THEN\n";
    return DO_BEGIN
      + held
      + $"    ALTER TABLE {qualifiedTable} SET ({name} = {value});\n"
      + whenChanged
      + END_IF
      + DO_END;
  }

  /// <summary>
  /// A size budget as a check constraint on <c>pg_column_size</c>, replaced when the declared budget changes.
  /// </summary>
  public static string SizeBudget(string qualifiedTable, string tableName, string column, int bytes) {
    var name = PostgresIdentifiers.WithinLimit($"ck_{tableName}_{column}_size");
    var limit = bytes.ToString(CultureInfo.InvariantCulture);
    var existing = $"SELECT 1 FROM pg_constraint WHERE conname = '{name}' AND conrelid = '{qualifiedTable}'::regclass";
    return DO_BEGIN
      + $"  IF EXISTS ({existing} AND pg_get_constraintdef(oid) NOT LIKE '%<= {limit})%') THEN\n"
      + $"    ALTER TABLE {qualifiedTable} DROP CONSTRAINT {name};\n"
      + END_IF
      + $"  IF NOT EXISTS ({existing}) THEN\n"
      + $"    ALTER TABLE {qualifiedTable} ADD CONSTRAINT {name} CHECK (pg_column_size({column}) <= {limit}) NOT VALID;\n"
      + END_IF
      + DO_END;
  }
}
