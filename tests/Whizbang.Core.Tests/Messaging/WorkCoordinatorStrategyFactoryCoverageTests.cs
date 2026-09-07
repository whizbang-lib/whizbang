using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Coverage round 23 tail: <see cref="WorkCoordinatorStrategyFactory.Create"/>'s default switch
/// arm. <c>WorkCoordinatorStrategyRegistrationTests.GeneratorPattern_AllEnumValuesHandled_DoesNotThrowAsync</c>
/// deliberately iterates every DEFINED enum value to prove none of them throw — which means the
/// undefined-value throw arm has never actually run.
/// </summary>
[Category("Messaging")]
public class WorkCoordinatorStrategyFactoryCoverageTests {

  /// <summary>
  /// Operator impact: <c>WorkCoordinatorStrategy</c> can arrive from bound configuration as a raw
  /// int, so an out-of-range value is reachable in production (a typo'd or stale config value),
  /// not just a theoretical enum gap. Silently falling through to one of the known strategies
  /// instead of throwing would run the wrong coordination pattern with no diagnostic at all.
  /// </summary>
  [Test]
  public async Task Create_UndefinedStrategyValue_ThrowsArgumentOutOfRangeExceptionAsync() {
    var sp = new ServiceCollection().BuildServiceProvider();
    const WorkCoordinatorStrategy undefined = (WorkCoordinatorStrategy)999;

    var thrown = await Assert.That(() => WorkCoordinatorStrategyFactory.Create(undefined, sp))
      .Throws<ArgumentOutOfRangeException>();

    await Assert.That(thrown!.ParamName).IsEqualTo("strategy");
    await Assert.That(thrown.Message).Contains("Unknown work coordinator strategy")
      .Because("an operator diagnosing a misconfigured deployment needs the message to name the "
             + "bad value, not just report 'argument out of range'");
  }
}
