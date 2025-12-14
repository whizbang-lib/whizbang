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
    if (!requirementsList.Any()) {
      return "// === Whizbang Service Bus Configuration ===\n" +
             "// No Service Bus topics required\n" +
             "// ==========================================";
    }

    var sb = new StringBuilder();

    // Header
    sb.AppendLine("// === Whizbang Service Bus Configuration ===");
    if (!string.IsNullOrWhiteSpace(serviceName)) {
      sb.AppendLine($"// Service Bus topics for {serviceName} service");
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
      var variableName = ToCamelCase(topicName) + "Topic";

      sb.AppendLine($"var {variableName} = serviceBus.AddServiceBusTopic(\"{topicName}\");");

      foreach (var requirement in topicGroup.OrderBy(r => r.SubscriptionName)) {
        sb.AppendLine($"{variableName}.AddServiceBusSubscription(\"{requirement.SubscriptionName}\");");
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
  private static string ToCamelCase(string input) {
    if (string.IsNullOrWhiteSpace(input)) {
      return input;
    }

    // Remove hyphens and underscores, capitalize after them
    var parts = input.Split(new[] { '-', '_' }, StringSplitOptions.RemoveEmptyEntries);

    if (parts.Length == 0) {
      return input;
    }

    var sb = new StringBuilder();

    // First part is lowercase
    sb.Append(char.ToLowerInvariant(parts[0][0]));
    if (parts[0].Length > 1) {
      sb.Append(parts[0].Substring(1));
    }

    // Remaining parts are capitalized
    for (int i = 1; i < parts.Length; i++) {
      if (parts[i].Length > 0) {
        sb.Append(char.ToUpperInvariant(parts[i][0]));
        if (parts[i].Length > 1) {
          sb.Append(parts[i].Substring(1));
        }
      }
    }

    return sb.ToString();
  }
}
