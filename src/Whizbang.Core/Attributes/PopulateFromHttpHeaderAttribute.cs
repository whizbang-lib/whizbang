namespace Whizbang.Core.Attributes;

/// <summary>
/// Marks a message property for automatic population from an inbound HTTP header captured at the request edge.
/// </summary>
/// <remarks>
/// <para>
/// Apply to a <see langword="string"/> property on a message type (command/event). The header value is
/// captured into the ambient scope's extensions at the HTTP boundary (via the transport's
/// <c>ExtensionHeaderMappings</c>) and rides the message context; the generated populator reads it back onto
/// the property. The value is matched by the extension key given here (case-insensitive), which should match
/// the key the edge maps the header into (by convention, the header name itself).
/// </para>
/// <para>
/// Header values are opaque strings (per W3C Trace Context, CloudEvents, and HTTP correlation conventions), so
/// the target property must be a string.
/// </para>
/// <para>
/// <strong>Example:</strong>
/// </para>
/// <code>
/// public class DocumentCreated {
///   [PopulateFromHttpHeader("X-Correlation-ID")]
///   public string? CorrelationId { get; set; }
/// }
/// </code>
/// </remarks>
/// <docs>extending/attributes/auto-populate</docs>
/// <tests>tests/Whizbang.Core.Tests/AutoPopulate/AutoPopulateHttpHeaderTests.cs</tests>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter, AllowMultiple = false, Inherited = true)]
public sealed class PopulateFromHttpHeaderAttribute(string headerName) : Attribute {
  /// <summary>
  /// Gets the header / scope-extension key the value is read from (case-insensitive).
  /// </summary>
  public string HeaderName { get; } = headerName;
}
