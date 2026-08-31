using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Hosting.AspNet.Tests;

/// <summary>
/// Covers the SchemaReadyHealthCheckExtensions registration helper, including the
/// default name and the "ready" tag fallback applied when the caller supplies none.
/// </summary>
public class SchemaReadyHealthCheckExtensionsTests {

  [Test]
  public async Task AddWhizbangSchemaReadyCheck_ReturnsSameBuilderForChainingAsync() {
    var services = new ServiceCollection();
    var builder = services.AddHealthChecks();

    var returned = builder.AddWhizbangSchemaReadyCheck();

    await Assert.That(returned).IsSameReferenceAs(builder);
  }

  [Test]
  public async Task AddWhizbangSchemaReadyCheck_WithoutTags_DefaultsToReadyTagAsync() {
    var services = new ServiceCollection();
    services.AddHealthChecks().AddWhizbangSchemaReadyCheck();

    using var provider = services.BuildServiceProvider();
    var registrations = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations;
    var registration = registrations.Single(r => r.Name == "schema");

    await Assert.That(registration.Tags).Contains("ready");
  }

  [Test]
  public async Task AddWhizbangSchemaReadyCheck_WithExplicitTags_UsesThemInsteadOfDefaultAsync() {
    var services = new ServiceCollection();
    services.AddHealthChecks().AddWhizbangSchemaReadyCheck("db-schema", "startup", "critical");

    using var provider = services.BuildServiceProvider();
    var registrations = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations;
    var registration = registrations.Single(r => r.Name == "db-schema");

    await Assert.That(registration.Tags).Contains("startup");
    await Assert.That(registration.Tags).Contains("critical");
    await Assert.That(registration.Tags).DoesNotContain("ready");
  }
}
