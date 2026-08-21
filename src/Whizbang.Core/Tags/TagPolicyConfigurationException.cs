namespace Whizbang.Core.Tags;

/// <summary>
/// A tag-policy configuration the host cannot run as declared — an application tag minting a
/// reserved <c>sys-*</c> name, or a message type whose tags match more than one coalesce
/// binding. Thrown at host start by <see cref="TagPolicyValidator"/>; failing loudly here is
/// the point, because the alternative is silently picking one interpretation and shipping
/// under a policy nobody declared.
/// </summary>
/// <docs>fundamentals/messages/message-tags#validation</docs>
/// <tests>tests/Whizbang.Core.Tests/Tags/TagPolicyValidatorTests.cs:Validate_UserTagWithSysPrefix_ThrowsAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Tags/TagPolicyValidatorTests.cs:Validate_TypeMatchingTwoCoalesceBindings_ThrowsNamingTypeAndBothTagsAsync</tests>
public sealed class TagPolicyConfigurationException : Exception {
  /// <summary>Creates the exception with a message describing the policy violation.</summary>
  public TagPolicyConfigurationException(string message) : base(message) { }

  /// <summary>Creates the exception with a message and an underlying cause.</summary>
  public TagPolicyConfigurationException(string message, Exception innerException)
    : base(message, innerException) { }

  /// <summary>Creates the exception with no message.</summary>
  public TagPolicyConfigurationException() { }
}
