using System.Text.Json;
using System.Text.Json.Serialization;

namespace Whizbang.Migrate.Wizard;

/// <summary>
/// Generates appsettings.json snippets from schema configuration decisions.
/// </summary>
/// <docs>migrate-from-marten-wolverine/cli-wizard#schema-configuration</docs>
public static class SchemaConfigurationGenerator {
  /// <summary>
  /// Generates the schema section of appsettings.json.
  /// </summary>
  /// <param name="decisions">Schema decisions from the migration wizard.</param>
  /// <returns>JSON string for the schema configuration.</returns>
  public static string Generate(SchemaDecisions decisions) {
    var config = new SchemaConfig {
      Name = decisions.SchemaName,
      InfrastructurePrefix = decisions.InfrastructurePrefix,
      PerspectivePrefix = decisions.PerspectivePrefix,
      ConnectionStringName = decisions.ConnectionStringName
    };

    var wrapper = new SchemaConfigWrapper { Schema = config };
    return JsonSerializer.Serialize(wrapper, SchemaConfigJsonContext.Default.SchemaConfigWrapper);
  }

  /// <summary>
  /// Generates the full Whizbang section of appsettings.json.
  /// </summary>
  /// <param name="decisions">Schema decisions from the migration wizard.</param>
  /// <returns>JSON string for the full Whizbang configuration.</returns>
  public static string GenerateFullConfig(SchemaDecisions decisions) {
    var config = new SchemaConfig {
      Name = decisions.SchemaName,
      InfrastructurePrefix = decisions.InfrastructurePrefix,
      PerspectivePrefix = decisions.PerspectivePrefix,
      ConnectionStringName = decisions.ConnectionStringName
    };

    var wrapper = new WhizbangConfigWrapper {
      Whizbang = new WhizbangConfig { Schema = config }
    };

    return JsonSerializer.Serialize(wrapper, SchemaConfigJsonContext.Default.WhizbangConfigWrapper);
  }
}

/// <summary>
/// Schema configuration section for appsettings.json.
/// </summary>
internal sealed class SchemaConfig {
  /// <summary>
  /// Schema name.
  /// </summary>
  public string Name { get; set; } = "whizbang";

  /// <summary>
  /// Prefix for infrastructure tables.
  /// </summary>
  public string InfrastructurePrefix { get; set; } = "wb_";

  /// <summary>
  /// Prefix for perspective tables.
  /// </summary>
  public string PerspectivePrefix { get; set; } = "wb_per_";

  /// <summary>
  /// Connection string name (if different database).
  /// </summary>
  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
  public string? ConnectionStringName { get; set; }
}

/// <summary>
/// Wrapper for schema configuration.
/// </summary>
internal sealed class SchemaConfigWrapper {
  /// <summary>
  /// Schema configuration.
  /// </summary>
  public SchemaConfig Schema { get; set; } = new();
}

/// <summary>
/// Full Whizbang configuration wrapper.
/// </summary>
internal sealed class WhizbangConfigWrapper {
  /// <summary>
  /// Whizbang configuration section.
  /// </summary>
  public WhizbangConfig Whizbang { get; set; } = new();
}

/// <summary>
/// Whizbang configuration section.
/// </summary>
internal sealed class WhizbangConfig {
  /// <summary>
  /// Schema configuration.
  /// </summary>
  public SchemaConfig Schema { get; set; } = new();
}

/// <summary>
/// JSON serialization context for AOT-compatible serialization.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(SchemaConfig))]
[JsonSerializable(typeof(SchemaConfigWrapper))]
[JsonSerializable(typeof(WhizbangConfig))]
[JsonSerializable(typeof(WhizbangConfigWrapper))]
internal sealed partial class SchemaConfigJsonContext : JsonSerializerContext { }
