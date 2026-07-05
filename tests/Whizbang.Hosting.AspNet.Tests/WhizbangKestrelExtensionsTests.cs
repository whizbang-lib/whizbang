using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Hosting.AspNet;

namespace Whizbang.Hosting.AspNet.Tests;

/// <summary>
/// Verifies the Kestrel hardening helper suppresses the <c>Server: Kestrel</c> response banner.
/// </summary>
public class WhizbangKestrelExtensionsTests {

  /// <summary>
  /// Minimal <see cref="IWebHostBuilder"/> that records ConfigureServices callbacks so tests can replay
  /// them onto a real <see cref="ServiceCollection"/> (WebHostBuilder itself is obsolete, ASPDEPR004).
  /// </summary>
  private sealed class RecordingWebHostBuilder : IWebHostBuilder {
    public List<Action<WebHostBuilderContext, IServiceCollection>> ConfigureServicesActions { get; } = [];

#pragma warning disable ASPDEPR008 // IWebHost is obsolete but required to implement IWebHostBuilder.Build()
    public IWebHost Build() => throw new NotSupportedException();
#pragma warning restore ASPDEPR008

    public IWebHostBuilder ConfigureAppConfiguration(
        Action<WebHostBuilderContext, IConfigurationBuilder> configureDelegate) => this;

    public IWebHostBuilder ConfigureServices(Action<IServiceCollection> configureServices) {
      ConfigureServicesActions.Add((_, services) => configureServices(services));
      return this;
    }

    public IWebHostBuilder ConfigureServices(
        Action<WebHostBuilderContext, IServiceCollection> configureServices) {
      ConfigureServicesActions.Add(configureServices);
      return this;
    }

    public string? GetSetting(string key) => null;

    public IWebHostBuilder UseSetting(string key, string? value) => this;
  }

  [Test]
  public async Task ApplyWhizbangSecurityDefaults_DisablesServerHeaderAsync() {
    var options = new KestrelServerOptions();

    options.ApplyWhizbangSecurityDefaults();

    await Assert.That(options.AddServerHeader).IsFalse()
      .Because("The Server: Kestrel banner discloses the server implementation for no benefit.");
  }

  [Test]
  public async Task UseWhizbangKestrelSecurityDefaults_ConfiguresKestrelOptionsViaDIAsync() {
    var builder = new RecordingWebHostBuilder();

    var returned = builder.UseWhizbangKestrelSecurityDefaults();

    await Assert.That(returned).IsSameReferenceAs(builder);

    // Replay the recorded service registrations and resolve the configured Kestrel options.
    var services = new ServiceCollection();
    var context = new WebHostBuilderContext();
    foreach (var configure in builder.ConfigureServicesActions) {
      configure(context, services);
    }
    await using var provider = services.BuildServiceProvider();
    var kestrelOptions = provider.GetRequiredService<IOptions<KestrelServerOptions>>().Value;

    await Assert.That(kestrelOptions.AddServerHeader).IsFalse()
      .Because("The builder extension must flow the hardening into the DI-configured Kestrel options.");
  }
}
