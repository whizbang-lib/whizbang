using System.Reflection;
using HotChocolate.Types;
using HotChocolate.Types.Descriptors;
using Whizbang.Core.Security.Attributes;

namespace Whizbang.Transports.HotChocolate.Middleware;

/// <summary>
/// Applies <see cref="RequirePermissionMiddleware"/> to the field, scanning both the
/// resolver method and its declaring class for <see cref="RequirePermissionAttribute"/>
/// declarations.
/// </summary>
/// <remarks>
/// <para>
/// Place this attribute on a resolver method when you also have one or more
/// <c>[RequirePermission]</c> attributes on the method or its class. The attribute does
/// nothing on its own — it just installs the middleware. Permissions and operation kind
/// (<see cref="ScopeOperation.Read"/>/<see cref="ScopeOperation.Write"/>/Any) come from
/// the <c>[RequirePermission]</c> attributes themselves.
/// </para>
/// </remarks>
/// <docs>apis/graphql/authorization#require-permission</docs>
/// <tests>tests/Whizbang.Transports.HotChocolate.Tests/Unit/RequirePermissionMiddlewareTests.cs</tests>
/// <example>
/// <code>
/// public class JobMutations {
///   [UseRequirePermission]
///   [RequirePermission("job", Operation = ScopeOperation.Write)]
///   public Task&lt;bool&gt; UpdateJob(...) { ... }
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Property, AllowMultiple = false)]
public sealed class UseRequirePermissionAttribute : ObjectFieldDescriptorAttribute {
  /// <summary>
  /// Applies the middleware to the field descriptor.
  /// </summary>
  protected override void OnConfigure(
      IDescriptorContext context,
      IObjectFieldDescriptor descriptor,
      MemberInfo member) {
    var attrs = _collectAttributes(member);
    descriptor.Use(RequirePermissionMiddleware.Create(attrs));
  }

  private static List<RequirePermissionAttribute> _collectAttributes(MemberInfo member) {
    var fromMember = member.GetCustomAttributes<RequirePermissionAttribute>(inherit: true);
    var fromDeclaringType = member.DeclaringType is { } t
      ? t.GetCustomAttributes<RequirePermissionAttribute>(inherit: true)
      : [];
    return [.. fromMember, .. fromDeclaringType];
  }
}
