using Microsoft.Extensions.Configuration;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Configuration;
using Whizbang.Core.Tags;

#pragma warning disable CA1707 // Test method names use underscores by convention

namespace Whizbang.Core.Tests.Configuration;

/// <summary>
/// The hand-rolled, reflection-free value readers the per-instance binders share: each applies
/// a value only when the key is present AND parses, so a typo leaves the code value in place.
/// </summary>
public class ConfigurationValueBinderTests {
  private static IConfiguration _config(params (string Key, string? Value)[] values) =>
    new ConfigurationBuilder()
      .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
      .Build();

  [Test]
  public async Task BindInt_ParsesInvariantInteger_AndSkipsMissingOrInvalidAsync() {
    var config = _config(("Good", "42"), ("Bad", "forty"));
    var applied = new List<int>();

    ConfigurationValueBinder.BindInt(config, "Good", applied.Add);
    ConfigurationValueBinder.BindInt(config, "Bad", applied.Add);
    ConfigurationValueBinder.BindInt(config, "Missing", applied.Add);

    await Assert.That(applied).IsEquivalentTo([42]);
  }

  [Test]
  public async Task BindUShort_ParsesInRangeValue_AndSkipsOutOfRangeAsync() {
    var config = _config(("Good", "300"), ("TooBig", "70000"));
    var applied = new List<ushort>();

    ConfigurationValueBinder.BindUShort(config, "Good", applied.Add);
    ConfigurationValueBinder.BindUShort(config, "TooBig", applied.Add);

    await Assert.That(applied).IsEquivalentTo([(ushort)300]);
  }

  [Test]
  public async Task BindDouble_ParsesInvariantDecimal_AndSkipsInvalidAsync() {
    var config = _config(("Good", "2.5"), ("Bad", "two"));
    var applied = new List<double>();

    ConfigurationValueBinder.BindDouble(config, "Good", applied.Add);
    ConfigurationValueBinder.BindDouble(config, "Bad", applied.Add);

    await Assert.That(applied).IsEquivalentTo([2.5]);
  }

  [Test]
  public async Task BindBool_ParsesTrueFalse_AndSkipsInvalidAsync() {
    var config = _config(("Good", "false"), ("Bad", "nope"));
    var applied = new List<bool>();

    ConfigurationValueBinder.BindBool(config, "Good", applied.Add);
    ConfigurationValueBinder.BindBool(config, "Bad", applied.Add);

    await Assert.That(applied).IsEquivalentTo([false]);
  }

  [Test]
  public async Task BindTimeSpan_ParsesInvariantTimeSpan_AndSkipsInvalidAsync() {
    var config = _config(("Good", "00:00:45"), ("Bad", "soon"));
    var applied = new List<TimeSpan>();

    ConfigurationValueBinder.BindTimeSpan(config, "Good", applied.Add);
    ConfigurationValueBinder.BindTimeSpan(config, "Bad", applied.Add);

    await Assert.That(applied).IsEquivalentTo([TimeSpan.FromSeconds(45)]);
  }

  [Test]
  public async Task BindString_AppliesNonBlank_AndSkipsBlankAsync() {
    var config = _config(("Good", "orders"), ("Blank", "  "));
    var applied = new List<string>();

    ConfigurationValueBinder.BindString(config, "Good", applied.Add);
    ConfigurationValueBinder.BindString(config, "Blank", applied.Add);
    ConfigurationValueBinder.BindString(config, "Missing", applied.Add);

    await Assert.That(applied).IsEquivalentTo(["orders"]);
  }

  [Test]
  public async Task BindEnum_ParsesNameCaseInsensitively_AndSkipsUnknownAsync() {
    var config = _config(("Good", "leasturgent"), ("Bad", "Sideways"), ("Undefined", "7"));
    var applied = new List<CompositePriorityFold>();

    ConfigurationValueBinder.BindEnum<CompositePriorityFold>(config, "Good", applied.Add);
    ConfigurationValueBinder.BindEnum<CompositePriorityFold>(config, "Bad", applied.Add);
    ConfigurationValueBinder.BindEnum<CompositePriorityFold>(config, "Undefined", applied.Add);

    await Assert.That(applied).IsEquivalentTo([CompositePriorityFold.LeastUrgent]);
  }

  [Test]
  public async Task Binders_RejectNullArgumentsAsync() {
    var config = _config();

    await Assert.That(() => ConfigurationValueBinder.BindInt(null!, "k", _ => { })).Throws<ArgumentNullException>();
    await Assert.That(() => ConfigurationValueBinder.BindInt(config, "k", null!)).Throws<ArgumentNullException>();
  }
}
