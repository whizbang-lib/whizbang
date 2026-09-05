using Whizbang.Migrate.Transformers;

namespace Whizbang.Migrate.Tests.Transformers;

/// <summary>
/// Tests for the Newtonsoft.Json to System.Text.Json transformer.
/// </summary>
/// <remarks>
/// This transformer rewrites a consumer's source in place, so the risk is not that it fails
/// loudly -- it is that it rewrites something it should have left alone, or stays silent about
/// a construct it cannot translate. Newtonsoft and System.Text.Json disagree on semantics in
/// places (converters especially), so "changed nothing and said nothing" is the dangerous
/// outcome, not the safe one. These tests pin both halves: what it rewrites, and what it
/// refuses to rewrite while warning.
/// </remarks>
/// <tests>Whizbang.Migrate/Transformers/NewtonsoftToSystemTextJsonTransformer.cs:*</tests>
public class NewtonsoftToSystemTextJsonTransformerTests {

  private const string NO_NEWTONSOFT_SOURCE = """
    using System;

    public class OrderService {
      public string Name { get; set; } = "";
    }
    """;

  [Test]
  public async Task TransformAsync_FileWithoutNewtonsoft_IsLeftByteForByteAsync() {
    // The migrator runs across an entire solution. A file that never used Newtonsoft must come
    // back untouched -- not merely equivalent, identical -- or the tool produces diff noise in
    // files it had no business editing.
    var transformer = new NewtonsoftToSystemTextJsonTransformer();

    var result = await transformer.TransformAsync(NO_NEWTONSOFT_SOURCE, "OrderService.cs");

    await Assert.That(result.TransformedCode).IsEqualTo(NO_NEWTONSOFT_SOURCE);
    await Assert.That(result.Changes).IsEmpty();
  }

  [Test]
  public async Task TransformAsync_JsonPropertyWithName_BecomesJsonPropertyNameAsync() {
    // The rename that carries the wire contract. Getting this wrong silently changes the JSON
    // field name a consumer's API emits.
    var transformer = new NewtonsoftToSystemTextJsonTransformer();
    const string source = """
      using Newtonsoft.Json;

      public class Order {
        [JsonProperty("order_id")]
        public string Id { get; set; } = "";
      }
      """;

    var result = await transformer.TransformAsync(source, "Order.cs");

    await Assert.That(result.TransformedCode).Contains("JsonPropertyName(\"order_id\")");
    await Assert.That(result.TransformedCode).DoesNotContain("JsonProperty(\"order_id\")");
    await Assert.That(result.Changes.Any(c => c.ChangeType == ChangeType.AttributeReplaced)).IsTrue();
  }

  [Test]
  public async Task TransformAsync_JsonPropertyRequiredAlways_BecomesJsonRequiredAsync() {
    // Required.Always has no argument-level equivalent; it maps to a separate attribute. Left
    // untranslated, a required field silently becomes optional after migration.
    var transformer = new NewtonsoftToSystemTextJsonTransformer();
    const string source = """
      using Newtonsoft.Json;

      public class Order {
        [JsonProperty(Required = Required.Always)]
        public string Id { get; set; } = "";
      }
      """;

    var result = await transformer.TransformAsync(source, "Order.cs");

    await Assert.That(result.TransformedCode).Contains("JsonRequired");
    await Assert.That(result.TransformedCode).DoesNotContain("Required.Always");
  }

  [Test]
  public async Task TransformAsync_JsonConvertSerializeObject_BecomesJsonSerializerSerializeAsync() {
    var transformer = new NewtonsoftToSystemTextJsonTransformer();
    const string source = """
      using Newtonsoft.Json;

      public class OrderService {
        public string Save(object order) {
          return JsonConvert.SerializeObject(order);
        }
      }
      """;

    var result = await transformer.TransformAsync(source, "OrderService.cs");

    await Assert.That(result.TransformedCode).Contains("JsonSerializer.Serialize(order)");
    await Assert.That(result.TransformedCode).DoesNotContain("JsonConvert.SerializeObject");
    await Assert.That(result.Changes.Any(c => c.ChangeType == ChangeType.MethodReplaced)).IsTrue();
  }

  [Test]
  public async Task TransformAsync_GenericDeserializeObject_KeepsTheTypeArgumentAsync() {
    // The type argument is the whole meaning of the call. Dropping it during the rewrite would
    // produce code that compiles differently or not at all.
    var transformer = new NewtonsoftToSystemTextJsonTransformer();
    const string source = """
      using Newtonsoft.Json;

      public class OrderService {
        public Order Load(string json) {
          return JsonConvert.DeserializeObject<Order>(json);
        }
      }

      public class Order { }
      """;

    var result = await transformer.TransformAsync(source, "OrderService.cs");

    await Assert.That(result.TransformedCode).Contains("JsonSerializer.Deserialize<Order>(json)");
    await Assert.That(result.TransformedCode).DoesNotContain("JsonConvert.DeserializeObject");
  }

  [Test]
  public async Task TransformAsync_JsonConverterAttribute_IsPreservedAndWarnedAboutAsync() {
    // Converter types are genuinely incompatible between the two libraries. Rewriting the
    // attribute would produce code that compiles against a converter that does not exist, so the
    // transformer must leave it alone AND say so -- silence here is what would ship broken.
    var transformer = new NewtonsoftToSystemTextJsonTransformer();
    const string source = """
      using Newtonsoft.Json;

      public class Order {
        [JsonConverter(typeof(CustomConverter))]
        public string Id { get; set; } = "";
      }
      """;

    var result = await transformer.TransformAsync(source, "Order.cs");

    await Assert.That(result.TransformedCode).Contains("JsonConverter(typeof(CustomConverter))")
      .Because("an incompatible converter must survive the rewrite untouched");
    await Assert.That(result.Warnings.Any(w => w.Contains("JsonConverter", StringComparison.Ordinal))).IsTrue()
      .Because("a construct the tool cannot migrate has to be reported, not silently left behind");
  }

  [Test]
  public async Task TransformAsync_JObjectUsage_WarnsForManualMigrationAsync() {
    // JObject has no mechanical equivalent -- JsonDocument/JsonElement have different ownership
    // and mutability semantics, so this is a human decision the tool must surface.
    var transformer = new NewtonsoftToSystemTextJsonTransformer();
    const string source = """
      using Newtonsoft.Json;
      using Newtonsoft.Json.Linq;

      public class OrderService {
        public void Read(string json) {
          var parsed = JObject.Parse(json);
        }
      }
      """;

    var result = await transformer.TransformAsync(source, "OrderService.cs");

    await Assert.That(result.Warnings.Any(w => w.Contains("JObject", StringComparison.Ordinal))).IsTrue();
  }

  [Test]
  public async Task TransformAsync_DeadImportOnly_IsRemovedAsync() {
    // An import with no corresponding usage is the one case that can be resolved with certainty,
    // so it is removed rather than warned about.
    var transformer = new NewtonsoftToSystemTextJsonTransformer();
    const string source = """
      using System;
      using Newtonsoft.Json;

      public class Order {
        public string Id { get; set; } = "";
      }
      """;

    var result = await transformer.TransformAsync(source, "Order.cs");

    await Assert.That(result.TransformedCode).DoesNotContain("Newtonsoft.Json");
    await Assert.That(result.TransformedCode).Contains("using System;")
      .Because("only the dead Newtonsoft import is removed, not unrelated usings");
  }

  [Test]
  public async Task TransformAsync_DeadImportRemovalDisabled_LeavesTheFileAloneAsync() {
    var transformer = new NewtonsoftToSystemTextJsonTransformer(removeDeadImports: false);
    const string source = """
      using Newtonsoft.Json;

      public class Order {
        public string Id { get; set; } = "";
      }
      """;

    var result = await transformer.TransformAsync(source, "Order.cs");

    await Assert.That(result.TransformedCode).IsEqualTo(source);
    await Assert.That(result.Changes).IsEmpty();
  }

  [Test]
  public async Task TransformAsync_WhenDisabled_ReportsWithoutRewritingAsync() {
    // Disabled is a reporting mode, not a no-op: the operator still needs to know which files
    // would change before opting in.
    var transformer = new NewtonsoftToSystemTextJsonTransformer(enabled: false);
    const string source = """
      using Newtonsoft.Json;

      public class OrderService {
        public string Save(object o) => JsonConvert.SerializeObject(o);
      }
      """;

    var result = await transformer.TransformAsync(source, "OrderService.cs");

    await Assert.That(result.TransformedCode).IsEqualTo(source)
      .Because("a disabled transformer must not edit the file");
    await Assert.That(result.Warnings.Count).IsGreaterThanOrEqualTo(1)
      .Because("it still reports that the file would be transformed");
  }

  [Test]
  public async Task TransformAsync_JsonIgnore_SurvivesTheRewriteAsync() {
    // [JsonIgnore] is spelled the same in both libraries, so the correct action is to keep it.
    // A transformer that "helpfully" rewrote it would churn the diff for no semantic gain.
    var transformer = new NewtonsoftToSystemTextJsonTransformer();
    const string source = """
      using Newtonsoft.Json;

      public class Order {
        [JsonIgnore]
        public string Secret { get; set; } = "";

        [JsonProperty("id")]
        public string Id { get; set; } = "";
      }
      """;

    var result = await transformer.TransformAsync(source, "Order.cs");

    await Assert.That(result.TransformedCode).Contains("[JsonIgnore]");
    await Assert.That(result.TransformedCode).Contains("JsonPropertyName(\"id\")");
  }


  [Test]
  public async Task TransformAsync_AttributesWithoutTheNewtonsoftJsonImport_StillEmitAValidUsingAsync() {
    // A file can carry Newtonsoft attributes while importing only a sub-namespace -- here
    // Newtonsoft.Json.Linq. There is then no `using Newtonsoft.Json;` to rewrite in place, so
    // the System.Text.Json.Serialization import is built from scratch. That path rendered as
    // `usingSystem.Text.Json.Serialization;` with no space, and the migrated file did not
    // compile: a migration that reports success and leaves a project unbuildable.
    var transformer = new NewtonsoftToSystemTextJsonTransformer();
    const string source = """
      using Newtonsoft.Json.Linq;

      public class Order {
        [JsonProperty("order_id")]
        public string Id { get; set; } = "";
      }
      """;

    var result = await transformer.TransformAsync(source, "Order.cs");

    await Assert.That(result.TransformedCode).Contains("using System.Text.Json.Serialization;")
      .Because("the keyword and the namespace need a space between them or the file will not parse");
    await Assert.That(result.TransformedCode).DoesNotContain("usingSystem");
  }

  [Test]
  public async Task TransformAsync_AddsTheSerializationImportOnlyOnceAsync() {
    // Two rewritten attributes must not each contribute an import. A duplicate using is a
    // compile error, so "needed" has to be answered once for the file.
    var transformer = new NewtonsoftToSystemTextJsonTransformer();
    const string source = """
      using Newtonsoft.Json.Linq;

      public class Order {
        [JsonProperty("order_id")]
        public string Id { get; set; } = "";

        [JsonProperty("customer_id")]
        public string CustomerId { get; set; } = "";
      }
      """;

    var result = await transformer.TransformAsync(source, "Order.cs");

    var count = result.TransformedCode.Split("using System.Text.Json.Serialization;").Length - 1;
    await Assert.That(count).IsEqualTo(1);
  }

  [Test]
  public async Task TransformAsync_WhenTheImportAlreadyExists_DoesNotAddASecondAsync() {
    // A partially migrated file is the normal case on a re-run, and re-adding the import would
    // break a file that was already correct.
    var transformer = new NewtonsoftToSystemTextJsonTransformer();
    const string source = """
      using Newtonsoft.Json;
      using System.Text.Json.Serialization;

      public class Order {
        [JsonProperty("order_id")]
        public string Id { get; set; } = "";
      }
      """;

    var result = await transformer.TransformAsync(source, "Order.cs");

    var count = result.TransformedCode.Split("using System.Text.Json.Serialization;").Length - 1;
    await Assert.That(count).IsEqualTo(1);
  }


  // ── Per-namespace handling: what survives the migration and what does not ──

  [Test]
  public async Task TransformAsync_SchemaNamespace_IsKeptWithAWarningAsync() {
    // Newtonsoft.Json.Schema has no System.Text.Json counterpart at all. Removing the import
    // would break every JSchema reference in the file, so it is deliberately left in place and
    // the operator is told to reach for another package. Keeping it is the correct outcome
    // here, which is the opposite of what this transformer does with every other Newtonsoft
    // namespace.
    var transformer = new NewtonsoftToSystemTextJsonTransformer();
    const string source = """
      using Newtonsoft.Json;
      using Newtonsoft.Json.Schema;

      public class Order {
        [JsonProperty("order_id")]
        public string Id { get; set; } = "";
      }
      """;

    var result = await transformer.TransformAsync(source, "Order.cs");

    await Assert.That(result.TransformedCode).Contains("using Newtonsoft.Json.Schema;")
      .Because("there is no equivalent to migrate to, so removing it would only break the file");
    await Assert.That(result.Warnings.Any(w => w.Contains("Schema", StringComparison.Ordinal))).IsTrue()
      .Because("a namespace the tool cannot migrate has to be reported, not silently left behind");
  }

  [Test]
  public async Task TransformAsync_ConvertersNamespace_IsRemovedWithAWarningAsync() {
    // Converters are the opposite call: System.Text.Json has converters, but they are a
    // different type entirely, so the import must go and the author must rewrite them. Leaving
    // it would reference a package the migration is removing.
    var transformer = new NewtonsoftToSystemTextJsonTransformer();
    const string source = """
      using Newtonsoft.Json;
      using Newtonsoft.Json.Converters;

      public class Order {
        [JsonProperty("order_id")]
        public string Id { get; set; } = "";
      }
      """;

    var result = await transformer.TransformAsync(source, "Order.cs");

    await Assert.That(result.TransformedCode).DoesNotContain("Newtonsoft.Json.Converters");
    await Assert.That(result.Warnings.Any(w => w.Contains("Converters", StringComparison.Ordinal))).IsTrue()
      .Because("converter types differ between the libraries, so this is manual work the operator must be told about");
  }

  [Test]
  public async Task TransformAsync_AnUnrecognisedNewtonsoftNamespace_IsRemovedAndNamedAsync() {
    // The catch-all. A namespace this tool has never heard of still cannot stay once the package
    // is gone, but removing it blindly could break code the tool does not understand -- so the
    // warning names the namespace rather than reporting a generic count.
    var transformer = new NewtonsoftToSystemTextJsonTransformer();
    const string source = """
      using Newtonsoft.Json;
      using Newtonsoft.Json.Bson;

      public class Order {
        [JsonProperty("order_id")]
        public string Id { get; set; } = "";
      }
      """;

    var result = await transformer.TransformAsync(source, "Order.cs");

    await Assert.That(result.TransformedCode).DoesNotContain("Newtonsoft.Json.Bson");
    await Assert.That(result.Warnings.Any(w => w.Contains("Newtonsoft.Json.Bson", StringComparison.Ordinal))).IsTrue()
      .Because("the operator needs to know which namespace went, not merely that one did");
  }

  [Test]
  public async Task TransformAsync_ComplexJsonProperty_IsLeftAloneAndFlaggedAsync() {
    // A [JsonProperty] carrying several settings has no single-attribute equivalent. Rewriting
    // it partially would silently drop the settings that did not survive, so it is left intact
    // and reported.
    var transformer = new NewtonsoftToSystemTextJsonTransformer();
    const string source = """
      using Newtonsoft.Json;

      public class Order {
        [JsonProperty("order_id", NullValueHandling = NullValueHandling.Ignore, Order = 2)]
        public string Id { get; set; } = "";
      }
      """;

    var result = await transformer.TransformAsync(source, "Order.cs");

    await Assert.That(result.Warnings.Any(w => w.Contains("Complex", StringComparison.Ordinal))).IsTrue()
      .Because("a partially converted attribute would drop settings without saying so");
    await Assert.That(result.TransformedCode).Contains("NullValueHandling")
      .Because("the original has to survive intact for the operator to convert by hand");
  }

  [Test]
  public async Task TransformAsync_ComplexJsonPropertyWithTodosDisabled_StaysSilentAsync() {
    // The reporting is opt-out. With it disabled the attribute is still left alone -- the
    // setting controls whether the tool comments, never whether it rewrites.
    var transformer = new NewtonsoftToSystemTextJsonTransformer(addTodoForUnsupported: false);
    const string source = """
      using Newtonsoft.Json;

      public class Order {
        [JsonProperty("order_id", NullValueHandling = NullValueHandling.Ignore, Order = 2)]
        public string Id { get; set; } = "";
      }
      """;

    var result = await transformer.TransformAsync(source, "Order.cs");

    await Assert.That(result.Warnings.Any(w => w.Contains("Complex", StringComparison.Ordinal))).IsFalse();
    await Assert.That(result.TransformedCode).Contains("NullValueHandling")
      .Because("silencing the warning must not change what the transformer does to the code");
  }

  [Test]
  public async Task TransformAsync_JsonConstructorAndExtensionData_SurviveUnchangedAsync() {
    // Both attributes exist under the same names in System.Text.Json, so the correct action is
    // to keep them and add the import. Rewriting them would churn a file for no gain.
    var transformer = new NewtonsoftToSystemTextJsonTransformer();
    const string source = """
      using Newtonsoft.Json;

      public class Order {
        [JsonExtensionData]
        public Dictionary<string, object> Extra { get; set; } = new();

        [JsonConstructor]
        public Order() { }
      }
      """;

    var result = await transformer.TransformAsync(source, "Order.cs");

    await Assert.That(result.TransformedCode).Contains("[JsonExtensionData]");
    await Assert.That(result.TransformedCode).Contains("[JsonConstructor]");
    await Assert.That(result.TransformedCode).Contains("using System.Text.Json.Serialization;")
      .Because("the attributes keep their names but move namespace, so the import has to arrive");
  }

}
