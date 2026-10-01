using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Minting;
using Whizbang.Core.SystemEvents;
using Whizbang.Core.Tags;

#pragma warning disable CA1707 // Test method names use underscores by convention

namespace Whizbang.Core.Tests.Tags;

/// <summary>
/// <c>Whizbang:Tags:Coalesce:&lt;tag&gt;</c> is the bindable map beside
/// <see cref="TagOptions.CoalesceBindings"/>: each tag's keys override that tag's code policy,
/// a configured tag with no code policy gets one, and every other tag keeps its code values.
/// </summary>
[Category("Core")]
[Category("Tags")]
public class TagCoalesceConfigurationBinderTests {
  private static IConfiguration _config(params (string Key, string Value)[] values) =>
    new ConfigurationBuilder()
      .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
      .Build();

  [Test]
  public async Task ConfiguredTag_OverridesItsCodePolicyPerKey_KeepingCodeOnlyMembersAsync() {
    static int priority(CoalesceFoldBatch _) => 3;
    var tags = new TagOptions()
      .Coalesce("orders", p => {
        p.SlideSeconds = 30;
        p.MaxBatchCount = 10;
        p.PriorityFor = priority;
      })
      .Coalesce("billing", p => p.SlideSeconds = 45);

    TagCoalesceConfigurationBinder.Apply(tags, _config(
      ("Whizbang:Tags:Coalesce:orders:SlideSeconds", "5"),
      ("Whizbang:Tags:Coalesce:orders:MaxDelaySeconds", "60"),
      ("Whizbang:Tags:Coalesce:orders:Atomicity", "Atomic"),
      ("Whizbang:Tags:Coalesce:orders:PriorityFold", "LeastUrgent")));

    var orders = tags.CoalesceBindings["orders"];
    await Assert.That(orders.SlideSeconds).IsEqualTo(5);
    await Assert.That(orders.MaxDelaySeconds).IsEqualTo(60);
    await Assert.That(orders.MaxBatchCount).IsEqualTo(10).Because("an unconfigured key keeps the code value");
    await Assert.That(orders.Atomicity).IsEqualTo(FanoutAtomicity.Atomic);
    await Assert.That(orders.PriorityFold).IsEqualTo(CompositePriorityFold.LeastUrgent);
    await Assert.That(orders.PriorityFor).IsNotNull().Because("delegates are code-only and survive binding");
    await Assert.That(tags.CoalesceBindings["billing"].SlideSeconds).IsEqualTo(45)
      .Because("a tag with no configured keys keeps its code policy");
  }

  [Test]
  public async Task ConfiguredTag_WithNoCodePolicy_GetsOneFromDefaultsAsync() {
    var tags = new TagOptions();

    TagCoalesceConfigurationBinder.Apply(tags, _config(("Whizbang:Tags:Coalesce:shipments:MaxBatchCount", "25")));

    var shipments = tags.CoalesceBindings["shipments"];
    await Assert.That(shipments.MaxBatchCount).IsEqualTo(25);
    await Assert.That(shipments.SlideSeconds).IsEqualTo(15);
  }

  [Test]
  public async Task NoConfigurationOrSection_LeavesBindingsAloneAsync() {
    var tags = new TagOptions().Coalesce("orders", p => p.SlideSeconds = 30);

    TagCoalesceConfigurationBinder.Apply(tags, null);
    TagCoalesceConfigurationBinder.Apply(tags, _config(("Whizbang:Tags:RouteNamespace:orders", "bulk")));

    await Assert.That(tags.CoalesceBindings.Count).IsEqualTo(1);
    await Assert.That(tags.CoalesceBindings["orders"].SlideSeconds).IsEqualTo(30);
  }

  [Test]
  public async Task Apply_RejectsNullTagOptionsAsync() {
    await Assert.That(() => TagCoalesceConfigurationBinder.Apply(null!, null)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Resolver_SeesConfigurationBoundCoalesceBindingsAsync() {
    var services = new ServiceCollection();
    services.AddSingleton(_config(("Whizbang:Tags:Coalesce:orders:SlideSeconds", "0")));
    services.AddWhizbang(o => o.Tags.Coalesce("orders", p => p.SlideSeconds = 30));

    await using var provider = services.BuildServiceProvider();
    var resolver = provider.GetRequiredService<CoalesceGroupResolver>();

    await Assert.That(resolver.GetBinding("orders")).IsNull()
      .Because("configuration switched the tag's coalescing off (SlideSeconds 0) without a redeploy");
  }

  [Test]
  public async Task StartupValidator_SeesConfigurationBoundCoalesceBindingsAsync() {
    var tags = new TagOptions();
    var validator = new TagPolicyStartupValidator(
      tags, () => [], Options.Create(new SystemEventOptions()),
      _config(("Whizbang:Tags:Coalesce:orders:SlideSeconds", "20")));

    await validator.StartAsync(CancellationToken.None);

    await Assert.That(tags.CoalesceBindings["orders"].SlideSeconds).IsEqualTo(20);
  }
}
