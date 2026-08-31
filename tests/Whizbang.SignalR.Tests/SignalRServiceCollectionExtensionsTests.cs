using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.SignalR.DependencyInjection;

namespace Whizbang.SignalR.Tests;

/// <summary>
/// Unit tests for <see cref="SignalRServiceCollectionExtensions"/>. Both overloads
/// register a JSON protocol configurator; that delegate only runs when the protocol
/// options are resolved, so the tests resolve them rather than stopping at registration.
/// </summary>
public class SignalRServiceCollectionExtensionsTests {

  private static ServiceProvider _build(Action<IServiceCollection> configure) {
    var services = new ServiceCollection();
    services.AddLogging();
    configure(services);
    return services.BuildServiceProvider();
  }

  [Test]
  public async Task AddWhizbangSignalR_ReturnsABuilderAsync() {
    var services = new ServiceCollection();
    services.AddLogging();

    var builder = services.AddWhizbangSignalR();

    await Assert.That(builder).IsNotNull();
  }

  [Test]
  public async Task AddWhizbangSignalR_AppliesTheCombinedJsonContextAsync() {
    using var provider = _build(s => s.AddWhizbangSignalR());

    var options = provider.GetRequiredService<IOptions<JsonHubProtocolOptions>>().Value;

    await Assert.That(options.PayloadSerializerOptions).IsNotNull();
    await Assert.That(options.PayloadSerializerOptions.TypeInfoResolver).IsNotNull();
  }

  [Test]
  public async Task AddWhizbangSignalR_WithHubOptions_AppliesTheCombinedJsonContextAsync() {
    using var provider = _build(s => s.AddWhizbangSignalR(hub => hub.EnableDetailedErrors = true));

    var options = provider.GetRequiredService<IOptions<JsonHubProtocolOptions>>().Value;

    await Assert.That(options.PayloadSerializerOptions).IsNotNull();
    await Assert.That(options.PayloadSerializerOptions.TypeInfoResolver).IsNotNull();
  }

  [Test]
  public async Task AddWhizbangSignalR_WithHubOptions_RunsTheCallerDelegateAsync() {
    using var provider = _build(s => s.AddWhizbangSignalR(hub => hub.EnableDetailedErrors = true));

    var hubOptions = provider.GetRequiredService<IOptions<HubOptions>>().Value;

    await Assert.That(hubOptions.EnableDetailedErrors).IsTrue();
  }

  [Test]
  public async Task AddWhizbangSignalR_WithoutHubOptions_LeavesDetailedErrorsOffAsync() {
    using var provider = _build(s => s.AddWhizbangSignalR());

    var hubOptions = provider.GetRequiredService<IOptions<HubOptions>>().Value;

    await Assert.That(hubOptions.EnableDetailedErrors).IsNotEqualTo(true);
  }
}
