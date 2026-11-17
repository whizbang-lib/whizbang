// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Perspectives;

/// <summary>
/// The names of a perspective row's own columns, for <see cref="PerspectiveIndexAttribute"/> to index beside the
/// model's properties.
/// </summary>
/// <remarks>
/// <para>
/// A perspective row carries columns of its own besides the model's document: when it was created and when it was
/// last written. A model property is named with <c>nameof</c>; one of these columns is named with a constant from
/// here, so an index can lead with it, follow a property with it, or cover it alone with a predicate.
/// </para>
/// <para>
/// <strong>No index on <c>updated_at</c> is built unless something asks for it.</strong> Every update rewrites the
/// column, and a btree over it keeps PostgreSQL from making the update HOT, so a table that carried it paid for it on
/// every write. A table that declares row retention keeps it, because the retention sweeps range-scan the column;
/// every other table has it only when its model declares it here. A query that sorts or filters on
/// <c>UpdatedAt</c> in a table without retention declares the index it needs.
/// </para>
/// <para>
/// Each name begins with <c>$</c>, which no C# identifier can, so a model's own property called <c>UpdatedAt</c>
/// stays a field in the document: <c>nameof(UpdatedAt)</c> indexes the property, and
/// <see cref="UpdatedAt"/> indexes the row's column.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// [PerspectiveIndex(PerspectiveRowColumns.UpdatedAt)]
/// [PerspectiveIndex(nameof(TenantId), PerspectiveRowColumns.UpdatedAt)]
/// [PerspectiveIndex(PerspectiveRowColumns.UpdatedAt, Where = "(data -&gt;&gt; 'Status') = 'open'")]
/// public record TicketModel {
///   public Guid TenantId { get; init; }
///   public string Status { get; init; } = "";
/// }
/// </code>
/// </example>
/// <docs>fundamentals/perspectives/perspective-indexes#the-updated_at-index</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/PerspectiveRowColumnsTests.cs</tests>
/// <tests>tests/Whizbang.Generators.Tests/UpdatedAtIndexGenerationTests.cs</tests>
public static class PerspectiveRowColumns {
#pragma warning disable IDE1006 // Named like the properties they stand beside in a [PerspectiveIndex], as TenantConstants.AllTenants is
  /// <summary>The row's <c>updated_at</c> column: when it was last written.</summary>
  public const string UpdatedAt = "$updated_at";

  /// <summary>The row's <c>created_at</c> column: when it was first written.</summary>
  public const string CreatedAt = "$created_at";
#pragma warning restore IDE1006
}
