// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Perspectives;

/// <summary>
/// Reads the promoted columns of the row a <see cref="FieldStorageMode.Split"/> model is being loaded from.
/// </summary>
/// <remarks>
/// A Split field lives only in its column, so a store hands one of these to the generated
/// <see cref="SplitPhysicalFieldMap{TModel}"/>, which copies each column into the model it just read from
/// the document. The store owns the conversion from its column types, the same conversion it applies in
/// reverse when it writes them.
/// </remarks>
/// <docs>fundamentals/perspectives/physical-fields</docs>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/Perspectives/DapperSplitPhysicalFieldReloadTests.cs</tests>
public interface IPhysicalColumnReader {
  /// <summary>Reads a column as the model property's type, or its default when the column is null.</summary>
  /// <typeparam name="T">The model property's declared type.</typeparam>
  /// <param name="column">The column name, exactly as the model's mapping declares it.</param>
  T Read<T>(string column);

  /// <summary>Reads a vector column as its components, or null when the column is null.</summary>
  /// <param name="column">The column name, exactly as the model's mapping declares it.</param>
  float[]? GetVector(string column);
}
