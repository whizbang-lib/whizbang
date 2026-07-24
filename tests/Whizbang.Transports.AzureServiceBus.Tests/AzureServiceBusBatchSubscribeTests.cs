using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Transports;
using Whizbang.Core.Workers;
using Whizbang.Transports.AzureServiceBus;

namespace Whizbang.Transports.AzureServiceBus.Tests;

/// <summary>
/// Tests for ASB SubscribeBatchAsync contract.
/// Guard clauses and method existence. Channel-level batch behavior
/// (per-message CompleteMessageAsync, session fallback) is tested in
/// integration tests with ASB emulator.
/// </summary>
[Timeout(10_000)]
public class AzureServiceBusBatchSubscribeTests {

  private const string EMULATOR_CONNECTION_STRING =
    "Endpoint=sb://localhost;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=ZmFrZWtleQ==;UseDevelopmentEmulator=true";

  [Test]
  public async Task SubscribeBatchAsync_NullHandler_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var transport = _createTransport();
    await transport.InitializeAsync();

    // Act & Assert
    await Assert.ThrowsAsync<ArgumentNullException>(async () =>
      await transport.SubscribeBatchAsync(
        null!,
        new TransportDestination("test-topic", "test-sub"),
        new TransportBatchOptions()
      )
    );
  }

  [Test]
  public async Task SubscribeBatchAsync_NullDestination_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var transport = _createTransport();
    await transport.InitializeAsync();

    // Act & Assert
    await Assert.ThrowsAsync<ArgumentNullException>(async () =>
      await transport.SubscribeBatchAsync(
        (batch, ct) => Task.CompletedTask,
        null!,
        new TransportBatchOptions()
      )
    );
  }

  [Test]
  public async Task SubscribeBatchAsync_NullBatchOptions_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var transport = _createTransport();
    await transport.InitializeAsync();

    // Act & Assert
    await Assert.ThrowsAsync<ArgumentNullException>(async () =>
      await transport.SubscribeBatchAsync(
        (batch, ct) => Task.CompletedTask,
        new TransportDestination("test-topic", "test-sub"),
        null!
      )
    );
  }

  [Test]
  public async Task SubscribeBatchAsync_Disposed_ThrowsObjectDisposedExceptionAsync() {
    // Arrange
    var transport = _createTransport();
    await transport.InitializeAsync();
    await transport.DisposeAsync();

    // Act & Assert
    await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
      await transport.SubscribeBatchAsync(
        (batch, ct) => Task.CompletedTask,
        new TransportDestination("test-topic", "test-sub"),
        new TransportBatchOptions()
      )
    );
  }

  [Test]
  public async Task SubscribeBatchAsync_MethodExistsOnInterfaceAsync() {
    // Verify ITransport has SubscribeBatchAsync (not SubscribeAsync)
    var methods = typeof(ITransport).GetMethods();
    var hasSubscribeBatch = methods.Any(m => m.Name == "SubscribeBatchAsync");
    var hasSubscribe = methods.Any(m => m.Name == "SubscribeAsync");

    await Assert.That(hasSubscribeBatch).IsTrue()
      .Because("ITransport should have SubscribeBatchAsync");
    await Assert.That(hasSubscribe).IsFalse()
      .Because("SubscribeAsync should be removed from ITransport");
  }

  // ========================================
  // Helpers
  // ========================================

  private static AzureServiceBusTransport _createTransport() {
    var client = new ServiceBusClient(EMULATOR_CONNECTION_STRING);
    var jsonOptions = new JsonSerializerOptions {
      TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };
    var options = new AzureServiceBusOptions {
      AutoProvisionInfrastructure = true
    };
    var logger = LoggerFactory
      .Create(builder => builder.SetMinimumLevel(LogLevel.Debug))
      .CreateLogger<AzureServiceBusTransport>();

    return new AzureServiceBusTransport(client, jsonOptions, options, logger);
  }
}
