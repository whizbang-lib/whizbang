using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Startup;
using Whizbang.Core.Workers;

namespace Whizbang.Hosting.AspNet.Tests;

/// <summary>
/// Increment 5's primary surface: <c>MapWhizbangStartupStatus</c>. Locks the proposal's three
/// non-negotiable constraints — no shared failure domain (the route exempts itself from the
/// availability gate), no information disclosure by default (<c>reason</c> strings are a separate
/// opt-in, not a verbosity dial), and honest degradation (not-started is a stated condition, and
/// so is an unreachable fleet — never an empty list).
/// </summary>
/// <code-under-test>src/Whizbang.Hosting.AspNet/StartupStatusEndpoints.cs</code-under-test>
/// <code-under-test>src/Whizbang.Hosting.AspNet/WhizbangAvailabilityExemptions.cs</code-under-test>
public class StartupStatusEndpointsTests {

  private sealed class FakeGate(bool ready = false) : ISchemaReadyGate {
    public bool IsReady { get; private set; } = ready;
    public void MarkReady() => IsReady = true;
    public Task WaitForReadyAsync(CancellationToken cancellationToken = default) {
      return IsReady ? Task.CompletedTask : Task.Delay(Timeout.Infinite, cancellationToken);
    }
  }

  private sealed class ThrowingFleetSource : IStartupFleetStatusSource {
    public Task<IReadOnlyList<FleetInstanceStatus>> GetFleetAsync(CancellationToken cancellationToken) =>
      throw new InvalidOperationException("relation tenant_secrets_wh_service_instances does not exist");
  }

  private sealed class FixedFleetSource(IReadOnlyList<FleetInstanceStatus> rows) : IStartupFleetStatusSource {
    public Task<IReadOnlyList<FleetInstanceStatus>> GetFleetAsync(CancellationToken cancellationToken) =>
      Task.FromResult(rows);
  }

  private static IHost _buildHost(
      Action<IServiceCollection>? configureServices = null,
      string pattern = "/whizbang/startup",
      bool includeReasons = false) {
    return new HostBuilder()
      .ConfigureWebHost(web => {
        web.UseTestServer();
        web.ConfigureServices(s => {
          s.AddRouting();
          s.AddSingleton<StartupPipelineState>();
          s.AddSingleton<IStartupPipelineState>(sp => sp.GetRequiredService<StartupPipelineState>());
          configureServices?.Invoke(s);
        });
        web.Configure(app => {
          app.UseRouting();
          app.UseEndpoints(e => e.MapWhizbangStartupStatus(pattern, includeReasons));
        });
      })
      .Build();
  }

  private static StartupStepDescriptor _step(string name, bool blocking = true) =>
    new() { Name = name, Blocking = blocking };

  private static async Task _driveAsync(StartupPipelineState state, params (string Name, StartupStepOutcome? Outcome, string? Reason)[] steps) {
    var plan = new StartupRunPlan([.. steps.Select(s => _step(s.Name))]);
    await state.OnRunStartingAsync(plan, CancellationToken.None);
    foreach (var (name, outcome, reason) in steps) {
      await state.OnStepStartingAsync(new StartupStepContext(_step(name)), CancellationToken.None);
      if (outcome is { } o) {
        await state.OnStepCompletedAsync(
          new StartupStepResult(name, o, TimeSpan.FromMilliseconds(5), reason), CancellationToken.None);
      }
    }
  }

  // ── honest degradation ──────────────────────────────────────────────────

  [Test]
  public async Task NotStarted_IsAStatedCondition_NeverAnEmptyRunAsync() {
    using var host = _buildHost();
    await host.StartAsync();
    var client = host.GetTestClient();

    var body = await client.GetStringAsync("/whizbang/startup");

    await Assert.That(body).Contains("\"started\":false")
      .Because("an empty step list and a pipeline that has not begun must not serialize identically");
    await Assert.That(body).DoesNotContain("\"steps\"");
  }

  [Test]
  public async Task Fleet_WithNoSourceRegistered_ReportsUnavailableWithTheReasonAsync() {
    using var host = _buildHost();
    await host.StartAsync();
    var client = host.GetTestClient();

    var body = await client.GetStringAsync("/whizbang/startup");

    await Assert.That(body).Contains("\"available\":false");
    await Assert.That(body).Contains("no fleet status source registered")
      .Because("unreachable is a stated condition, never an empty list — 'no other instances' and "
             + "'cannot see the other instances' mean opposite things during an incident");
  }

  // ── the instance section, from memory ───────────────────────────────────

  [Test]
  public async Task Started_ProjectsTheOrderedStepsWithLiveStatusAsync() {
    using var host = _buildHost();
    await host.StartAsync();
    var state = host.Services.GetRequiredService<StartupPipelineState>();
    await _driveAsync(state,
      ("Migrate", StartupStepOutcome.Completed, null),
      ("Reconcile", null, null));   // running right now

    var body = await host.GetTestClient().GetStringAsync("/whizbang/startup");

    await Assert.That(body).Contains("\"started\":true");
    await Assert.That(body).Contains("\"currentStep\":\"Reconcile\"");
    await Assert.That(body).Contains("\"name\":\"Migrate\"");
    await Assert.That(body).Contains("\"status\":\"Completed\"");
    await Assert.That(body).Contains("\"status\":\"Running\"");
    await Assert.That(body).Contains("\"pipelineReady\":false")
      .Because("a blocking step is still running — the instance section is exact and current");
  }

  // ── information disclosure: reasons are an opt-in level ────────────────

  [Test]
  public async Task Reasons_AreExcludedByDefault_TheyCarryContentTheFrameworkDoesNotControlAsync() {
    using var host = _buildHost();
    await host.StartAsync();
    var state = host.Services.GetRequiredService<StartupPipelineState>();
    await _driveAsync(state, ("Migrate", StartupStepOutcome.Failed, "42P01: relation tenant_secrets does not exist"));

    var body = await host.GetTestClient().GetStringAsync("/whizbang/startup");

    await Assert.That(body).Contains("\"status\":\"Failed\"")
      .Because("the outcome itself is framework-authored and always visible");
    await Assert.That(body).DoesNotContain("tenant_secrets")
      .Because("reasons originate in exception messages — schema names, constraint names, raw "
             + "driver text — and are a separate opt-in level, not a verbosity dial");
  }

  [Test]
  public async Task Reasons_AppearWhenTheHostOptsInAsync() {
    using var host = _buildHost(includeReasons: true);
    await host.StartAsync();
    var state = host.Services.GetRequiredService<StartupPipelineState>();
    await _driveAsync(state, ("Migrate", StartupStepOutcome.Failed, "42P01: relation tenant_secrets does not exist"));

    var body = await host.GetTestClient().GetStringAsync("/whizbang/startup");

    await Assert.That(body).Contains("tenant_secrets");
  }

  [Test]
  public async Task Fleet_FailureText_RidesTheSameOptInAsync() {
    using var terse = _buildHost(s => s.AddSingleton<IStartupFleetStatusSource>(new ThrowingFleetSource()));
    await terse.StartAsync();
    var terseBody = await terse.GetTestClient().GetStringAsync("/whizbang/startup");

    await Assert.That(terseBody).Contains("\"available\":false");
    await Assert.That(terseBody).Contains("fleet query failed");
    await Assert.That(terseBody).DoesNotContain("tenant_secrets")
      .Because("driver error text is content the framework does not control");

    using var verbose = _buildHost(
      s => s.AddSingleton<IStartupFleetStatusSource>(new ThrowingFleetSource()), includeReasons: true);
    await verbose.StartAsync();
    var verboseBody = await verbose.GetTestClient().GetStringAsync("/whizbang/startup");

    await Assert.That(verboseBody).Contains("tenant_secrets");
  }

  // ── the fleet section, from the database ────────────────────────────────

  [Test]
  public async Task Fleet_WithASource_ReportsEachRowWithItsOwnAgeAsync() {
    var instanceId = Guid.NewGuid();
    var rows = new List<FleetInstanceStatus> {
      new(instanceId, "svc-a", "host-1", DateTimeOffset.UtcNow.AddSeconds(-40)),
    };
    using var host = _buildHost(s => s.AddSingleton<IStartupFleetStatusSource>(new FixedFleetSource(rows)));
    await host.StartAsync();

    var body = await host.GetTestClient().GetStringAsync("/whizbang/startup");

    await Assert.That(body).Contains("\"available\":true");
    await Assert.That(body).Contains(instanceId.ToString());
    await Assert.That(body).Contains("\"lastSeenSecondsAgo\":")
      .Because("every fleet row is only as current as that instance's last heartbeat — each "
             + "carries its own age so a thirty-seconds-dead instance never reads as healthy");
  }

  // ── no shared failure domain: the route exempts itself from the gate ───

  [Test]
  public async Task SelfExemption_TheAvailabilityGateNever503sTheStatusRouteAsync() {
    using var host = new HostBuilder()
      .ConfigureWebHost(web => {
        web.UseTestServer();
        web.ConfigureServices(s => {
          s.AddRouting();
          s.AddWhizbangAspNet();
          s.AddSingleton<ISchemaReadyGate>(new FakeGate(ready: false));   // migrations "in progress"
          s.Configure<WhizbangAvailabilityOptions>(o => o.Mode = AvailabilityGateMode.AllNonExempt);
          s.AddSingleton<StartupPipelineState>();
          s.AddSingleton<IStartupPipelineState>(sp => sp.GetRequiredService<StartupPipelineState>());
        });
        web.Configure(app => {
          app.UseRouting();
          app.UseEndpoints(e => {
            e.MapWhizbangStartupStatus("/ops/boot");
            e.MapGet("/other", () => "hello");
          });
        });
      })
      .Build();
    await host.StartAsync();
    var client = host.GetTestClient();

    var other = await client.GetAsync("/other");
    await Assert.That((int)other.StatusCode).IsEqualTo(503)
      .Because("control: the gate is closed and this route is not exempt");

    var status = await client.GetAsync("/ops/boot");
    await Assert.That((int)status.StatusCode).IsEqualTo(200)
      .Because("a startup endpoint that cannot answer until startup finishes is worthless "
             + "precisely when it is wanted — mapping registers its own exemption, on whatever "
             + "route the host chose");
  }
}
