using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Tests for WhizbangDatabaseInitializerService hosted service.
/// Verifies delegation to DbContextInitializationRegistry.
/// </summary>
[NotInParallel("DbContextInitializationRegistry")]
public class WhizbangDatabaseInitializerServiceTests {
  [Before(Test)]
  public void ResetStaticState() {
    // Reset DbContextInitializationRegistry static state
    var initializersField = typeof(DbContextInitializationRegistry)
        .GetField("_initializers", BindingFlags.Static | BindingFlags.NonPublic)!;
    var list = (System.Collections.IList)initializersField.GetValue(null)!;
    list.Clear();

    var initializedField = typeof(DbContextInitializationRegistry)
        .GetField("_initialized", BindingFlags.Static | BindingFlags.NonPublic)!;
    initializedField.SetValue(null, 0);
  }

  [Test]
  public async Task StartAsync_CallsInitializeAllAsyncAsync() {
    // Arrange
    var callbackInvoked = false;
    DbContextInitializationRegistry.Register<FakeInitDbContext>(
        (_, _, _) => { callbackInvoked = true; return Task.CompletedTask; });

    var sp = new FakeServiceProvider();
    var logger = NullLogger<WhizbangDatabaseInitializerService>.Instance;
    var service = new WhizbangDatabaseInitializerService(sp, logger);

    // Act
    await service.StartAsync(CancellationToken.None);

    // Assert — the registered callback was invoked via InitializeAllAsync
    await Assert.That(callbackInvoked).IsTrue();
  }

  [Test]
  public async Task StopAsync_ReturnsCompletedTaskAsync() {
    // Arrange
    var sp = new FakeServiceProvider();
    var logger = NullLogger<WhizbangDatabaseInitializerService>.Instance;
    var service = new WhizbangDatabaseInitializerService(sp, logger);

    // Act
    var task = service.StopAsync(CancellationToken.None);

    // Assert
    await Assert.That(task.IsCompleted).IsTrue();
  }

  private sealed class FakeInitDbContext;

  private sealed class FakeServiceProvider : IServiceProvider {
    public object? GetService(Type serviceType) => null;
  }
}
