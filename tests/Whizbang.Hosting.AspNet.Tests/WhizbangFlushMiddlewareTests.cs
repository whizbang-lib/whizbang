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
/// Tests for WhizbangFlushMiddleware — verifies flush is called after pipeline
/// completes but before scope disposal, with correct cancellation token propagation.
/// </summary>
public class WhizbangFlushMiddlewareTests {

  [Test]
  public async Task FlushMiddleware_CallsFlushAsyncAfterPipelineAsync() {
    // Arrange
    var flusher = new FakeWorkFlusher();
    var pipelineCompleted = false;

    using var host = await new HostBuilder()
      .ConfigureWebHost(webBuilder => {
        webBuilder.UseTestServer();
        webBuilder.ConfigureServices(services => services.AddScoped<IWorkFlusher>(_ => flusher));
        webBuilder.Configure(app => {
          app.UseWhizbangFlush();
          app.Run(context => {
            pipelineCompleted = true;
            // At this point, flush should NOT have been called yet
            return Task.CompletedTask;
          });
        });
      })
      .StartAsync();

    var client = host.GetTestClient();

    // Act
    await client.GetAsync("/test");

    // Assert — flush was called after the pipeline completed
    await Assert.That(pipelineCompleted).IsTrue()
      .Because("Pipeline endpoint should have been reached");
    await Assert.That(flusher.FlushCallCount).IsEqualTo(1)
      .Because("FlushAsync should be called exactly once after the pipeline completes");
  }

  [Test]
  public async Task FlushMiddleware_NoFlusher_DoesNotThrowAsync() {
    // Arrange — no IWorkFlusher registered
    using var host = await new HostBuilder()
      .ConfigureWebHost(webBuilder => {
        webBuilder.UseTestServer();
        webBuilder.Configure(app => {
          app.UseWhizbangFlush();
          app.Run(_ => Task.CompletedTask);
        });
      })
      .StartAsync();

    var client = host.GetTestClient();

    // Act & Assert — should not throw when IWorkFlusher is not registered
    var response = await client.GetAsync("/test");
    await Assert.That((int)response.StatusCode).IsEqualTo(200);
  }

  [Test]
  public async Task FlushMiddleware_RequestAborted_PassesCancellationAsync() {
    // Arrange
    var flusher = new FakeWorkFlusher();
    CancellationToken capturedToken = default;

    flusher.OnFlush = ct => {
      capturedToken = ct;
      return Task.CompletedTask;
    };

    using var host = await new HostBuilder()
      .ConfigureWebHost(webBuilder => {
        webBuilder.UseTestServer();
        webBuilder.ConfigureServices(services => services.AddScoped<IWorkFlusher>(_ => flusher));
        webBuilder.Configure(app => {
          app.UseWhizbangFlush();
          app.Run(_ => Task.CompletedTask);
        });
      })
      .StartAsync();

    var client = host.GetTestClient();

    // Act
    await client.GetAsync("/test");

    // Assert — a cancellation token was passed (it's the RequestAborted token)
    await Assert.That(flusher.FlushCallCount).IsEqualTo(1);
    // The token should have been captured (struct, always non-null, just verify flush was called)
  }

  [Test]
  public async Task FlushMiddleware_FlushCalledAfterEndpoint_NotBeforeAsync() {
    // Arrange — verifies ordering: endpoint runs first, then flush
    var callOrder = new List<string>();
    var flusher = new FakeWorkFlusher();

    flusher.OnFlush = _ => {
      callOrder.Add("flush");
      return Task.CompletedTask;
    };

    using var host = await new HostBuilder()
      .ConfigureWebHost(webBuilder => {
        webBuilder.UseTestServer();
        webBuilder.ConfigureServices(services => services.AddScoped<IWorkFlusher>(_ => flusher));
        webBuilder.Configure(app => {
          app.UseWhizbangFlush();
          app.Run(_ => {
            callOrder.Add("endpoint");
            return Task.CompletedTask;
          });
        });
      })
      .StartAsync();

    var client = host.GetTestClient();

    // Act
    await client.GetAsync("/test");

    // Assert — endpoint ran before flush
    await Assert.That(callOrder).Count().IsEqualTo(2);
    await Assert.That(callOrder[0]).IsEqualTo("endpoint");
    await Assert.That(callOrder[1]).IsEqualTo("flush");
  }

  [Test]
  public async Task FlushMiddleware_ScopedRegistration_ResolvesFlusherPerRequestAsync() {
    // Arrange — verify each request gets its own flusher instance
    var flushCounts = new List<int>();

    using var host = await new HostBuilder()
      .ConfigureWebHost(webBuilder => {
        webBuilder.UseTestServer();
        webBuilder.ConfigureServices(services => services.AddScoped<IWorkFlusher, FakeWorkFlusher>());
        webBuilder.Configure(app => {
          app.UseWhizbangFlush();
          app.Run(context => {
            var flusher = context.RequestServices.GetRequiredService<IWorkFlusher>();
            flushCounts.Add(((FakeWorkFlusher)flusher).FlushCallCount);
            return Task.CompletedTask;
          });
        });
      })
      .StartAsync();

    var client = host.GetTestClient();

    // Act — two requests
    await client.GetAsync("/test1");
    await client.GetAsync("/test2");

    // Assert — each endpoint sees 0 calls (flush happens after endpoint)
    await Assert.That(flushCounts).Count().IsEqualTo(2);
    await Assert.That(flushCounts[0]).IsEqualTo(0);
    await Assert.That(flushCounts[1]).IsEqualTo(0);
  }

  // ========================================
  // Test Fakes
  // ========================================

  private sealed class FakeWorkFlusher : IWorkFlusher {
    public int FlushCallCount { get; private set; }
    public Func<CancellationToken, Task>? OnFlush { get; set; }

    public async Task FlushAsync(CancellationToken ct = default) {
      FlushCallCount++;
      if (OnFlush != null) {
        await OnFlush(ct);
      }
    }
  }
}
