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
/// <docs>fundamentals/perspectives/physical-fields#jsonb-storage</docs>
public sealed record TableStorageInfo(string? DataCompression, int? ToastTupleTarget) {
  private const string ATTRIBUTE = "Whizbang.Core.Perspectives.PerspectiveTableStorageAttribute";

  /// <summary>The model's declared table storage, or null when it declares none.</summary>
  /// <param name="model">The perspective's model type.</param>
  /// <returns>The options, or null.</returns>
  public static TableStorageInfo? From(ITypeSymbol? model) {
    var attribute = model?.GetAttributes().FirstOrDefault(a => TypeNameUtilities.IsNamed(a.AttributeClass, ATTRIBUTE));
    if (attribute is null) {
      return null;
    }

    string? compression = null;
    int? target = null;
    foreach (var argument in attribute.NamedArguments) {
      if (argument.Key == "DataCompression") {
        compression = ColumnStorageSql.CompressionName(argument.Value.Value);
      } else if (argument.Key == "ToastTupleTarget" && argument.Value.Value is int value && value > 0) {
        target = value;
      }
    }

    return new TableStorageInfo(compression, target);
  }
}

/// <summary>
/// The schema-pass statements that apply a column's or a table's declared storage options: <c>SET STORAGE</c>,
/// <c>SET COMPRESSION</c>, <c>toast_tuple_target</c>, and a size budget as a check constraint.
/// </summary>
/// <remarks>
/// <para>
/// Every statement compares the catalog first and alters only what differs, so a restart issues no
/// <c>ALTER TABLE</c> and takes no lock. Both drivers' schema generators emit the same statements.
/// </para>
/// <para>
/// None of these rewrites existing rows: storage and compression apply to values written from then on, and the
/// size constraint is added <c>NOT VALID</c>, so rows already stored are not scanned.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/physical-fields#jsonb-storage</docs>
/// <tests>tests/Whizbang.Generators.Tests/ColumnStorageSqlTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/JsonbColumnStorageIntegrationTests.cs</tests>
public static class ColumnStorageSql {
  private const string DO_BEGIN = "DO $$ BEGIN\n";
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
  }

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
  public static string SetToastTupleTarget(string qualifiedTable, int target) {
    var setting = "toast_tuple_target=" + target.ToString(CultureInfo.InvariantCulture);
    return DO_BEGIN
      + $"  IF NOT EXISTS (SELECT 1 FROM pg_class WHERE oid = '{qualifiedTable}'::regclass\n"
      + $"    AND '{setting}' = ANY (coalesce(reloptions, ARRAY[]::text[]))) THEN\n"
      + $"    ALTER TABLE {qualifiedTable} SET ({setting.Replace("=", " = ")});\n"
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
