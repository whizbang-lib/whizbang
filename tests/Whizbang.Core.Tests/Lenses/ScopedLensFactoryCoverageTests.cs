using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.SystemEvents;

namespace Whizbang.Core.Tests.Lenses;

/// <summary>
/// Coverage-round-23 targets for <see cref="ScopedLensFactory"/>'s named-scope-to-flags conversion
/// (<c>_convertScopeDefinitionToFilter</c>, reached only through the string-scope-name overload of
/// <c>GetLens</c>): the three <c>FilterPropertyName</c> mappings not already exercised by another
/// property name, and the unrecognized-<c>FilterInterfaceType</c> fallback. A lens's scope filter
/// decides which rows a query can see — mapping "CustomerId" to the wrong flag (or to None) either
/// hides rows a caller should see or, worse, removes a filter that was scoping the query to one
/// customer's data, leaking rows across tenants/customers/organizations.
/// </summary>
[Category("Core")]
[Category("Lenses")]
public class ScopedLensFactoryCoverageTests {
  [Test]
  public async Task GetLens_ByName_FilterPropertyNameUserId_MapsToUserFilterAsync() {
    var context = _createScopeContext(userId: "user-456");
    var (factory, accessor) = _createFactory(options =>
      options.DefineScope("UserProp", scope => scope.FilterPropertyName = "UserId"));
    accessor.Current = context;

    var lens = factory.GetLens<ITestLensQuery>("UserProp");

    await Assert.That(lens.AppliedFilter!.Value.Filters).IsEqualTo(ScopeFilters.User)
      .Because("the raw property-name mapping for \"UserId\" must resolve to exactly ScopeFilters.User");
    await Assert.That(lens.AppliedFilter!.Value.UserId).IsEqualTo("user-456");
  }

  [Test]
  public async Task GetLens_ByName_FilterPropertyNameOrganizationId_MapsToOrganizationFilterAsync() {
    var context = _createScopeContext(organizationId: "org-456");
    var (factory, accessor) = _createFactory(options =>
      options.DefineScope("OrgProp", scope => scope.FilterPropertyName = "OrganizationId"));
    accessor.Current = context;

    var lens = factory.GetLens<ITestLensQuery>("OrgProp");

    await Assert.That(lens.AppliedFilter!.Value.Filters).IsEqualTo(ScopeFilters.Organization)
      .Because("the raw property-name mapping for \"OrganizationId\" must resolve to exactly ScopeFilters.Organization");
    await Assert.That(lens.AppliedFilter!.Value.OrganizationId).IsEqualTo("org-456");
  }

  [Test]
  public async Task GetLens_ByName_FilterPropertyNameCustomerId_MapsToCustomerFilterAsync() {
    var context = _createScopeContext(customerId: "cust-789");
    var (factory, accessor) = _createFactory(options =>
      options.DefineScope("CustomerProp", scope => scope.FilterPropertyName = "CustomerId"));
    accessor.Current = context;

    var lens = factory.GetLens<ITestLensQuery>("CustomerProp");

    await Assert.That(lens.AppliedFilter!.Value.Filters).IsEqualTo(ScopeFilters.Customer)
      .Because("the raw property-name mapping for \"CustomerId\" must resolve to exactly ScopeFilters.Customer "
        + "— mapping it to None would silently remove the only filter isolating one customer's rows");
    await Assert.That(lens.AppliedFilter!.Value.CustomerId).IsEqualTo("cust-789");
  }

  [Test]
  public async Task GetLens_ByName_UnrecognizedFilterInterfaceType_MapsToNoFilterAsync() {
    var (factory, _) = _createFactory(options =>
      options.DefineScope("UnknownInterface", scope => scope.FilterInterfaceType = typeof(INotARecognizedScopeMarker)));

    // None requires no ambient scope context to be set, so reaching a non-null lens here already
    // proves the fast (filters == None) path was taken, not an accidental match of a real filter.
    var lens = factory.GetLens<ITestLensQuery>("UnknownInterface");

    await Assert.That(lens).IsNotNull();
    await Assert.That(lens.AppliedFilter!.Value.Filters).IsEqualTo(ScopeFilters.None)
      .Because("an interface type whose Name matches none of the four recognized scope markers must fall "
        + "back to ScopeFilters.None (the same as an explicit NoFilter scope) rather than throwing or "
        + "silently reusing whatever flags an earlier scope happened to leave set");
  }

  private interface INotARecognizedScopeMarker;

  // === Helper Methods (mirrors ScopedLensFactoryImplTests.cs's fixture, kept file-local) ===

  private static (ScopedLensFactory factory, ScopeContextAccessor accessor) _createFactory(
      Action<LensOptions>? configureOptions = null) {
    var services = new ServiceCollection();
    var accessor = new ScopeContextAccessor();

    services.AddSingleton<IScopeContextAccessor>(accessor);
    services.AddSingleton<ISystemEventEmitter, _nullSystemEventEmitter>();
    services.AddScoped<ITestLensQuery, _testLensQuery>();

    var lensOptions = new LensOptions();
    configureOptions?.Invoke(lensOptions);
    services.AddSingleton(lensOptions);

    var provider = services.BuildServiceProvider();
    var factory = new ScopedLensFactory(
      provider,
      accessor,
      lensOptions,
      provider.GetRequiredService<ISystemEventEmitter>());

    return (factory, accessor);
  }

  private static ScopeContext _createScopeContext(
      string? tenantId = null,
      string? userId = null,
      string? organizationId = null,
      string? customerId = null) {
    return new ScopeContext {
      Scope = new PerspectiveScope {
        TenantId = tenantId,
        UserId = userId,
        OrganizationId = organizationId,
        CustomerId = customerId
      },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>()
    };
  }

  public interface ITestLensQuery : ILensQuery, IFilterableLens {
    ScopeFilterInfo? AppliedFilter { get; }
  }

  private sealed class _testLensQuery : ITestLensQuery {
    public ScopeFilterInfo? AppliedFilter { get; private set; }

    public void ApplyFilter(ScopeFilterInfo filterInfo) {
      AppliedFilter = filterInfo;
    }
  }

  private sealed class _nullSystemEventEmitter : ISystemEventEmitter {
    public Task EmitEventAuditedAsync<TEvent>(
        Guid streamId,
        long streamPosition,
        MessageEnvelope<TEvent> envelope,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task EmitCommandAuditedAsync<TCommand, TResponse>(
        TCommand command,
        TResponse response,
        string receptorName,
        IMessageContext? context,
        CancellationToken cancellationToken = default) where TCommand : notnull => Task.CompletedTask;

    public Task EmitAsync<TSystemEvent>(
        TSystemEvent systemEvent,
        CancellationToken cancellationToken = default) where TSystemEvent : ISystemEvent => Task.CompletedTask;

    public bool ShouldExcludeFromAudit(Type type) => false;
  }
}
