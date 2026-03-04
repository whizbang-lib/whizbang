namespace Whizbang.Core;

/// <summary>
/// Suppresses TrackedGuid interception for Guid.NewGuid() and Guid.CreateVersion7() calls
/// within the decorated method, type, or assembly.
/// </summary>
/// <remarks>
/// <para>
/// When applied to a method, class, struct, or assembly, the Whizbang source generator
/// will not intercept GUID creation calls within that scope. This is useful when:
/// </para>
/// <list type="bullet">
///   <item>Performance is critical and tracking overhead is unacceptable</item>
///   <item>Interoperating with code that expects raw Guid types</item>
///   <item>Testing scenarios where tracking is not needed</item>
///   <item>Internal tracking IDs that don't need time-ordering validation</item>
/// </list>
/// </remarks>
/// <example>
/// <code>
/// // Suppress at method level
/// [SuppressGuidInterception]
/// public void CreateInternalTrackingId() {
///   var trackingId = Guid.NewGuid(); // Not intercepted
/// }
///
/// // Suppress at class level
/// [SuppressGuidInterception]
/// public class LegacyIdGenerator {
///   public Guid Generate() => Guid.NewGuid(); // Not intercepted
/// }
/// </code>
/// </example>
/// <docs>core-concepts/whizbang-ids#suppress-interception</docs>
[AttributeUsage(
    AttributeTargets.Method |
    AttributeTargets.Class |
    AttributeTargets.Struct |
    AttributeTargets.Assembly,
    AllowMultiple = false,
    Inherited = false)]
public sealed class SuppressGuidInterceptionAttribute : Attribute {
}
