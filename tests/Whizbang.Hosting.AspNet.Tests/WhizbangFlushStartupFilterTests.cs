using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Hosting.AspNet;

namespace Whizbang.Hosting.AspNet.Tests;

/// <summary>
/// Tests for WhizbangFlushStartupFilter — verifies that the flush middleware
/// is automatically injected into the pipeline via IStartupFilter when
/// AddWhizbangAspNet is called during service registration.
/// </summary>
public class WhizbangFlushStartupFilterTests {

  [Test]
  public async Task StartupFilter_AutoInjectsMiddleware_FlushCalledAfterRequestAsync() {
    // Arrange — use AddWhizbangAspNet instead of manually adding middleware
    var flusher = new FakeWorkFlusher();

    using var host = await new HostBuilder()
      .ConfigureWebHost(webBuilder => {
        webBuilder.UseTestServer();
        webBuilder.ConfigureServices(services => {
          services.AddWhizbangAspNet();
          services.AddScoped<IWorkFlusher>(_ => flusher);
        });
        webBuilder.Configure(app => {
          // Note: NOT calling app.UseWhizbangFlush() — startup filter does it
          app.Run(_ => Task.CompletedTask);
        });
      })
      .StartAsync();

    var client = host.GetTestClient();

    // Act
    await client.GetAsync("/test");

    // Assert — flush was called automatically via startup filter
    await Assert.That(flusher.FlushCallCount).IsEqualTo(1)
      .Because("StartupFilter should automatically inject flush middleware");
  }

  [Test]
  public async Task StartupFilter_NoFlusherRegistered_DoesNotThrowAsync() {
    // Arrange — AddWhizbangAspNet but no IWorkFlusher
    using var host = await new HostBuilder()
      .ConfigureWebHost(webBuilder => {
        webBuilder.UseTestServer();
        webBuilder.ConfigureServices(services => {
          services.AddWhizbangAspNet();
        });
        webBuilder.Configure(app => {
          app.Run(_ => Task.CompletedTask);
        });
      })
      .StartAsync();

    var client = host.GetTestClient();

    // Act & Assert — should not throw
    var response = await client.GetAsync("/test");
    await Assert.That((int)response.StatusCode).IsEqualTo(200);
  }

  // ========================================
  // Test Fakes
  // ========================================

  private sealed class FakeWorkFlusher : IWorkFlusher {
    public int FlushCallCount { get; private set; }

    public Task FlushAsync(CancellationToken ct = default) {
      FlushCallCount++;
      return Task.CompletedTask;
    }
  }
}
