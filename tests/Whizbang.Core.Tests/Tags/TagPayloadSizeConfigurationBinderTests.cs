using Microsoft.Extensions.Configuration;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Tags;

namespace Whizbang.Core.Tests.Tags;

/// <summary>
/// That the tag payload-size thresholds bind from configuration, globally and per tag.
/// </summary>
/// <remarks>
/// The warning threshold defaults to 8 KiB and nothing bound either threshold from configuration,
/// so a consumer whose messages on one tag legitimately carry 40 KB received a warning per hook per
/// message and had no way to raise the line without a code change. The binder reads the same
/// section the route-namespace binder does, and a per-tag value wins over the global one.
/// </remarks>
/// <docs>fundamentals/tags/tags</docs>
[Category("Tags")]
public class TagPayloadSizeConfigurationBinderTests {
  private static IConfiguration _config(params (string Key, string? Value)[] pairs) =>
    new ConfigurationBuilder()
      .AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value)))
      .Build();

  [Test]
  public async Task Apply_ReadsTheGlobalThresholdsAsync() {
    var options = new TagOptions();

    TagPayloadSizeConfigurationBinder.Apply(options, _config(
      ("Whizbang:Tags:PayloadSizeWarningThresholdBytes", "65536"),
      ("Whizbang:Tags:PayloadSizeErrorThresholdBytes", "262144")));

    await Assert.That(options.PayloadSizeWarningThresholdBytes).IsEqualTo(65536);
    await Assert.That(options.PayloadSizeErrorThresholdBytes).IsEqualTo(262144);
  }

  [Test]
  public async Task Apply_ReadsPerTagOverridesAsync() {
    var options = new TagOptions();

    TagPayloadSizeConfigurationBinder.Apply(options, _config(
      ("Whizbang:Tags:PayloadSizeWarningThresholdBytesByTag:embeddings", "65536"),
      ("Whizbang:Tags:PayloadSizeErrorThresholdBytesByTag:embeddings", "131072")));

    await Assert.That(options.ResolvePayloadSizeWarningThreshold("embeddings")).IsEqualTo(65536);
    await Assert.That(options.ResolvePayloadSizeErrorThreshold("embeddings")).IsEqualTo(131072);
    await Assert.That(options.ResolvePayloadSizeWarningThreshold("other")).IsEqualTo(8192)
      .Because("a tag without an override keeps the global value");
    await Assert.That(options.ResolvePayloadSizeErrorThreshold("other")).IsNull();
  }

  [Test]
  public async Task Apply_AnEmptyValueDisablesTheThresholdAsync() {
    var options = new TagOptions();

    TagPayloadSizeConfigurationBinder.Apply(options, _config(
      ("Whizbang:Tags:PayloadSizeWarningThresholdBytes", ""),
      ("Whizbang:Tags:PayloadSizeWarningThresholdBytesByTag:audit", "")));

    await Assert.That(options.PayloadSizeWarningThresholdBytes).IsNull()
      .Because("an operator disables a threshold by clearing it, the same way the property is disabled in code");
    await Assert.That(options.ResolvePayloadSizeWarningThreshold("audit")).IsNull();
  }

  [Test]
  public async Task Apply_AValueThatIsNotANumberIsRefusedByNameAsync() {
    var options = new TagOptions();

    await Assert.That(() => TagPayloadSizeConfigurationBinder.Apply(options, _config(
        ("Whizbang:Tags:PayloadSizeErrorThresholdBytesByTag:embeddings", "lots"))))
      .Throws<InvalidOperationException>()
      .WithMessageContaining("Whizbang:Tags:PayloadSizeErrorThresholdBytesByTag:embeddings");
  }

  [Test]
  public async Task Apply_WithoutTheSection_LeavesTheDefaultsAsync() {
    var options = new TagOptions();

    TagPayloadSizeConfigurationBinder.Apply(options, _config(("Whizbang:Tags:RouteNamespace:x", "y")));
    TagPayloadSizeConfigurationBinder.Apply(options, null);

    await Assert.That(options.PayloadSizeWarningThresholdBytes).IsEqualTo(8192);
    await Assert.That(options.PayloadSizeErrorThresholdBytes).IsNull();
    await Assert.That(options.PayloadSizeWarningThresholdBytesByTag).IsEmpty();
  }

  [Test]
  public async Task UsePayloadSizeThresholds_RecordsAPerTagDecisionInCodeAsync() {
    var options = new TagOptions();

    options.UsePayloadSizeThresholds("embeddings", warningBytes: null, errorBytes: 65536);

    await Assert.That(options.ResolvePayloadSizeWarningThreshold("embeddings")).IsNull()
      .Because("an explicit null per tag disables that threshold for the tag even though the global one is set");
    await Assert.That(options.ResolvePayloadSizeErrorThreshold("embeddings")).IsEqualTo(65536);
    await Assert.That(() => options.UsePayloadSizeThresholds(" ", 1, 1)).Throws<ArgumentException>();
  }
}
