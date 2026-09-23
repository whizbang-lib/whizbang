using Whizbang.Testing.Options;

namespace Whizbang.Testing.Tests.Options;

/// <summary>
/// Locks the <see cref="StaticOptionsMonitor{TOptions}"/> contract: one fixed value served to
/// every named lookup, and a change subscription that hands back nothing to dispose.
/// </summary>
public class StaticOptionsMonitorTests {
  public sealed class ProbeOptions {
    public string Name { get; set; } = "";
  }

  [Test]
  public async Task Get_ForEveryName_ReturnsTheOneConfiguredValueAsync() {
    var value = new ProbeOptions { Name = "configured" };
    var monitor = new StaticOptionsMonitor<ProbeOptions>(value);

    // Framework workers resolve named option instances. A monitor that honored the name would
    // hand a hand-built worker a default-constructed options object instead of the one the test
    // configured, and the worker would silently run on framework defaults.
    await Assert.That(monitor.Get(null)).IsSameReferenceAs(value);
    await Assert.That(monitor.Get(Microsoft.Extensions.Options.Options.DefaultName)).IsSameReferenceAs(value);
    await Assert.That(monitor.Get("a-named-instance")).IsSameReferenceAs(value);
    await Assert.That(monitor.Get("another-named-instance")).IsSameReferenceAs(monitor.CurrentValue);
  }

  [Test]
  public async Task OnChange_ReturnsNoRegistrationToDisposeAsync() {
    var monitor = new StaticOptionsMonitor<ProbeOptions>(new ProbeOptions { Name = "configured" });

    var registration = monitor.OnChange((_, _) => throw new InvalidOperationException(
      "A monitor over one fixed value must never raise a change notification."));

    // The value never changes, so there is nothing to unsubscribe from. Callers written as
    // `using var _ = monitor.OnChange(...)` depend on the null: a non-null registration would
    // promise a notification this monitor can never deliver.
    await Assert.That(registration).IsNull();
    await Assert.That(monitor.CurrentValue.Name).IsEqualTo("configured");
  }
}
