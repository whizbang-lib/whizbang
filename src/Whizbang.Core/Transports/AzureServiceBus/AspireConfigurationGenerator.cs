using System.Globalization;
using System.Text;

namespace Whizbang.Core.Transports.AzureServiceBus;

/// <summary>
/// Generates Aspire AppHost configuration code for Service Bus topics and subscriptions.
/// </summary>
/// <tests>tests/Whizbang.Core.Tests/Transports/AzureServiceBus/AspireConfigurationGeneratorTests.cs:GenerateAppHostCode_WithNoRequirements_ReturnsEmptyMessageAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Transports/AzureServiceBus/AspireConfigurationGeneratorTests.cs:GenerateAppHostCode_WithSingleRequirement_GeneratesCorrectCodeAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Transports/AzureServiceBus/AspireConfigurationGeneratorTests.cs:GenerateAppHostCode_WithMultipleRequirements_GeneratesCorrectCodeAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Transports/AzureServiceBus/AspireConfigurationGeneratorTests.cs:GenerateAppHostCode_GroupsByTopic_WhenMultipleSubscriptionsAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Transports/AzureServiceBus/AspireConfigurationGeneratorTests.cs:GenerateAppHostCode_WithServiceName_IncludesServiceNameInCommentsAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Transports/AzureServiceBus/AspireConfigurationGeneratorTests.cs:GenerateAppHostCode_IncludesHeaderAndFooterAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Transports/AzureServiceBus/AspireConfigurationGeneratorTests.cs:GenerateAppHostCode_GeneratesValidCSharpSyntaxAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Transports/AzureServiceBus/AspireConfigurationGeneratorTests.cs:GenerateAppHostCode_SortsTopicsAlphabeticallyAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Transports/AzureServiceBus/AspireConfigurationGeneratorTests.cs:GenerateAppHostCode_WithSpecialCharacters_EscapesCorrectlyAsync</tests>
public static class AspireConfigurationGenerator {
  private static readonly char[] _separators = ['-', '_'];

  /// <summary>
  /// Generates C# code for configuring Service Bus topics in Aspire AppHost.
  /// </summary>
  /// <param name="requirements">Collection of topic and subscription requirements</param>
  /// <param name="serviceName">Optional service name to include in comments</param>
  /// <returns>Generated C# code as a string</returns>
  public static string GenerateAppHostCode(
    IEnumerable<TopicRequirement> requirements,
    string? serviceName = null) {
    var requirementsList = requirements.ToList();

    // Handle empty case
    if (requirementsList.Count == 0) {
      return "// === Whizbang Service Bus Configuration ===\n" +
             "// No Service Bus topics required\n" +
             "// ==========================================";
    }

    var sb = new StringBuilder();

    // Header
    sb.AppendLine("// === Whizbang Service Bus Configuration ===");
    if (!string.IsNullOrWhiteSpace(serviceName)) {
      sb.AppendLine(CultureInfo.InvariantCulture, $"// Service Bus topics for {serviceName} service");
    } else {
      sb.AppendLine("// Add this to your AppHost Program.cs:");
    }
    sb.AppendLine();

    // Group by topic name and sort alphabetically
    var topicGroups = requirementsList
      .GroupBy(r => r.TopicName)
      .OrderBy(g => g.Key)
      .ToList();

    // Generate code for each topic
    foreach (var topicGroup in topicGroups) {
      var topicName = topicGroup.Key;
      var variableName = _toCamelCase(topicName) + "Topic";

      sb.AppendLine(CultureInfo.InvariantCulture, $"var {variableName} = serviceBus.AddServiceBusTopic(\"{topicName}\");");

      foreach (var requirement in topicGroup.OrderBy(r => r.SubscriptionName)) {
        sb.AppendLine(CultureInfo.InvariantCulture, $"{variableName}.AddServiceBusSubscription(\"{requirement.SubscriptionName}\");");
      }

      sb.AppendLine();
    }

    // Footer
    sb.AppendLine("// ==========================================");

    return sb.ToString();
  }

  /// <summary>
  /// Converts a string to camelCase for variable naming.
  /// </summary>
  private static string _toCamelCase(string input) {
    if (string.IsNullOrWhiteSpace(input)) {
      return input;
    }

    // Remove hyphens and underscores, capitalize after them
    var parts = input.Split(_separators, StringSplitOptions.RemoveEmptyEntries);

    if (parts.Length == 0) {
      return input;
    }

    var sb = new StringBuilder();

    // First part is lowercase
    sb.Append(char.ToLowerInvariant(parts[0][0]));
    if (parts[0].Length > 1) {
      sb.Append(parts[0].AsSpan(1));
    }

    // Remaining parts are capitalized
    for (int i = 1; i < parts.Length; i++) {
      if (parts[i].Length > 0) {
        sb.Append(char.ToUpperInvariant(parts[i][0]));
        if (parts[i].Length > 1) {
          sb.Append(parts[i].AsSpan(1));
        }
      }
    }

    return sb.ToString();
  }
}
