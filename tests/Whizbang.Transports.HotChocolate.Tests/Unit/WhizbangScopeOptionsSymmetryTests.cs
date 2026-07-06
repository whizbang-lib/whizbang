using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Whizbang.Core.Security;
using Whizbang.Transports.HotChocolate.Middleware;

namespace Whizbang.Transports.HotChocolate.Tests.Unit;

/// <summary>
/// Tests for the symmetric multi-claim-name support on <see cref="WhizbangScopeOptions"/>:
/// every claim category exposes both a singular and a plural property, and multi-valued
/// claims (permissions, groups) honor a configurable <see cref="ClaimAggregation"/>.
/// </summary>
/// <tests>WhizbangScopeOptions,WhizbangScopeMiddleware,ClaimAggregation</tests>
public class WhizbangScopeOptionsSymmetryTests {
  // ===== Singular ↔ plural shim symmetry =====

  [Test]
  public async Task TenantId_SingularGet_ReadsFirstPluralEntryAsync() {
    var opts = new WhizbangScopeOptions { TenantIdClaimTypes = ["custom_tenant", "tenant_id"] };
    var got = opts.TenantIdClaimType;
    await Assert.That(got).IsEqualTo("custom_tenant");
  }

  [Test]
  public async Task TenantId_SingularSet_ReplacesPluralListAsync() {
    var opts = new WhizbangScopeOptions { TenantIdClaimTypes = ["a", "b", "c"] };
    opts.TenantIdClaimType = "only";
    var list = opts.TenantIdClaimTypes;
    await Assert.That(list.Count).IsEqualTo(1);
    await Assert.That(list[0]).IsEqualTo("only");
  }

  [Test]
  public async Task Permissions_SingularSet_ReplacesPluralListAsync() {
    var opts = new WhizbangScopeOptions();
    opts.PermissionsClaimType = "perms";
    var list = opts.PermissionsClaimTypes;
    await Assert.That(list.Count).IsEqualTo(1);
    await Assert.That(list[0]).IsEqualTo("perms");
  }

  [Test]
  public async Task Groups_SingularSet_ReplacesPluralListAsync() {
    var opts = new WhizbangScopeOptions();
    opts.GroupsClaimType = "grp";
    var list = opts.GroupsClaimTypes;
    await Assert.That(list.Count).IsEqualTo(1);
    await Assert.That(list[0]).IsEqualTo("grp");
  }

  [Test]
  public async Task Organization_SingularSet_ReplacesPluralListAsync() {
    var opts = new WhizbangScopeOptions();
    opts.OrganizationIdClaimType = "org";
    var list = opts.OrganizationIdClaimTypes;
    await Assert.That(list.Count).IsEqualTo(1);
    await Assert.That(list[0]).IsEqualTo("org");
  }

  [Test]
  public async Task Customer_SingularSet_ReplacesPluralListAsync() {
    var opts = new WhizbangScopeOptions();
    opts.CustomerIdClaimType = "cust";
    var list = opts.CustomerIdClaimTypes;
    await Assert.That(list.Count).IsEqualTo(1);
    await Assert.That(list[0]).IsEqualTo("cust");
  }

  [Test]
  public async Task Organization_And_Customer_SingularGet_ReadFirstPluralEntryAsync() {
    var opts = new WhizbangScopeOptions {
      OrganizationIdClaimTypes = ["primary_org", "org_id"],
      CustomerIdClaimTypes = ["primary_customer", "customer_id"],
    };
    await Assert.That(opts.OrganizationIdClaimType).IsEqualTo("primary_org");
    await Assert.That(opts.CustomerIdClaimType).IsEqualTo("primary_customer");
  }

  [Test]
  public async Task SingularGetters_EmptiedPluralLists_FallBackToLegacyDefaultsAsync() {
    // Emptying every plural list exercises the null-coalescing arm of each singular
    // getter shim — the legacy default names must come back rather than null.
    var opts = new WhizbangScopeOptions {
      TenantIdClaimTypes = [],
      UserIdClaimTypes = [],
      OrganizationIdClaimTypes = [],
      CustomerIdClaimTypes = [],
      PermissionsClaimTypes = [],
      GroupsClaimTypes = [],
    };

    await Assert.That(opts.TenantIdClaimType).IsEqualTo("tenant_id");
    await Assert.That(opts.UserIdClaimType).IsEqualTo(ClaimTypes.NameIdentifier);
    await Assert.That(opts.OrganizationIdClaimType).IsEqualTo("org_id");
    await Assert.That(opts.CustomerIdClaimType).IsEqualTo("customer_id");
    await Assert.That(opts.PermissionsClaimType).IsEqualTo("permissions");
    await Assert.That(opts.GroupsClaimType).IsEqualTo("groups");
  }

  // ===== Defaults preserve legacy behavior =====

  [Test]
  public async Task Defaults_PluralListsContainSingleLegacyEntryAsync() {
    var opts = new WhizbangScopeOptions();
    await Assert.That(opts.TenantIdClaimTypes.Count).IsEqualTo(1);
    await Assert.That(opts.TenantIdClaimTypes[0]).IsEqualTo("tenant_id");
    await Assert.That(opts.PermissionsClaimTypes.Count).IsEqualTo(1);
    await Assert.That(opts.PermissionsClaimTypes[0]).IsEqualTo("permissions");
    await Assert.That(opts.GroupsClaimTypes.Count).IsEqualTo(1);
    await Assert.That(opts.GroupsClaimTypes[0]).IsEqualTo("groups");
    await Assert.That(opts.OrganizationIdClaimTypes.Count).IsEqualTo(1);
    await Assert.That(opts.OrganizationIdClaimTypes[0]).IsEqualTo("org_id");
    await Assert.That(opts.CustomerIdClaimTypes.Count).IsEqualTo(1);
    await Assert.That(opts.CustomerIdClaimTypes[0]).IsEqualTo("customer_id");
  }

  [Test]
  public async Task Defaults_PermissionsAggregation_IsFirstMatchAsync() {
    var opts = new WhizbangScopeOptions();
    var mode = opts.PermissionsAggregation;
    await Assert.That(mode).IsEqualTo(ClaimAggregation.FirstMatch);
  }

  [Test]
  public async Task Defaults_GroupsAggregation_IsFirstMatchAsync() {
    var opts = new WhizbangScopeOptions();
    var mode = opts.GroupsAggregation;
    await Assert.That(mode).IsEqualTo(ClaimAggregation.FirstMatch);
  }

  // ===== Middleware honors plural lists for single-valued claims =====

  [Test]
  public async Task Middleware_TenantId_FallsBackThroughPluralListAsync() {
    var opts = new WhizbangScopeOptions {
      TenantIdClaimTypes = ["primary_tenant", "tenant_id"]
    };
    var accessor = new TestScopeContextAccessor();
    var middleware = new WhizbangScopeMiddleware(_ => Task.CompletedTask, opts);
    var context = _createContextWithClaims(("tenant_id", "T-9"));   // primary missing → falls back

    await middleware.InvokeAsync(context, accessor);

    var ctx = accessor.Current as ImmutableScopeContext;
    await Assert.That(ctx).IsNotNull();
    await Assert.That(ctx!.Scope.TenantId).IsEqualTo("T-9");
  }

  [Test]
  public async Task Middleware_TenantId_PrimaryWinsOverFallbackAsync() {
    var opts = new WhizbangScopeOptions {
      TenantIdClaimTypes = ["primary_tenant", "tenant_id"]
    };
    var accessor = new TestScopeContextAccessor();
    var middleware = new WhizbangScopeMiddleware(_ => Task.CompletedTask, opts);
    var context = _createContextWithClaims(
      ("primary_tenant", "T-PRIMARY"),
      ("tenant_id", "T-FALLBACK"));

    await middleware.InvokeAsync(context, accessor);

    var ctx = accessor.Current as ImmutableScopeContext;
    await Assert.That(ctx!.Scope.TenantId).IsEqualTo("T-PRIMARY");
  }

  // ===== Permissions aggregation =====

  [Test]
  public async Task Permissions_FirstMatch_OnlyReadsFirstClaimWithValuesAsync() {
    var opts = new WhizbangScopeOptions {
      PermissionsClaimTypes = ["perms", "permissions"],
      PermissionsAggregation = ClaimAggregation.FirstMatch
    };
    var accessor = new TestScopeContextAccessor();
    var middleware = new WhizbangScopeMiddleware(_ => Task.CompletedTask, opts);
    var context = _createContextWithClaims(
      ("perms", "p1"),
      ("perms", "p2"),
      ("permissions", "p3"));

    await middleware.InvokeAsync(context, accessor);

    var ctx = accessor.Current as ImmutableScopeContext;
    var perms = ctx!.Permissions.Select(p => p.Value).ToHashSet();
    await Assert.That(perms.Count).IsEqualTo(2);
    await Assert.That(perms.Contains("p1")).IsTrue();
    await Assert.That(perms.Contains("p2")).IsTrue();
    await Assert.That(perms.Contains("p3")).IsFalse();
  }

  [Test]
  public async Task Permissions_FirstMatch_FallsThroughEmptyClaimAsync() {
    var opts = new WhizbangScopeOptions {
      PermissionsClaimTypes = ["perms", "permissions"],
      PermissionsAggregation = ClaimAggregation.FirstMatch
    };
    var accessor = new TestScopeContextAccessor();
    var middleware = new WhizbangScopeMiddleware(_ => Task.CompletedTask, opts);
    var context = _createContextWithClaims(("permissions", "p3"));   // no "perms"

    await middleware.InvokeAsync(context, accessor);

    var ctx = accessor.Current as ImmutableScopeContext;
    var perms = ctx!.Permissions.Select(p => p.Value).ToHashSet();
    await Assert.That(perms.Count).IsEqualTo(1);
    await Assert.That(perms.Contains("p3")).IsTrue();
  }

  [Test]
  public async Task Permissions_Aggregate_UnionsAcrossAllClaimsAsync() {
    var opts = new WhizbangScopeOptions {
      PermissionsClaimTypes = ["perms", "permissions"],
      PermissionsAggregation = ClaimAggregation.Aggregate
    };
    var accessor = new TestScopeContextAccessor();
    var middleware = new WhizbangScopeMiddleware(_ => Task.CompletedTask, opts);
    var context = _createContextWithClaims(
      ("perms", "p1"),
      ("permissions", "p2"),
      ("permissions", "p3"));

    await middleware.InvokeAsync(context, accessor);

    var ctx = accessor.Current as ImmutableScopeContext;
    var perms = ctx!.Permissions.Select(p => p.Value).ToHashSet();
    await Assert.That(perms.Count).IsEqualTo(3);
    await Assert.That(perms.Contains("p1")).IsTrue();
    await Assert.That(perms.Contains("p2")).IsTrue();
    await Assert.That(perms.Contains("p3")).IsTrue();
  }

  [Test]
  public async Task Permissions_Aggregate_DeduplicatesOverlappingValuesAsync() {
    var opts = new WhizbangScopeOptions {
      PermissionsClaimTypes = ["perms", "permissions"],
      PermissionsAggregation = ClaimAggregation.Aggregate
    };
    var accessor = new TestScopeContextAccessor();
    var middleware = new WhizbangScopeMiddleware(_ => Task.CompletedTask, opts);
    var context = _createContextWithClaims(
      ("perms", "shared"),
      ("permissions", "shared"));

    await middleware.InvokeAsync(context, accessor);

    var ctx = accessor.Current as ImmutableScopeContext;
    var perms = ctx!.Permissions.Select(p => p.Value).ToList();
    await Assert.That(perms.Count).IsEqualTo(1);
    await Assert.That(perms[0]).IsEqualTo("shared");
  }

  // ===== Groups aggregation =====

  [Test]
  public async Task Groups_Aggregate_UnionsAcrossClaimsAsync() {
    var opts = new WhizbangScopeOptions {
      GroupsClaimTypes = ["groups", "roles"],
      GroupsAggregation = ClaimAggregation.Aggregate
    };
    var accessor = new TestScopeContextAccessor();
    var middleware = new WhizbangScopeMiddleware(_ => Task.CompletedTask, opts);
    var context = _createContextWithClaims(
      ("groups", "g1"),
      ("roles", "g2"));

    await middleware.InvokeAsync(context, accessor);

    var ctx = accessor.Current as ImmutableScopeContext;
    var groupValues = ctx!.SecurityPrincipals
      .Where(p => p.IsGroup)
      .Select(p => p.Value)
      .ToHashSet();
    await Assert.That(groupValues.Contains("group:g1")).IsTrue();
    await Assert.That(groupValues.Contains("group:g2")).IsTrue();
  }

  // ===== helpers =====

  private static DefaultHttpContext _createContextWithClaims(params (string type, string value)[] claims) {
    var claimsList = claims.Select(c => new Claim(c.type, c.value)).ToList();
    var identity = new ClaimsIdentity(claimsList, "TestAuth");
    return new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
  }
}
