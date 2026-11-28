// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Attributes;

/// <summary>
/// Keeps a vector property (a <c>float[]</c> or an <c>IReadOnlyList&lt;float&gt;</c>) of an event stored as a JSON
/// array of numbers, opting it out of the packed binary32 form that event vectors are stored in by default (#2624).
/// </summary>
/// <remarks>
/// Use it when something outside the framework reads the vector out of the stored body as JSON numbers, such as a
/// SQL query or a report. The property gets no packed-form converter and no stored-vector path, so it is never packed.
/// The wire form is the JSON array either way: packing applies to the stored body only.
/// </remarks>
/// <example>
/// <code>
/// public sealed record PassageEmbedded(
///     [property: StreamId] Guid PassageId,
///     float[] Embedding,
///     [property: StoredAsJsonArray] float[] Centroid) : IEvent;
/// </code>
/// </example>
/// <docs>data/event-store#packed-vectors</docs>
/// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextStoredVectorTests.cs:Generator_AnOptedOutVector_GetsNoConverterAndNoPathAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Serialization/StoredVectorEventReadTests.cs:AnOptedOutOrDoubleVector_IsNeverReadAsPackedAsync</tests>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class StoredAsJsonArrayAttribute : Attribute;
