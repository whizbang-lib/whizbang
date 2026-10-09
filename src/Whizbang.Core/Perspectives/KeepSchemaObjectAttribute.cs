// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Perspectives;

/// <summary>
/// Pins a database object on the perspective's table so Whizbang never drops it, though the model no longer
/// declares it: an index a report or another service still reads, kept while it is moved elsewhere.
/// </summary>
/// <remarks>
/// <para>
/// Whizbang drops an object it built for a perspective once the model stops declaring it. This attribute sets the
/// object's <em>code pin</em> in the managed-object ledger at every start; removing the attribute releases the pin
/// at the next start, and the object is dropped on the start after that unless a database pin also holds it.
/// </para>
/// <para>
/// The code pin is C#'s, and a DBA's pin (<c>wh_pin_object</c>, <c>whizbang schema pin</c>) is separate: each
/// keeps the object on its own, and neither releases the other.
/// </para>
/// </remarks>
/// <param name="name">The object's name, as PostgreSQL stores it (for example <c>idx_job_legacy_code</c>).</param>
/// <docs>fundamentals/perspectives/managed-schema-objects#setting-a-code-pin</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/DocumentIndexInitializationTests.cs:AnObjectTheModelPins_IsKeptAndRecordedAsPinnedByCodeAsync</tests>
/// <example>
/// <code>
/// [KeepSchemaObject("idx_job_legacy_code", Reason = "the reporting job reads it")]
/// public record JobModel {
///   // ...
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class KeepSchemaObjectAttribute(string name) : Attribute {
  /// <summary>The object's name.</summary>
  public string Name { get; } = name;

  /// <summary>Why it is kept, recorded with the pin and shown by <c>whizbang schema status</c>.</summary>
  public string? Reason { get; init; }
}
