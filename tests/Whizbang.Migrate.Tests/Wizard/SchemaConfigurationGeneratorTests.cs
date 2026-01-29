using Whizbang.Migrate.Wizard;

namespace Whizbang.Migrate.Tests.Wizard;

/// <summary>
/// Tests for the SchemaConfigurationGenerator that produces appsettings.json snippets.
/// </summary>
/// <tests>Whizbang.Migrate/Wizard/SchemaConfigurationGenerator.cs:*</tests>
public class SchemaConfigurationGeneratorTests {
  [Test]
  public async Task Generate_SameDbDifferentSchema_ProducesCorrectJson_Async() {
    // Arrange
    var decisions = new SchemaDecisions {
      Strategy = SchemaStrategy.SameDbDifferentSchema,
      SchemaName = "whizbang",
      InfrastructurePrefix = "wb_",
      PerspectivePrefix = "wb_per_"
    };

    // Act
    var json = SchemaConfigurationGenerator.Generate(decisions);

    // Assert
    await Assert.That(json).Contains("\"schema\":");
    await Assert.That(json).Contains("\"name\": \"whizbang\"");
    await Assert.That(json).Contains("\"infrastructure_prefix\": \"wb_\"");
    await Assert.That(json).Contains("\"perspective_prefix\": \"wb_per_\"");
  }

  [Test]
  public async Task Generate_DifferentDbDefaultSchema_IncludesConnectionString_Async() {
    // Arrange
    var decisions = new SchemaDecisions {
      Strategy = SchemaStrategy.DifferentDbDefaultSchema,
      SchemaName = "public",
      ConnectionStringName = "WhizbangConnection"
    };

    // Act
    var json = SchemaConfigurationGenerator.Generate(decisions);

    // Assert
    await Assert.That(json).Contains("\"connection_string_name\": \"WhizbangConnection\"");
  }

  [Test]
  public async Task Generate_SameDbSameSchemaWithPrefix_UsesPublicSchema_Async() {
    // Arrange
    var decisions = new SchemaDecisions {
      Strategy = SchemaStrategy.SameDbSameSchemaWithPrefix,
      SchemaName = "public",
      InfrastructurePrefix = "wb_"
    };

    // Act
    var json = SchemaConfigurationGenerator.Generate(decisions);

    // Assert
    await Assert.That(json).Contains("\"name\": \"public\"");
    await Assert.That(json).Contains("\"infrastructure_prefix\": \"wb_\"");
  }

  [Test]
  public async Task Generate_CustomSchemaName_ReflectedInOutput_Async() {
    // Arrange
    var decisions = new SchemaDecisions {
      Strategy = SchemaStrategy.SameDbDifferentSchema,
      SchemaName = "eventsourcing"
    };

    // Act
    var json = SchemaConfigurationGenerator.Generate(decisions);

    // Assert
    await Assert.That(json).Contains("\"name\": \"eventsourcing\"");
  }

  [Test]
  public async Task Generate_DifferentDbDifferentSchema_IncludesAllSettings_Async() {
    // Arrange
    var decisions = new SchemaDecisions {
      Strategy = SchemaStrategy.DifferentDbDifferentSchema,
      SchemaName = "events",
      InfrastructurePrefix = "evt_",
      PerspectivePrefix = "evt_per_",
      ConnectionStringName = "EventStoreConnection"
    };

    // Act
    var json = SchemaConfigurationGenerator.Generate(decisions);

    // Assert
    await Assert.That(json).Contains("\"name\": \"events\"");
    await Assert.That(json).Contains("\"infrastructure_prefix\": \"evt_\"");
    await Assert.That(json).Contains("\"perspective_prefix\": \"evt_per_\"");
    await Assert.That(json).Contains("\"connection_string_name\": \"EventStoreConnection\"");
  }

  [Test]
  public async Task GenerateFullConfig_IncludesWhizbangSection_Async() {
    // Arrange
    var decisions = new SchemaDecisions {
      Strategy = SchemaStrategy.SameDbDifferentSchema,
      SchemaName = "whizbang"
    };

    // Act
    var json = SchemaConfigurationGenerator.GenerateFullConfig(decisions);

    // Assert
    await Assert.That(json).Contains("\"whizbang\":");
    await Assert.That(json).Contains("\"schema\":");
  }

  [Test]
  public async Task Generate_ReturnsValidJson_Async() {
    // Arrange
    var decisions = new SchemaDecisions {
      Strategy = SchemaStrategy.SameDbDifferentSchema,
      SchemaName = "test"
    };

    // Act
    var json = SchemaConfigurationGenerator.Generate(decisions);

    // Assert - Should be parseable as JSON
    var parsed = System.Text.Json.JsonDocument.Parse(json);
    await Assert.That(parsed).IsNotNull();
  }
}
