using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Health;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Tests.Perspectives;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Health;

/// <summary>
/// That the health endpoint reports the streams whose stored form could not be read.
/// </summary>
/// <remarks>
/// Degraded, never faulted: the rest of the service serves, and the rows are parked with backoff
/// in the database rather than lost. What the operator needs is to see it after the log scrolled
/// away, with enough detail to know which perspective and where in the document.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Health/StoredFormHealthSource.cs</code-under-test>
[Category("Core")]
[Category("Health")]
public class StoredFormHealthSourceTests {
  private static StoredFormUnreadable _failure() {
    try {
      JsonSerializer.Deserialize("{\"At\":true}", HolderContext.Default.Holder);
    } catch (JsonException ex) {
      StoredFormUnreadable.TryClassify(ex, out var failure);
      return failure!;
    }
    throw new InvalidOperationException("the fixture did not fail");
  }

  /// <summary>Nothing unreadable, nothing to report.</summary>
  [Test]
  public async Task NothingUnreadableIsOperationalAsync() {
    var source = new StoredFormHealthSource(new StoredFormFailureRegistry(new FakeTimeProvider()));

    var health = await source.ReportAsync(CancellationToken.None);

    await Assert.That(source.Component).IsEqualTo("perspective-stored-forms");
    await Assert.That(health.State).IsEqualTo(ComponentState.Operational);
  }

  /// <summary>An unreadable stream degrades the component, with the count and the first detail.</summary>
  [Test]
  public async Task AnUnreadableStreamIsDegradedWithDetailAsync() {
    var registry = new StoredFormFailureRegistry(new FakeTimeProvider());
    var stream = Guid.CreateVersion7();
    registry.Record("Orders", stream, _failure());
    registry.Record("Orders", Guid.CreateVersion7(), _failure());
    var source = new StoredFormHealthSource(registry);

    var health = await source.ReportAsync(CancellationToken.None);

    await Assert.That(health.State).IsEqualTo(ComponentState.Degraded)
      .Because("the service serves and the rows are parked, so this is impaired, not broken");
    await Assert.That(health.Detail).Contains("2 stream(s)", StringComparison.Ordinal);
    await Assert.That(health.Detail).Contains("Orders", StringComparison.Ordinal);
    await Assert.That(health.Detail).Contains("$.At", StringComparison.Ordinal);
  }

  /// <summary>The worker pipeline registers the source and the registry it reads.</summary>
  [Test]
  public async Task TheWorkerPipelineRegistersTheSourceAsync() {
    var services = new ServiceCollection();
    services.AddWhizbangWorkers();

    var sources = services
      .Where(d => d.ServiceType == typeof(IWhizbangHealthSource))
      .Select(d => d.ImplementationType)
      .ToList();

    await Assert.That(sources).Contains(typeof(StoredFormHealthSource));
    await Assert.That(services.Any(d => d.ServiceType == typeof(StoredFormFailureRegistry))).IsTrue()
      .Because("the worker records into the same registry the health source reads");
  }
}
