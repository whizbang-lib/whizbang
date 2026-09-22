using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Configuration;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Locks the slice 22b.3d auto-wiring invariant:
/// 1. The consumer assembly's <c>[ModuleInitializer]</c> registers a callback with
///    <see cref="ServiceRegistrationCallbacks.PerspectivePersistenceOptions"/>.
/// 2. Firing the callback (as <c>AddWhizbang()</c> would via <c>InvokeAll</c>) sets
///    <see cref="BaseUpsertStrategy.PathOnePersistenceOptionsProvider"/> to a non-null
///    options factory.
/// 3. The factory produces a chain where serializing a perspective TModel with a
///    <c>[WhizbangId]</c> property emits the EF-compatible <c>{"Value":"&lt;guid&gt;"}</c>
///    nested-object form — the same byte format slice 22b.3a's integration test verifies
///    against EF Core's own writer.
/// </summary>
/// <remarks>
/// This is a pure unit test — no database, no DI container — because the auto-wire
/// path's correctness is structural: the callback either fires correctly or it doesn't.
/// Slice 22b.3c's integration tests already prove the end-to-end SQL semantics with the
/// hook set manually.
/// </remarks>
// Mutates the process-wide BaseUpsertStrategy.PathOnePersistenceOptionsProvider — serialize against the
// other persistence tests so it can't flip the provider mid-seed (the cross-test static race).
[NotInParallel("EFCorePostgresTests")]
[Category("Shard2")]
public class PerspectivePersistenceAutoWireTests {
  [After(Test)]
  public Task ResetHookAsync() {
    BaseUpsertStrategy.PathOnePersistenceOptionsProvider = null;
    return Task.CompletedTask;
  }

  [Test]
  public async Task ModuleInitializer_RegistersCallback_OnServiceRegistrationCallbacksAsync() {
    // Module initializers run on assembly load — by the time TUnit starts, the consumer
    // assembly's PerspectivePersistenceCallbackInitializer.Initialize has already fired,
    // so the callback property is set.
    await Assert.That(ServiceRegistrationCallbacks.PerspectivePersistenceOptions).IsNotNull();
  }

  [Test]
  public async Task FiringCallback_SetsPathOnePersistenceOptionsProvider_NonNullAsync() {
    // Arrange — start from a clean state so we can prove the callback flips the hook.
    BaseUpsertStrategy.PathOnePersistenceOptionsProvider = null;

    // Act — simulate what AddWhizbang's ServiceRegistrationCallbacks.InvokeAll(services, options) does.
    // Pass a throwaway IServiceCollection; the callback ignores it.
    var services = new ServiceCollection();
    ServiceRegistrationCallbacks.PerspectivePersistenceOptions!.Invoke(services);

    // Assert — Path 1 hook is now wired.
    var provider = BaseUpsertStrategy.PathOnePersistenceOptionsProvider;
    await Assert.That(provider).IsNotNull();
  }

  [Test]
  public async Task WiredOptionsProvider_ProducesEFCompatibleByteFormatAsync() {
    // Arrange — simulate AddWhizbang firing the auto-wire.
    BaseUpsertStrategy.PathOnePersistenceOptionsProvider = null;
    var services = new ServiceCollection();
    ServiceRegistrationCallbacks.PerspectivePersistenceOptions!.Invoke(services);

    // Act — exercise the options factory; it should yield a chain that serializes
    // [WhizbangId] properties in object mode.
    var provider = BaseUpsertStrategy.PathOnePersistenceOptionsProvider!;
    var options = provider();
    var testGuid = System.Guid.Parse("019e244a-6bda-78a9-a08f-a1011c9c31dd");
    var order = new Order {
      OrderId = new TestOrderId(testGuid),
      Amount = 100.00m,
      Status = "Created"
    };
    var orderTypeInfo = options.GetTypeInfo(typeof(Order));
    var json = System.Text.Json.JsonSerializer.Serialize(order, orderTypeInfo);

    // Assert — byte-format invariant matches what EF Core 10's ComplexProperty.ToJson writes.
    await Assert.That(json).Contains("\"OrderId\":{\"Value\":\"019e244a-6bda-78a9-a08f-a1011c9c31dd\"}");
    await Assert.That(json).DoesNotContain("\"OrderId\":\"019e244a-6bda-78a9-a08f-a1011c9c31dd\"");
  }
}
