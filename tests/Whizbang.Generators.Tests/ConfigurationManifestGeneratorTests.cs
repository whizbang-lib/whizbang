// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Generators;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Tests for the generator that records, per assembly, each configuration section bound from a literal key, the
/// keys under it, and the capability each attributed setting requires.
/// </summary>
/// <remarks>
/// The capability verdict has to name the features that need a capability and the setting that turned each one
/// on. Reading that from options types at run time would be reflection, so the generator writes it down at
/// compile time from code that already exists: the binding call and the bound type. Only the requirement is
/// declared, as an attribute on the setting or on the enum member that selects the feature.
/// </remarks>
[Category("Generators")]
public class ConfigurationManifestGeneratorTests {
  internal const string MANIFEST = "ConfigurationManifest.g.cs";

  [Test]
  [RequiresAssemblyFiles]
  public async Task AttributedProperty_IsRequiredUnderItsKeyAsync() {
    const string source = """
      using Microsoft.Extensions.Configuration;
      using Whizbang.Core.Capabilities;
      namespace App;
      public sealed class AdvisorOptions {
        [RequiresPostgresExtension("pg_stat_statements", Feature = "Expensive predicate advice")]
        public bool ReadStatements { get; set; }
        public int Limit { get; set; }
      }
      public static class Binding {
        public static void Apply(IConfiguration configuration, AdvisorOptions options) =>
          configuration.GetSection("Whizbang:Advisor").Bind(options);
      }
      """;

    var (manifest, errors) = Run(source);

    await Assert.That(errors).IsEmpty().Because("the generated manifest must compile in the assembly that binds the section");
    await Assert.That(manifest).Contains(Binding("Whizbang:Advisor", "global::App.AdvisorOptions"));
    await Assert.That(manifest).Contains("\"Whizbang:Advisor:ReadStatements\", \"Whizbang:Advisor:Limit\"");
    await Assert.That(manifest).Contains(Requirement("postgres", "pg_stat_statements", "Expensive predicate advice", "Whizbang:Advisor:ReadStatements", null))
      .Because("an attributed setting requires the capability under its own key, with no value: it is on when set");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task AttributedEnumMember_IsRequiredWhenTheKeySelectsItAsync() {
    const string source = """
      using Microsoft.Extensions.Configuration;
      using Whizbang.Core.Capabilities;
      namespace App;
      public enum TableRewriteStrategy {
        None,
        [RequiresPostgresExtension("pg_squeeze", Feature = "Online table rewrite")]
        PgSqueeze,
        [RequiresCapability("postgres", "pg_repack")]
        PgRepack,
      }
      public sealed class MaintenanceOptions {
        public TableRewriteStrategy? TableRewriteStrategy { get; set; }
      }
      public static class Binding {
        public static void Apply(IConfiguration configuration, MaintenanceOptions options) =>
          ConfigurationBinder.Bind(configuration.GetSection("Whizbang:Workers:Maintenance"), options);
      }
      """;

    var (manifest, errors) = Run(source);

    await Assert.That(errors).IsEmpty();
    await Assert.That(manifest).Contains("\"Whizbang:Workers:Maintenance:TableRewriteStrategy\"");
    await Assert.That(manifest).Contains(Requirement("postgres", "pg_squeeze", "Online table rewrite", "Whizbang:Workers:Maintenance:TableRewriteStrategy", "PgSqueeze"))
      .Because("an attributed enum member requires the capability when its key is set to that member");
    await Assert.That(manifest).Contains(Requirement("postgres", "pg_repack", "PgRepack", "Whizbang:Workers:Maintenance:TableRewriteStrategy", "PgRepack"))
      .Because("a requirement with no feature named is named after the member that declares it");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task NestedSection_KeysAndRequirementsCarryTheFullPathAsync() {
    const string source = """
      using Microsoft.Extensions.Configuration;
      using Whizbang.Core.Capabilities;
      namespace App;
      public sealed class PartitioningOptions {
        [RequiresPostgresExtension("pg_partman", Feature = "Partition management")]
        [ConfigurationKeyName("Managed")]
        public bool UsePartman { get; set; }
      }
      public sealed class RetentionOptions {
        public System.TimeSpan Age { get; set; }
      }
      public sealed class LedgerOptions {
        public PartitioningOptions Partitioning { get; } = new();
        public RetentionOptions Retention { get; set; } = new();
      }
      public static class Binding {
        public static LedgerOptions? Read(IConfiguration configuration) =>
          configuration.GetSection("Whizbang").GetSection("Ledger").Get<LedgerOptions>();
      }
      """;

    var (manifest, errors) = Run(source);

    await Assert.That(errors).IsEmpty();
    await Assert.That(manifest).Contains(Binding("Whizbang:Ledger", "global::App.LedgerOptions"))
      .Because("a chain of literal GetSection calls is one section path");
    await Assert.That(manifest).Contains("\"Whizbang:Ledger:Partitioning:Managed\", \"Whizbang:Ledger:Retention:Age\"")
      .Because("a nested options object is a nested section; a renamed property is read under its configured name; a TimeSpan is a value, not a section");
    await Assert.That(manifest).Contains(Requirement("postgres", "pg_partman", "Partition management", "Whizbang:Ledger:Partitioning:Managed", null));
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task DictionaryAndList_EntriesAreAWildcardSegmentAsync() {
    const string source = """
      using System.Collections.Generic;
      using Microsoft.Extensions.Configuration;
      using Microsoft.Extensions.DependencyInjection;
      using Whizbang.Core.Capabilities;
      namespace App;
      public sealed class DatabaseOptions {
        [RequiresPostgresExtension("vector", Feature = "Vector fields")]
        public bool Vectors { get; set; }
      }
      public sealed class StorageOptions {
        public Dictionary<string, DatabaseOptions> Databases { get; set; } = new();
        public IDictionary<string, int> Limits { get; set; } = new Dictionary<string, int>();
        public List<string> Names { get; set; } = new();
        public string[] Tags { get; set; } = [];
      }
      public static class Binding {
        public static IServiceCollection Apply(IServiceCollection services, IConfiguration configuration) =>
          services.Configure<StorageOptions>(configuration.GetSection("Whizbang:Storage"));
      }
      """;

    var (manifest, errors) = Run(source);

    await Assert.That(errors).IsEmpty();
    await Assert.That(manifest).Contains(
      "\"Whizbang:Storage:Databases:*:Vectors\", \"Whizbang:Storage:Limits:*\", \"Whizbang:Storage:Names:*\", \"Whizbang:Storage:Tags:*\"")
      .Because("a dictionary entry or list item is a name the consumer chooses, recorded as '*'");
    await Assert.That(manifest).Contains(Requirement("postgres", "vector", "Vector fields", "Whizbang:Storage:Databases:*:Vectors", null));
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task PrivateOptionsType_IsSkippedAsync() {
    const string source = """
      using Microsoft.Extensions.Configuration;
      using Whizbang.Core.Capabilities;
      namespace App;
      public sealed class VisibleOptions {
        public bool On { get; set; }
      }
      public static class Holder {
        private sealed class HiddenOptions {
          [RequiresPostgresExtension("pg_trgm")]
          public bool On { get; set; }
        }
        public static void Apply(IConfiguration configuration) {
          configuration.GetSection("Whizbang:Hidden").Bind(new HiddenOptions());
          configuration.GetSection("Whizbang:Visible").Bind(new VisibleOptions());
        }
      }
      """;

    var (manifest, errors) = Run(source);

    await Assert.That(errors).IsEmpty()
      .Because("the manifest names each options type, and generated code cannot name a private one");
    await Assert.That(manifest).Contains(Binding("Whizbang:Visible", "global::App.VisibleOptions"));
    await Assert.That(manifest).DoesNotContain("Whizbang:Hidden");
    await Assert.That(manifest).DoesNotContain("pg_trgm");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task SecondAssembly_ReadsTheFirstAssemblysAttributesAndRegistersItsOwnManifestAsync() {
    const string library = """
      using Microsoft.Extensions.Configuration;
      using Microsoft.Extensions.DependencyInjection;
      using Whizbang.Core.Capabilities;
      namespace ThirdParty.Storage;
      public enum RewriteMode {
        Off,
        [RequiresPostgresExtension("pg_squeeze", Feature = "Online rewrite")]
        Squeeze,
      }
      public sealed class RewriteOptions {
        public RewriteMode Mode { get; set; }
      }
      public static class Registration {
        public static IServiceCollection AddRewrite(this IServiceCollection services, IConfiguration configuration) =>
          services.Configure<RewriteOptions>(configuration.GetSection("ThirdParty:Rewrite"));
      }
      """;
    const string consumer = """
      using Microsoft.Extensions.Configuration;
      using Microsoft.Extensions.DependencyInjection;
      namespace Consumer.App;
      public static class Startup {
        public static void Apply(IServiceCollection services, IConfiguration configuration) =>
          services.AddOptions<ThirdParty.Storage.RewriteOptions>().Bind(configuration.GetSection("Consumer:Rewrite"));
      }
      """;

    var (libraryManifest, libraryErrors) = Run(library, "ThirdParty.Storage");
    var libraryImage = EmitImage(library, "ThirdParty.Storage");
    var (consumerManifest, consumerErrors) = Run(consumer, "Consumer.App", libraryImage);

    await Assert.That(libraryErrors).IsEmpty();
    await Assert.That(consumerErrors).IsEmpty()
      .Because("both assemblies carry a generated manifest class, and the consumer must still compile against the library");
    await Assert.That(libraryManifest).Contains("\"ThirdParty.Storage\"");
    await Assert.That(libraryManifest).Contains(Requirement("postgres", "pg_squeeze", "Online rewrite", "ThirdParty:Rewrite:Mode", "Squeeze"));
    await Assert.That(consumerManifest).Contains("\"Consumer.App\"");
    await Assert.That(consumerManifest).Contains(Binding("Consumer:Rewrite", "global::ThirdParty.Storage.RewriteOptions"));
    await Assert.That(consumerManifest).Contains(Requirement("postgres", "pg_squeeze", "Online rewrite", "Consumer:Rewrite:Mode", "Squeeze"))
      .Because("an options type from a referenced assembly carries its attributes in metadata");
    await Assert.That(consumerManifest).DoesNotContain("ThirdParty:Rewrite")
      .Because("each assembly registers only the sections it binds itself");
  }

  /// <summary>Runs the generator and returns the manifest source (empty when none) and the compilation's errors.</summary>
  [RequiresAssemblyFiles]
  internal static (string Manifest, ImmutableArray<Diagnostic> Errors) Run(
      string source, string assemblyName = "TestAssembly", params MetadataReference[] references) {
    var (result, output) = GeneratorTestHelper.RunGeneratorWithFullReferences(
      new ConfigurationManifestGenerator(), source, assemblyName, references);
    var manifest = GeneratorTestHelper.GetGeneratedSource(result, MANIFEST) ?? string.Empty;
    return (manifest, [.. output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error)]);
  }

  /// <summary>Compiles a source with the generator and returns it as a metadata reference.</summary>
  [RequiresAssemblyFiles]
  internal static MetadataReference EmitImage(string source, string assemblyName) {
    var (_, output) = GeneratorTestHelper.RunGeneratorWithFullReferences(new ConfigurationManifestGenerator(), source, assemblyName);
    using var image = new MemoryStream();
    var emitted = output.Emit(image);
    if (!emitted.Success) {
      throw new InvalidOperationException("The first assembly did not compile: " + string.Join("; ", emitted.Diagnostics));
    }
    return MetadataReference.CreateFromImage(image.ToArray());
  }

  /// <summary>The generated opening of one binding.</summary>
  internal static string Binding(string section, string optionsType) =>
    $"new global::Whizbang.Core.Configuration.ConfigurationBinding(\"{section}\", typeof({optionsType}),";

  /// <summary>The generated text of one requirement.</summary>
  internal static string Requirement(string provider, string name, string feature, string key, string? value) =>
    "new global::Whizbang.Core.Capabilities.CapabilityRequirement("
    + $"new global::Whizbang.Core.Capabilities.CapabilityId(\"{provider}\", \"{name}\"), \"{feature}\", \"{key}\", "
    + (value is null ? "null" : $"\"{value}\"") + ")";
}
