namespace Whizbang.Generators.Shared.Models;

/// <summary>
/// Configuration for perspective table naming.
/// This record uses value equality which is critical for incremental generator performance.
/// Configuration is read from MSBuild properties via ConfigurationUtilities.
/// </summary>
/// <param name="StripSuffixes">Whether to strip common suffixes from model names (default: true)</param>
/// <param name="SuffixesToStrip">Array of suffixes to strip (default: Model, Projection, ReadModel, Dto, View)</param>
/// <docs>v1.0.0/perspectives/table-naming</docs>
/// <tests>tests/Whizbang.Generators.Tests/Utilities/NamingConventionUtilitiesTests.cs:StripConfigurableSuffixes_WhenEnabled_StripsMatchingSuffixAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/Utilities/NamingConventionUtilitiesTests.cs:GenerateTableName_WithProjection_GeneratesCorrectTableNameAsync</tests>
public sealed record TableNameConfig(
    bool StripSuffixes,
    string[] SuffixesToStrip
) {
  /// <summary>
  /// Default configuration: strip common suffixes (Model, Projection, ReadModel, Dto, View).
  /// </summary>
  public static TableNameConfig Default { get; } = new(
      StripSuffixes: true,
      SuffixesToStrip: new[] { "ReadModel", "Model", "Projection", "Dto", "View" }
  );

  /// <summary>
  /// Configuration that preserves all suffixes (no stripping).
  /// </summary>
  public static TableNameConfig NoStripping { get; } = new(
      StripSuffixes: false,
      SuffixesToStrip: Array.Empty<string>()
  );
}
