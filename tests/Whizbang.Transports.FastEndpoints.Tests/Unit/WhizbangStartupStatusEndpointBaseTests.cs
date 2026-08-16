using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Startup;

namespace Whizbang.Transports.FastEndpoints.Tests.Unit;

/// <summary>
/// The FastEndpoints flavor of the startup status surface. The projection lives in
/// <see cref="StartupStatusReporter"/> and is covered exhaustively at the ASP.NET surface; these
/// tests hold the FastEndpoints base to its own contract — the consumer's endpoint declares route
/// and security, the base answers with the shared report, and <c>reason</c> content stays behind
/// the <c>IncludeReasons</c> opt-in.
/// </summary>
[Category("FastEndpoints")]
public class WhizbangStartupStatusEndpointBaseTests {

  private sealed class TerseEndpoint : WhizbangStartupStatusEndpointBase {
    public override void Configure() {
      Get("/whizbang/startup");
      AllowAnonymous();
    }
    public Task<StartupStatusReport> BuildForTestAsync(CancellationToken ct) => BuildReportAsync(ct);
  }

  private sealed class VerboseEndpoint : WhizbangStartupStatusEndpointBase {
    protected override bool IncludeReasons => true;
    public override void Configure() {
      Get("/ops/boot");
      AllowAnonymous();
    }
    public Task<StartupStatusReport> BuildForTestAsync(CancellationToken ct) => BuildReportAsync(ct);
  }

  private static async Task<StartupPipelineState> _stateWithFailedMigrateAsync() {
    var state = new StartupPipelineState();
    var step = new StartupStepDescriptor { Name = "Migrate" };
    await state.OnRunStartingAsync(new StartupRunPlan([step]), CancellationToken.None);
    await state.OnStepStartingAsync(new StartupStepContext(step), CancellationToken.None);
    await state.OnStepCompletedAsync(
      new StartupStepResult("Migrate", StartupStepOutcome.Failed, TimeSpan.FromMilliseconds(3),
        "42P01: relation tenant_secrets does not exist"),
      CancellationToken.None);
    return state;
  }

  private static TEndpoint _endpointOver<TEndpoint>(IServiceProvider provider)
      where TEndpoint : class, IEndpoint {
    // Initializes FastEndpoints' global test service resolver — required by Factory.Create.
    // The endpoints under test resolve everything from the HttpContext's RequestServices, so
    // the global registration stays empty.
    Factory.RegisterTestServices(_ => { });
    var httpContext = new DefaultHttpContext { RequestServices = provider };
    return Factory.Create<TEndpoint>(httpContext);
  }

  [Test]
  public async Task BuildReport_NotStarted_IsAStatedConditionAsync() {
    var services = new ServiceCollection();
    services.AddSingleton<IStartupPipelineState>(new StartupPipelineState());
    await using var provider = services.BuildServiceProvider();
    var endpoint = _endpointOver<TerseEndpoint>(provider);

    var report = await endpoint.BuildForTestAsync(CancellationToken.None);

    await Assert.That(report.Instance.Started).IsFalse()
      .Because("an empty step list and a pipeline that has not begun must not serialize identically");
    await Assert.That(report.Instance.Steps).IsNull();
    await Assert.That(report.Fleet.Available).IsFalse()
      .Because("no fleet source registered is a stated condition, never an empty fleet");
  }

  [Test]
  public async Task BuildReport_ReasonsStayBehindTheOptInAsync() {
    var services = new ServiceCollection();
    services.AddSingleton<IStartupPipelineState>(await _stateWithFailedMigrateAsync());
    await using var provider = services.BuildServiceProvider();

    var terse = await _endpointOver<TerseEndpoint>(provider).BuildForTestAsync(CancellationToken.None);
    await Assert.That(terse.Instance.Steps![0].Outcome).IsEqualTo(StartupStepOutcome.Failed)
      .Because("the outcome itself is framework-authored and always visible");
    await Assert.That(terse.Instance.Steps![0].Reason).IsNull()
      .Because("reasons originate in exception messages and are a separate opt-in level — the "
             + "FastEndpoints surface must hold the same disclosure line as the others");

    var verbose = await _endpointOver<VerboseEndpoint>(provider).BuildForTestAsync(CancellationToken.None);
    await Assert.That(verbose.Instance.Steps![0].Reason).Contains("tenant_secrets");
  }
}
