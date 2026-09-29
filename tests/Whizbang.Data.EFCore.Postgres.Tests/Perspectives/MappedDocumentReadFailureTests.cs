using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;
using Whizbang.Data.EFCore.Postgres.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// How a failed read of a JSON-mapped document is explained: the value, at its path, that the property
/// mapped there cannot take (issue #985).
/// </summary>
/// <remarks>
/// Entity Framework's materializer reports a stored value of the wrong JSON type as a bare reader error
/// with no path, which the perspective worker does not classify as a stored document no reader takes.
/// The explanation walks the stored document against the model Entity Framework read it with. No
/// database: building a model needs a provider, not a connection, and the documents are text.
/// </remarks>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/Perspectives/MappedDocumentReadFailure.cs</code-under-test>
[Category("Shard4")]
public class MappedDocumentReadFailureTests {
  public enum Shade { Light, Dark }

  public sealed class Inner {
    public string Label { get; set; } = string.Empty;
  }

  public sealed class Split {
    public string Note { get; set; } = string.Empty;
  }

  public sealed class Doc {
    public string Name { get; set; } = string.Empty;
    public string? Optional { get; set; }
    public int Count { get; set; }
    public int? Maybe { get; set; }
    public decimal Price { get; set; }
    public bool Flag { get; set; }
    public Guid Ref { get; set; }
    public char Letter { get; set; }
    public Shade Plain { get; set; }
    public Shade Named { get; set; }
    public DateTimeOffset At { get; set; }
    public byte[] Blob { get; set; } = [];
    public List<string> Tags { get; set; } = [];
    public Inner Child { get; set; } = new();
    public List<Inner> Items { get; set; } = [];
  }

  public sealed class Row {
    public Guid Id { get; set; }
    public Doc Data { get; set; } = new();
    public Split Extra { get; set; } = new();
  }

  private sealed class ProbeContext(DbContextOptions<ProbeContext> options) : DbContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) {
      modelBuilder.Entity<Row>(entity => {
        entity.ToTable("wh_per_probe");
        entity.HasKey(r => r.Id);
        entity.ComplexProperty(r => r.Data, d => {
          d.ToJson("data");
          d.Property(x => x.Named).HasConversion<string>();
          d.ComplexProperty(x => x.Child);
          d.ComplexCollection(x => x.Items);
        });
        entity.ComplexProperty(r => r.Extra);
      });
    }
  }

  private static IEntityType _rowType() {
    using var context = new ProbeContext(new DbContextOptionsBuilder<ProbeContext>()
      .UseNpgsql("Host=localhost;Database=probe;Username=u;Password=p")
      .Options);
    return context.Model.FindEntityType(typeof(Row))!;
  }

  private static readonly IEntityType _row = _rowType();

  private const string VALID = """
    {"Name":"n","Optional":null,"Count":1,"Maybe":2,"Price":1.5,"Flag":true,"Ref":"01a0edca-3f41-75c8-846b-f121c23b7528",
     "Letter":"x","Plain":0,"Named":"Dark","At":123,"Blob":"AAE=","Tags":["a"],"Child":{"Label":"c"},
     "Items":[{"Label":"i0"},{"Label":"i1"}]}
    """;

  private static readonly InvalidOperationException _raised = new("Cannot get the value of a token type 'Number' as a string.");

  private static JsonException? _explain(string? data) =>
    MappedDocumentReadFailure.Explain(_row, "Probe", column => column == "data" ? data : null, _raised);

  /// <summary>The valid document with the value at a dotted path (an element as <c>Name[i]</c>) replaced.</summary>
  private static string _with(string path, string json) {
    var root = JsonNode.Parse(VALID)!;
    var segments = path.Split('.');
    var parent = root;
    foreach (var segment in segments[..^1]) {
      parent = _step(parent, segment);
    }
    var last = segments[^1];
    if (last.EndsWith(']')) {
      var (key, index) = _element(last);
      parent[key]![index] = JsonNode.Parse(json);
    } else {
      parent[last] = JsonNode.Parse(json);
    }
    return root.ToJsonString();
  }

  private static JsonNode _step(JsonNode node, string segment) {
    if (!segment.EndsWith(']')) {
      return node[segment]!;
    }
    var (key, index) = _element(segment);
    return node[key]![index]!;
  }

  private static (string Key, int Index) _element(string segment) {
    var open = segment.IndexOf('[');
    return (segment[..open], int.Parse(segment[(open + 1)..^1], System.Globalization.CultureInfo.InvariantCulture));
  }

  [Test]
  public async Task AValidDocument_IsNotExplained_SoTheOriginalFailureStandsAsync() {
    await Assert.That(_explain(VALID)).IsNull();
  }

  [Test]
  public async Task NoStoredDocument_IsNotExplainedAsync() {
    await Assert.That(_explain(null)).IsNull();
  }

  [Test]
  [Arguments("Name", "123", "$.Name", "a number", "Doc.Name (String) reads a string")]
  [Arguments("Count", "\"many\"", "$.Count", "a string", "Doc.Count (Int32) reads a number")]
  [Arguments("Maybe", "\"two\"", "$.Maybe", "a string", "Doc.Maybe (Int32) reads a number")]
  [Arguments("Price", "true", "$.Price", "a boolean", "Doc.Price (Decimal) reads a number")]
  [Arguments("Flag", "\"yes\"", "$.Flag", "a string", "Doc.Flag (Boolean) reads true or false")]
  [Arguments("Ref", "\"not-a-guid\"", "$.Ref", "a string", "Doc.Ref (Guid) reads a string")]
  [Arguments("Ref", "7", "$.Ref", "a number", "Doc.Ref (Guid) reads a string")]
  [Arguments("Letter", "[1]", "$.Letter", "an array", "Doc.Letter (Char) reads a string")]
  [Arguments("Named", "{}", "$.Named", "an object", "Doc.Named (String) reads a string")]
  [Arguments("Plain", "\"Dark\"", "$.Plain", "a string", "Doc.Plain (Int32) reads a number")]
  [Arguments("Tags", "\"a\"", "$.Tags", "a string", "Doc.Tags reads an array")]
  [Arguments("Child.Label", "5", "$.Child.Label", "a number", "Inner.Label (String) reads a string")]
  [Arguments("Child", "5", "$.Child", "a number", "Child reads an object")]
  [Arguments("Items", "{}", "$.Items", "an object", "Items reads an array")]
  [Arguments("Items[1].Label", "false", "$.Items[1].Label", "a boolean", "Inner.Label (String) reads a string")]
  public async Task AValueOfTheWrongType_IsExplainedWithItsPathAsync(
      string at, string json, string path, string found, string expected) {
    var explained = _explain(_with(at, json));

    await Assert.That(explained).IsNotNull();
    await Assert.That(explained!.Path).IsEqualTo(path);
    await Assert.That(explained.Message).Contains($"holds {found} at {path}, where {expected}");
    await Assert.That(explained.Message).Contains("stored data document of a Probe row");
    await Assert.That(explained.InnerException).IsSameReferenceAs(_raised)
      .Because("the materializer's own error stays inside the explanation");
    await Assert.That(StoredFormUnreadable.TryClassify(explained, out var classified)).IsTrue()
      .Because("the explanation is what lets the worker classify the stream as unreadable");
    await Assert.That(classified!.Path).IsEqualTo(path);
  }

  [Test]
  [Arguments("At", "\"2026-01-01T00:00:00Z\"")]
  [Arguments("Blob", "5")]
  [Arguments("Optional", "null")]
  [Arguments("Child", "null")]
  [Arguments("Items", "null")]
  [Arguments("Items[0]", "null")]
  public async Task AValueTheWalkLeavesToItsReader_IsNotExplainedAsync(string at, string json) {
    await Assert.That(_explain(_with(at, json))).IsNull()
      .Because("a null, a temporal and a type the walk does not know are not claimed: the temporal "
        + "readers refuse on their own, and the rest is not a type mismatch");
  }

  [Test]
  public async Task AMissingKey_IsNotExplainedAsync() {
    var root = JsonNode.Parse(VALID)!.AsObject();
    root.Remove("Name");
    root.Remove("Child");

    await Assert.That(_explain(root.ToJsonString())).IsNull();
  }

  [Test]
  public async Task MayBeDocumentRead_IsTheReadersFailuresOnlyAsync() {
    await Assert.That(MappedDocumentReadFailure.MayBeDocumentRead(new InvalidOperationException())).IsTrue();
    await Assert.That(MappedDocumentReadFailure.MayBeDocumentRead(new FormatException())).IsTrue();
    await Assert.That(MappedDocumentReadFailure.MayBeDocumentRead(new ObjectDisposedException("context"))).IsFalse()
      .Because("a disposed context is never the document's");
    await Assert.That(MappedDocumentReadFailure.MayBeDocumentRead(new TimeoutException())).IsFalse();
  }
}
