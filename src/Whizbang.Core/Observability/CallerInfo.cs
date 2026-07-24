namespace Whizbang.Core.Observability;

/// <summary>
/// Default implementation of <see cref="ICallerInfo"/>.
/// Immutable sealed record for AOT compatibility and value semantics.
/// </summary>
/// <tests>Whizbang.Core.Tests/Observability/CallerInfoTests.cs</tests>
public sealed record CallerInfo(
    string CallerMemberName,
    string CallerFilePath,
    int CallerLineNumber) : ICallerInfo {
  /// <summary>
  /// Returns a human-readable representation of the caller location.
  /// Format: "MemberName (FilePath:LineNumber)"
  /// </summary>
  public override string ToString() =>
      $"{CallerMemberName} ({CallerFilePath}:{CallerLineNumber})";
}
