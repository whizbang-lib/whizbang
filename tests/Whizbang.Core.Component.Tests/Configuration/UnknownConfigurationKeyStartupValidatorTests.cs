// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Configuration;

namespace Whizbang.Core.Tests.Configuration;

/// <summary>
/// #1490: the unknown-key check runs in the normal host start, registered by <c>AddWhizbang</c>, so a
/// deployment that still sets a removed key is told so with no wiring of its own.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Configuration/UnknownConfigurationKeyStartupValidator.cs</code-under-test>
/// <docs>operations/configuration/configuration-reference#unknown-keys</docs>
public class UnknownConfigurationKeyStartupValidatorTests {
  private const string MAINTENANCE_INTERVAL = "Whizbang:WorkCoordinator:MaintenanceInterval";

  // A real host builder and AddWhizbang, as an application registers it.
  private sealed class TestHost(IHost host, IServiceCollection services) : IDisposable {
    public IServiceProvider Services => host.Services;

    // Only the registrations that build the check: starting every hosted service AddWhizbang adds would
    // need the rest of the framework (serializer options, a database) registered too.
    public UnknownConfigurationKeyStartupValidator[] Validators => [.. services
      .Where(d => d.ServiceType == typeof(IHostedService)
        && d.ImplementationFactory is Func<IServiceProvider, UnknownConfigurationKeyStartupValidator>)
      .Select(d => (UnknownConfigurationKeyStartupValidator)d.ImplementationFactory!(host.Services))];

    public void Dispose() => host.Dispose();
  }

  private static TestHost _host(params (string Key, string Value)[] settings) {
    var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
    builder.Configuration.AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => (string?)s.Value));
    builder.Logging.AddFakeLogging();
    builder.Services.AddWhizbang();
    return new TestHost(builder.Build(), builder.Services);
  }

  private static IReadOnlyList<FakeLogRecord> _records(IServiceProvider provider) =>
    [.. provider.GetFakeLogCollector().GetSnapshot()
      .Where(r => r.Category == typeof(UnknownConfigurationKeyStartupValidator).FullName)];

  [Test]
  public async Task AddWhizbang_AHostStillSettingARemovedKey_WarnsOnceNamingTheReplacementAsync() {
    using var host = _host(
      (MAINTENANCE_INTERVAL, "00:05:00"),
      ("Whizbang:Workers:Maintenance:IntervalMinutes", "5"),
      ("Whizbang:PerspectiveRowRetention:Overrides:OrderView", "30"));

    var validators = host.Validators;
    await Assert.That(validators).Count().IsEqualTo(1)
      .Because("AddWhizbang wires the check into host start");
    await validators[0].StartAsync(CancellationToken.None);

    var records = _records(host.Services);
    await Assert.That(records).Count().IsEqualTo(1);
    await Assert.That(records[0].Level).IsEqualTo(LogLevel.Warning);
    await Assert.That(records[0].Message).Contains(MAINTENANCE_INTERVAL);
    await Assert.That(records[0].Message).Contains("Whizbang:Workers:Maintenance:IntervalMinutes");
  }

  [Test]
  public async Task AddWhizbang_ThrowMode_FailsStartAsync() {
    using var host = _host(
      (MAINTENANCE_INTERVAL, "00:05:00"),
      ("Whizbang:Configuration:UnknownKeys", "Throw"));

    var start = () => host.Validators[0].StartAsync(CancellationToken.None);

    var thrown = await Assert.That(start).Throws<InvalidOperationException>();
    await Assert.That(thrown!.Message).Contains(MAINTENANCE_INTERVAL);
  }

  [Test]
  public async Task AddWhizbang_IgnoreMode_IsQuietAsync() {
    using var host = _host(
      (MAINTENANCE_INTERVAL, "00:05:00"),
      ("Whizbang:Configuration:UnknownKeys", "Ignore"));

    await host.Validators[0].StartAsync(CancellationToken.None);

    await Assert.That(_records(host.Services)).IsEmpty();
  }

  [Test]
  public async Task AddWhizbang_CalledTwice_ChecksOnceAsync() {
    var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
    builder.Services.AddWhizbang();
    builder.Services.AddWhizbang();
    using var host = new TestHost(builder.Build(), builder.Services);

    await Assert.That(host.Validators).Count().IsEqualTo(1)
      .Because("one line per unknown key per start, however many times AddWhizbang is called");
  }

  [Test]
  public async Task AContainerWithoutConfiguration_StartsAndStopsQuietlyAsync() {
    var logger = new FakeLogger<UnknownConfigurationKeyStartupValidator>();
    var validator = new UnknownConfigurationKeyStartupValidator(
      Options.Create(new ConfigurationCheckOptions { UnknownKeys = UnknownConfigurationKeyMode.Throw }), logger, configuration: null);

    await validator.StartAsync(CancellationToken.None);
    await validator.StopAsync(CancellationToken.None);

    await Assert.That(logger.Collector.Count).IsEqualTo(0);
  }
}
