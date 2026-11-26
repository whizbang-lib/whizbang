// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core;

/// <summary>
/// Opts one message type out of the event and message id time-ordering check, deliberately and visibly: a non-v7 id of
/// that type is accepted where it would be refused, and is still logged and counted.
/// </summary>
/// <remarks>
/// <para>
/// <b>On a message type</b> (a class, record, struct or record struct), every entry point that checks an event or
/// message id (the dispatcher's cascades, the work coordinator strategies, the EF Core and Dapper outbox, inbox and
/// dead-letter writes, and the event store appends) treats a non-v7 id of exactly this type as if
/// <c>GuidOrderingViolationSeverity</c> were at most <c>Warning</c>: at the default <c>Error</c> the id is accepted, logged
/// once as a warning naming this attribute, and counted in <c>whizbang.guid_ordering.non_v7_message_ids</c>. A lower
/// configured severity is kept (<c>Info</c> logs at information, <c>None</c> is silent); the attribute never raises it.
/// </para>
/// <para>
/// What it never does: let <see cref="Guid.Empty"/> through (refused at every severity, attribute or not); apply to a
/// derived type (it is not inherited, so each type opts out on its own); change stream id reporting; or relax the strict
/// <c>MessageId.From</c> and the JSON converter, so a non-v7 id arriving on the wire is still refused at deserialization.
/// The type's ids are then ordered by their bytes, not by when they were minted, wherever version assignment, apply
/// order and replay read the time in an id: use it only for ids whose source you cannot change.
/// </para>
/// <para>
/// <b>On a method</b> (or on a type, for the code inside it), the attribute is a compile-time opt-out: WHIZ037, the
/// warning for a parsed or constructed Guid flowing into a message, event or stream id, is not reported inside it. It
/// changes nothing at run time: the ids that code produces are checked by their message type, as above.
/// </para>
/// <para>
/// The attribute is read by the source generator, which registers each attributed type by name at load, so the check
/// reads no attribute by reflection.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // Ids come from a legacy system that issues UUIDv4 and cannot be changed
/// [SuppressGuidOrderingValidation]
/// public sealed record LegacyOrderImported(Guid OrderId) : IEvent;
///
/// public static class LegacyImport {
///   [SuppressGuidOrderingValidation] // no WHIZ037 for the parse below
///   public static MessageId IdFor(string legacyId) => new(Guid.Parse(legacyId));
/// }
/// </code>
/// </example>
/// <docs>fundamentals/identity/whizbang-ids#time-ordering-checks</docs>
/// <tests>tests/Whizbang.Core.Tests/Validation/GuidOrderingSuppressionTests.cs</tests>
/// <tests>tests/Whizbang.Generators.Tests/GuidOrderingSuppressionRegistryGeneratorTests.cs</tests>
/// <tests>tests/Whizbang.Generators.Tests/NonTimeOrderedIdAnalyzerTests.cs:AMethodCarryingTheSuppression_IsQuiet_AndItsNeighborStillReportsAsync</tests>
[AttributeUsage(
    AttributeTargets.Method |
    AttributeTargets.Class |
    AttributeTargets.Struct,
    AllowMultiple = false,
    Inherited = false)]
public sealed class SuppressGuidOrderingValidationAttribute : Attribute;
