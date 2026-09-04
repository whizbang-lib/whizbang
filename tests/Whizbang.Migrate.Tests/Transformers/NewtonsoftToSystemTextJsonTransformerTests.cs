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
}
