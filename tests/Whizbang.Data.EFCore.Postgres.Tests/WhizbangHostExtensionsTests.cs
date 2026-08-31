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
/// Shares the "DbContextInitializationRegistry" not-in-parallel group with
/// <see cref="DbContextInitializationRegistryTests"/>: both mutate the same static
/// registry and its one-shot _initialized flag. A distinct key would let the two
/// classes run concurrently and clobber each other's reset.
/// </remarks>
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

    var initializedField = typeof(DbContextInitializationRegistry)
        .GetField("_initialized", BindingFlags.Static | BindingFlags.NonPublic)!;
    initializedField.SetValue(null, 0);
  }

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

    using var host = new HostBuilder()
        .ConfigureServices(services => services.AddLogging())
        .Build();

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

    using var host = new HostBuilder()
        .ConfigureServices(services => services.AddLogging())
        .Build();

    await host.EnsureWhizbangInitializedAsync();

    await Assert.That(seenLogger).IsNotNull();
  }

  [Test]
  public async Task EnsureWhizbangInitializedAsync_WithNoRegistrations_CompletesSuccessfullyAsync() {
    using var host = new HostBuilder()
        .ConfigureServices(services => services.AddLogging())
        .Build();

    await host.EnsureWhizbangInitializedAsync();

    await Assert.That(DbContextInitializationRegistry.Count).IsEqualTo(0);
  }
}
