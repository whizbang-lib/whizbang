using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.RunControl;

namespace Whizbang.Core.Tests.RunControl;

/// <summary>
/// Coverage for the <see cref="IWhizbangKillswitch"/> registration
/// <see cref="WhizbangRunControlDiTests"/> never resolves. <c>AddWhizbangRunControl</c> wires the
/// killswitch's factory lambda, but nothing in the DI suite ever calls
/// <c>GetRequiredService&lt;IWhizbangKillswitch&gt;()</c>, so the lambda itself has never run. The
/// killswitch is the one operator-facing "stop everything now" lever — a DI wiring break here would
/// only surface the first time someone actually needed to pull it in production.
/// </summary>
public class WhizbangRunControlServiceCollectionExtensionsCoverageTests {

  [Test]
  public async Task AddWhizbangRunControl_ResolvesKillswitchOverTheSameLifecycleStateAsync() {
    var services = new ServiceCollection();
    services.AddWhizbangRunControl();
    var provider = services.BuildServiceProvider();

    var killswitch = provider.GetRequiredService<IWhizbangKillswitch>();

    await Assert.That(killswitch).IsNotNull()
      .Because("the killswitch factory must actually build — a DI wiring break here would only surface the first time an operator needs the emergency stop");
    await Assert.That(provider.GetRequiredService<IWhizbangKillswitch>()).IsSameReferenceAs(killswitch)
      .Because("TryAddSingleton must produce exactly one killswitch instance shared across every resolution, not a fresh one each time");
  }
}
