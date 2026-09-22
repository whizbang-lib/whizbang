using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Tracing;

namespace Whizbang.Core.Tests.Tracing;

[Category("Core")]
[Category("Attributes")]
[Category("Tracing")]
public class WhizbangTraceAttributeTests {
  [Test]
  public async Task Constructor_Default_UsesNormalVerbosityAsync() {
    var attribute = new WhizbangTraceAttribute();

    await Assert.That(attribute.Verbosity).IsEqualTo(TraceVerbosity.Normal);
  }

  [Test]
  [Arguments(TraceVerbosity.Off)]
  [Arguments(TraceVerbosity.Minimal)]
  [Arguments(TraceVerbosity.Normal)]
  public async Task Verbosity_WhenInitialized_OverridesDefaultAsync(TraceVerbosity verbosity) {
    var attribute = new WhizbangTraceAttribute { Verbosity = verbosity };

    await Assert.That(attribute.Verbosity).IsEqualTo(verbosity);
  }

  [Test]
  public async Task AttributeUsage_TargetsClassesOnlyAsync() {
    var usage = typeof(WhizbangTraceAttribute)
        .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
        .Cast<AttributeUsageAttribute>()
        .Single();

    await Assert.That(usage.ValidOn).IsEqualTo(AttributeTargets.Class);
    await Assert.That(usage.AllowMultiple).IsFalse();
    await Assert.That(usage.Inherited).IsFalse();
  }
}
