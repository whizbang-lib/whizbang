// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Core.Component.Tests;

/// <summary>
/// The startup report of items two assemblies both register (#2201): a Warning per collision naming the item and both
/// assemblies, and, under <c>Whizbang:Startup:RegistrationCollisions = Fail</c>, a failed start naming every one.
/// </summary>
/// <code-under-test>src/Whizbang.Core/RegistrationCollisionStartupValidator.cs</code-under-test>
public class RegistrationCollisionStartupValidatorTests {
  private sealed class FixedSource(params RegistrationCollision[] collisions) : IRegistrationCollisionSource {
    public IReadOnlyList<RegistrationCollision> GetCollisions() => collisions;
  }

  private static readonly RegistrationCollision _perspective =
    new("IPerspectiveRunnerRegistry", "Orders.OrderSummaryPerspective", "Billing", "Orders");
  private static readonly RegistrationCollision _model =
    new("Perspective model", "Orders.OrderSummary in OrdersDbContext", "Host", "Orders");

  private static ServiceProvider _provider(string? mode, params IRegistrationCollisionSource[] sources) {
    var services = new ServiceCollection();
    services.AddFakeLogging();
    if (mode is not null) {
      services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["Whizbang:Startup:RegistrationCollisions"] = mode })
        .Build());
    }
    foreach (var source in sources) {
      services.AddRegistrationCollisionSource(source);
    }
    return services.BuildServiceProvider();
  }

  private static IHostedService[] _validators(IServiceProvider provider) =>
    [.. provider.GetServices<IHostedService>().Where(s => s is RegistrationCollisionStartupValidator)];

  private static FakeLogRecord[] _warnings(IServiceProvider provider) =>
    [.. provider.GetFakeLogCollector().GetSnapshot().Where(r => r.Level == LogLevel.Warning)];

  [Test]
  public async Task ACollision_UnderWarn_IsLoggedNamingBothAssemblies_AndStartsAsync() {
    await using var provider = _provider(mode: null, new FixedSource(_perspective));

    await _validators(provider).Single().StartAsync(CancellationToken.None);

    var warnings = _warnings(provider);
    await Assert.That(warnings.Length).IsEqualTo(1);
    await Assert.That(warnings[0].Message).IsEqualTo(
      "IPerspectiveRunnerRegistry 'Orders.OrderSummaryPerspective' is registered by both Orders and Billing. "
      + "Billing was registered latest and is used; Orders's registration is hidden");
  }

  [Test]
  public async Task CollisionsUnderFail_StopStartup_NamingEveryCollision_AfterWarningForEachAsync() {
    await using var provider = _provider("Fail", new FixedSource(_perspective), new FixedSource(_model));

    var failure = await Assert.That(() => _validators(provider).Single().StartAsync(CancellationToken.None))
      .Throws<InvalidOperationException>();

    await Assert.That(failure!.Message).Contains(_perspective.ToString());
    await Assert.That(failure.Message).Contains(_model.ToString());
    await Assert.That(failure.Message).Contains("Whizbang:Startup:RegistrationCollisions");
    await Assert.That(_warnings(provider).Length).IsEqualTo(2)
      .Because("each collision is still named in the log, before the failure");
  }

  [Test]
  public async Task NoCollisions_UnderFail_StartsQuietlyAsync() {
    await using var provider = _provider("Fail", new FixedSource());

    var validator = _validators(provider).Single();
    await validator.StartAsync(CancellationToken.None);
    await validator.StopAsync(CancellationToken.None);

    await Assert.That(_warnings(provider)).IsEmpty();
  }

  [Test]
  public async Task CollisionsUnderWarn_SetExplicitly_DoNotStopStartupAsync() {
    await using var provider = _provider("Warn", new FixedSource(_perspective));

    await _validators(provider).Single().StartAsync(CancellationToken.None);

    await Assert.That(_warnings(provider).Length).IsEqualTo(1);
  }

  [Test]
  public async Task SeveralSources_ShareOneCheckAsync() {
    await using var provider = _provider(mode: null, new FixedSource(_perspective), new FixedSource(_model));

    await Assert.That(_validators(provider).Length).IsEqualTo(1)
      .Because("every per-assembly registration adds the check, and the host runs it once");
  }

  [Test]
  public async Task AddRegistrationCollisionSource_NullArguments_ThrowAsync() {
    await Assert.That(() => RegistrationCollisionServiceCollectionExtensions.AddRegistrationCollisionSource(null!, new FixedSource()))
      .Throws<ArgumentNullException>();
    await Assert.That(() => new ServiceCollection().AddRegistrationCollisionSource(null!))
      .Throws<ArgumentNullException>();
  }
}
