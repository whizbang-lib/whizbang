using Microsoft.Extensions.Hosting;
using Whizbang.Core.Messaging;

namespace Whizbang.Testing.Tests.TestSupport;

/// <summary>
/// Minimal <see cref="IHost"/> test double whose service provider resolves only
/// <see cref="IReceptorRegistry"/>. Enough for the lifecycle awaiters under test.
/// </summary>
internal sealed class FakeHost(IReceptorRegistry registry) : IHost {
  public IServiceProvider Services { get; } = new RegistryServiceProvider(registry);

  public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

  public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

  public void Dispose() {
    // Nothing to dispose - test double.
  }

  private sealed class RegistryServiceProvider(IReceptorRegistry registry) : IServiceProvider {
    public object? GetService(Type serviceType) =>
      serviceType == typeof(IReceptorRegistry) ? registry : null;
  }
}
