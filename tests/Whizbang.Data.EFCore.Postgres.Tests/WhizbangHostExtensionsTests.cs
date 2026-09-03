using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Covers <see cref="WhizbangHostExtensions.EnsureWhizbangInitializedAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// Shares the "DbContextInitializationRegistry" not-in-parallel group with
/// <see cref="DbContextInitializationRegistryTests"/>: both mutate the same static registry. A
/// distinct key would let the two classes run concurrently and clobber each other's reset.
/// </para>
/// <para>
/// The idempotence guard is keyed on the host's root service provider, not on the process (issue
/// #620). Two hosts in one process — a test suite with a host per test, or a composition root that
/// hosts two services — used to share one process-wide "already initialized" flag, so the second
/// host was skipped and started against a database with no Whizbang schema at all; the first thing
/// to touch it was the duty elector's <c>record_capability</c> call, surfaced as a Kestrel bind
/// cancellation three layers away.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/WhizbangHostExtensions.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/DbContextInitializationRegistry.cs</code-under-test>
[NotInParallel("DbContextInitializationRegistry")]
[Category("Shard3")]
public class WhizbangHostExtensionsTests {

  private sealed class FakeInitializedContext;

  [Before(Test)]
  public void ResetStaticState() {
    var initializersField = typeof(DbContextInitializationRegistry)
        .GetField("_initializers", BindingFlags.Static | BindingFlags.NonPublic)!;
    var list = (System.Collections.IList)initializersField.GetValue(null)!;
    list.Clear();

    // The pre-#620 process-wide flag. The guard is now keyed per service provider (a weak table
    // that fresh hosts never collide in), so this only matters for a build that still carries the
    // flag — tolerated rather than required.
    typeof(DbContextInitializationRegistry)
        .GetField("_initialized", BindingFlags.Static | BindingFlags.NonPublic)
        ?.SetValue(null, 0);
  }

  private static IHost _host() => new HostBuilder()
      .ConfigureServices(services => services.AddLogging())
      .Build();

  [Test]
  public async Task EnsureWhizbangInitializedAsync_WithNullHost_ThrowsArgumentNullExceptionAsync() {
    await Assert.That(async () =>
      await ((IHost)null!).EnsureWhizbangInitializedAsync()
    ).ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task EnsureWhizbangInitializedAsync_RunsRegisteredInitializersAsync() {
    var invoked = false;
    DbContextInitializationRegistry.Register<FakeInitializedContext>((_, _, _) => {
      invoked = true;
      return Task.CompletedTask;
    });

    using var host = _host();

    await host.EnsureWhizbangInitializedAsync();

    await Assert.That(invoked).IsTrue();
  }

  [Test]
  public async Task EnsureWhizbangInitializedAsync_PassesALoggerWhenLoggingIsRegisteredAsync() {
    ILogger? seenLogger = null;
    DbContextInitializationRegistry.Register<FakeInitializedContext>((_, logger, _) => {
      seenLogger = logger;
      return Task.CompletedTask;
    });

    using var host = _host();

    await host.EnsureWhizbangInitializedAsync();

    await Assert.That(seenLogger).IsNotNull();
  }

  [Test]
  public async Task EnsureWhizbangInitializedAsync_WithNoRegistrations_CompletesSuccessfullyAsync() {
    using var host = _host();

    await host.EnsureWhizbangInitializedAsync();

    await Assert.That(DbContextInitializationRegistry.Count).IsEqualTo(0);
  }

  [Test]
  public async Task EnsureWhizbangInitializedAsync_TwoHostsInOneProcess_InitializesBothAsync() {
    var initializedFor = new List<IServiceProvider>();
    DbContextInitializationRegistry.Register<FakeInitializedContext>(
        (sp, _, _) => { initializedFor.Add(sp); return Task.CompletedTask; });
    using var first = _host();
    using var second = _host();

    await first.EnsureWhizbangInitializedAsync();
    await second.EnsureWhizbangInitializedAsync();

    await Assert.That(initializedFor.Count).IsEqualTo(2)
      .Because("each host owns its own database; the second must not inherit the first's 'done'");
    await Assert.That(initializedFor[0]).IsSameReferenceAs(first.Services)
      .Because("the callback receives the host's ROOT provider — it creates its own scope, and "
             + "keying the guard on a per-call scope would re-initialize on every call");
    await Assert.That(initializedFor[1]).IsSameReferenceAs(second.Services);
  }

  [Test]
  public async Task EnsureWhizbangInitializedAsync_SameHostTwice_InitializesOnceAsync() {
    var calls = 0;
    DbContextInitializationRegistry.Register<FakeInitializedContext>(
        (_, _, _) => { calls++; return Task.CompletedTask; });
    using var host = _host();

    await host.EnsureWhizbangInitializedAsync();
    await host.EnsureWhizbangInitializedAsync();

    await Assert.That(calls).IsEqualTo(1)
      .Because("the documented idempotence is per host: explicit call plus the hosted initializer "
             + "service must still add up to one initialization");
  }

  [Test]
  public async Task EnsureWhizbangInitializedAsync_HostWithoutLogging_InitializesWithANullLoggerAsync() {
    // A bare host with no ILoggerFactory registered: initialization must still run, and the
    // callback simply receives no logger — the logger is a convenience, never a prerequisite.
    ILogger? seenLogger = new LoggerFactory().CreateLogger("sentinel");
    var invoked = false;
    DbContextInitializationRegistry.Register<FakeInitializedContext>((_, logger, _) => {
      seenLogger = logger;
      invoked = true;
      return Task.CompletedTask;
    });
    using var host = new BareHost(new ServiceCollection().BuildServiceProvider());

    await host.EnsureWhizbangInitializedAsync();

    await Assert.That(invoked).IsTrue();
    await Assert.That(seenLogger).IsNull();
  }

  /// <summary>An <see cref="IHost"/> over a provider with nothing registered, not even logging.</summary>
  private sealed class BareHost(IServiceProvider services) : IHost {
    public IServiceProvider Services { get; } = services;
    public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public void Dispose() {
      // Nothing owned — the provider belongs to the test.
    }
  }
}
