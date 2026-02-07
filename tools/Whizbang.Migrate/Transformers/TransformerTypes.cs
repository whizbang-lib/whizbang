namespace Whizbang.Migrate.Transformers;

/// <summary>
/// Result of a code transformation.
/// </summary>
/// <param name="OriginalCode">The original source code.</param>
/// <param name="TransformedCode">The transformed source code.</param>
/// <param name="Changes">List of changes made.</param>
/// <param name="Warnings">Any warnings generated during transformation.</param>
public sealed record TransformationResult(
    string OriginalCode,
    string TransformedCode,
    IReadOnlyList<CodeChange> Changes,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Represents a single code change.
/// </summary>
/// <param name="LineNumber">Line number where change occurred.</param>
/// <param name="ChangeType">Type of change made.</param>
/// <param name="Description">Human-readable description of the change.</param>
/// <param name="OriginalText">The original text that was changed.</param>
/// <param name="NewText">The new text after transformation.</param>
public sealed record CodeChange(
    int LineNumber,
    ChangeType ChangeType,
    string Description,
    string OriginalText,
    string NewText);

/// <summary>
/// Type of code change.
/// </summary>
public enum ChangeType {
  /// <summary>Interface was replaced.</summary>
  InterfaceReplacement,

  /// <summary>Base class was replaced.</summary>
  BaseClassReplacement,

  /// <summary>Method signature was changed.</summary>
  MethodSignatureChange,

  /// <summary>Using directive was added.</summary>
  UsingAdded,

  /// <summary>Using directive was removed.</summary>
  UsingRemoved,

  /// <summary>Using directive was replaced.</summary>
  UsingReplaced,

  /// <summary>Attribute was removed.</summary>
  AttributeRemoved,

  /// <summary>Attribute was replaced with a different attribute.</summary>
  AttributeReplaced,

  /// <summary>Type name was changed.</summary>
  TypeRename,

  /// <summary>Namespace was changed.</summary>
  NamespaceChange,

  /// <summary>Method call was replaced.</summary>
  MethodCallReplacement,

  /// <summary>Method was replaced with a different method.</summary>
  MethodReplaced,

  /// <summary>Method was transformed (e.g., ShouldDelete to Apply returning ModelAction).</summary>
  MethodTransformed
}
