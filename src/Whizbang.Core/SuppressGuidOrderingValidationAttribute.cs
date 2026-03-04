namespace Whizbang.Core;

/// <summary>
/// Suppresses runtime validation warnings for non-time-ordered GUIDs used in time-sensitive contexts.
/// </summary>
/// <remarks>
/// <para>
/// When applied to a method or class, the Whizbang runtime validation will not log warnings
/// or throw exceptions when non-time-ordered (v4) GUIDs are used for event IDs, message IDs,
/// or stream IDs within that scope.
/// </para>
/// <para>
/// This attribute is useful when:
/// </para>
/// <list type="bullet">
///   <item>Migrating legacy code that uses v4 GUIDs</item>
///   <item>Internal tracking IDs that don't need time-ordering</item>
///   <item>Testing scenarios where GUID ordering is not relevant</item>
///   <item>Interoperating with external systems that provide v4 GUIDs</item>
/// </list>
/// </remarks>
/// <example>
/// <code>
/// [SuppressGuidOrderingValidation]
/// public void ProcessLegacyEvent(Guid eventId) {
///   // No warning even though eventId might be v4
///   _eventStore.Append(eventId, eventData);
/// }
/// </code>
/// </example>
/// <docs>core-concepts/whizbang-ids#suppress-ordering-validation</docs>
[AttributeUsage(
    AttributeTargets.Method |
    AttributeTargets.Class,
    AllowMultiple = false,
    Inherited = false)]
public sealed class SuppressGuidOrderingValidationAttribute : Attribute {
}
