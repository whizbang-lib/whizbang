using HotChocolate;
using HotChocolate.Execution;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Startup;

namespace Whizbang.Transports.HotChocolate.Tests.Unit;

/// <summary>
/// The <c>whizbangStartup</c> query field — the GraphQL flavor of the startup status surface. The
/// projection is shared through <see cref="StartupStatusReporter"/>; these tests hold the field to
/// its contract: explicitly contributed (opt-in), same two-section shape, reasons behind the
/// opt-in, honest not-started and fleet-unavailable degradation.
/// </summary>
[Category("HotChocolate")]
public class StartupStatusQueryTests {

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

  private const string QUERY = """
    {
      whizbangStartup {
        instance { started currentStep steps { name status reason } }
        fleet { available reason }
      }
    }
    """;

  [Test]
  public async Task WhizbangStartup_NotStarted_IsAStatedConditionAsync() {
    var services = new ServiceCollection();
    services.AddSingleton<IStartupPipelineState>(new StartupPipelineState());
    var result = await services
      .AddGraphQL()
      .AddQueryType(d => d.Name("Query"))
      .AddWhizbangStartupStatus()
      .ExecuteRequestAsync(QUERY);

    var json = result.ToJson();
    await Assert.That(json).Contains("\"started\": false")
      .Because("an empty step list and a pipeline that has not begun must not serialize identically");
    await Assert.That(json).Contains("\"available\": false")
      .Because("no fleet source registered is a stated condition, never an empty fleet");
  }

  [Test]
  public async Task WhizbangStartup_ReasonsStayBehindTheOptInAsync() {
    var services = new ServiceCollection();
    services.AddSingleton<IStartupPipelineState>(await _stateWithFailedMigrateAsync());
    var terse = await services
      .AddGraphQL()
      .AddQueryType(d => d.Name("Query"))
      .AddWhizbangStartupStatus()
      .ExecuteRequestAsync(QUERY);

    var terseJson = terse.ToJson();
    await Assert.That(terseJson).Contains("FAILED")
      .Because("the outcome itself is framework-authored and always visible");
    await Assert.That(terseJson).DoesNotContain("tenant_secrets")
      .Because("reasons originate in exception messages and are a separate opt-in level — the "
             + "GraphQL surface must hold the same disclosure line as the others");

    var verboseServices = new ServiceCollection();
    verboseServices.AddSingleton<IStartupPipelineState>(await _stateWithFailedMigrateAsync());
    var verbose = await verboseServices
      .AddGraphQL()
      .AddQueryType(d => d.Name("Query"))
      .AddWhizbangStartupStatus(includeReasons: true)
      .ExecuteRequestAsync(QUERY);

    await Assert.That(verbose.ToJson()).Contains("tenant_secrets");
  }
}
