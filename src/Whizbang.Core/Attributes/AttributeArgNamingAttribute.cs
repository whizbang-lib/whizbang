using System;

namespace Whizbang.Core.Attributes;

/// <summary>
/// Declares the naming convention the <c>MessageTagDiscoveryGenerator</c> should use when
/// mapping positional constructor parameter names to property-initializer names on a
/// tag-attribute subclass. Apply to a subclass of <see cref="MessageTagAttribute"/> when
/// the parameter→property mapping isn't standard PascalCase.
/// </summary>
/// <remarks>
/// <para>
/// Without this attribute the generator defaults to <see cref="AttributeArgNamingConvention.PascalCase"/> —
/// constructor parameter <c>tagValue</c> maps to property <c>TagValue</c>, etc. That covers
/// the common C# case.
/// </para>
/// <para>
/// Example:
/// <code>
/// [AttributeArgNaming(AttributeArgNamingConvention.PascalCase)]
/// public class NotificationTagAttribute : MessageTagAttribute {
///   public string? TagValue { get; init; }
///   public NotificationTagAttribute(string tag, string tagValue) {
///     Tag = tag;
///     TagValue = tagValue;
///   }
/// }
/// </code>
/// </para>
/// </remarks>
/// <remarks>Constructs the attribute with the supplied convention.</remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = true, AllowMultiple = false)]
public sealed class AttributeArgNamingAttribute(AttributeArgNamingConvention convention) : Attribute {
  /// <summary>The naming convention applied to constructor parameter names.</summary>
  public AttributeArgNamingConvention Convention { get; } = convention;
}
