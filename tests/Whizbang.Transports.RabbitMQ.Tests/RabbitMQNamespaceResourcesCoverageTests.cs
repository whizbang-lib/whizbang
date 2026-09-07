using RabbitMQ.Client;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Transports.RabbitMQ;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Transports.RabbitMQ.Tests;

/// <summary>
/// Round-23 coverage additions for the internal <see cref="RabbitMQNamespaceResources"/>: the
/// loud, informative failure when a caller asks for a TransportNamespace key that was never
/// configured, and the double-<c>Dispose()</c> guard that keeps a defensive or reentrant
/// shutdown from re-iterating (and double-disposing) the cached connections/pools.
/// </summary>
/// <code-under-test>src/Whizbang.Transports.RabbitMQ/RabbitMQNamespaceResources.cs</code-under-test>
public class RabbitMQNamespaceResourcesCoverageTests {

  // A typo'd or forgotten TransportNamespace key at a publish/provision call site must fail
  // LOUDLY and name what IS configured — otherwise a message silently resolves against whatever
  // key the lookup happens to land on (or crashes downstream with no clue which namespace was
  // meant) instead of failing once, clearly, at first use.
  [Test]
  public async Task Get_UnconfiguredNamespaceKey_ThrowsArgumentExceptionNamingConfiguredKeysAsync() {
    var factory = new StubNamespaceConnectionFactory();
    var resources = new RabbitMQNamespaceResources(
      factory,
      new RabbitMQOptions(),
      new Dictionary<string, string> { ["bulk"] = "amqp://fake-broker-host/bulk" });

    var ex = await Assert.ThrowsAsync<ArgumentException>(() => {
      resources.Get("does-not-exist");
      return Task.CompletedTask;
    });

    await Assert.That(ex!.Message).Contains("does-not-exist");
    await Assert.That(ex.Message).Contains("bulk")
      .Because("the exception must name what IS configured, or an operator can't tell a typo "
             + "from a genuinely missing entry");
    await Assert.That(ex.ParamName).IsEqualTo("namespaceKey");

    resources.Dispose();
  }

  // The DI container disposes this singleton exactly once in normal operation, but a defensive
  // manual Dispose() (or a race between two shutdown paths) must not re-enter the disposal loop
  // and attempt to double-dispose the same cached connection/pool pair — a real IConnection can
  // throw or corrupt state on a second Close/Dispose.
  [Test]
  public async Task Dispose_CalledTwice_SecondCallIsANoOpAsync() {
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(new FakeChannel()));
    var factory = new StubNamespaceConnectionFactory { Connection = connection };
    var resources = new RabbitMQNamespaceResources(
      factory,
      new RabbitMQOptions(),
      new Dictionary<string, string> { ["bulk"] = "amqp://fake-broker-host/bulk" });
    _ = resources.Get("bulk"); // opens the connection/pool so Dispose() has something to iterate

    resources.Dispose();

    await Assert.That(() => resources.Dispose()).ThrowsNothing();
  }

  private sealed class StubNamespaceConnectionFactory : IRabbitMQNamespaceConnectionFactory {
    public IConnection? Connection { get; set; }

    public IConnection CreateConnection(string namespaceKey, string connectionString, RabbitMQOptions options) =>
      Connection ?? throw new InvalidOperationException("Test did not configure a connection for this call.");
  }
}
