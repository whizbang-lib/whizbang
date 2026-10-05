// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Perspectives;

/// <summary>
/// Declares that a property was removed from a perspective model, so its key is dropped from the documents
/// already stored. Put one on the model (or on the nested type that lost the property) per removed property.
/// </summary>
/// <remarks>
/// The path is relative to the type carrying the attribute, with nested keys separated by dots
/// (<c>"Shipping.Instructions"</c>). The rewrite is idempotent and journaled like every stored-form migration, and
/// never blocks startup. A physical column the property had is left in place: the framework never drops a column.
/// </remarks>
/// <param name="path">The removed property's key, relative to the type carrying the attribute.</param>
/// <docs>fundamentals/perspectives/stored-form-migrations</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/StoredFormAttributeTests.cs</tests>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = true, Inherited = false)]
public sealed class StoredFormRemovedAttribute(string path) : Attribute {
  /// <summary>The removed property's key, relative to the type carrying the attribute.</summary>
  public string Path { get; } = path;
}
