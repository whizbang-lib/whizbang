// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using HotChocolate.Execution.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Transports.HotChocolate.Middleware;

namespace Whizbang.Transports.HotChocolate.Tests.Unit;

/// <summary>
/// #1014: the GraphQL scope mappings bind from <c>Whizbang:Scope</c> and the startup-status reasons
/// opt-in from <c>Whizbang:StartupStatusGraph</c>, so both can change at deploy time.
/// </summary>
/// <code-under-test>src/Whizbang.Transports.HotChocolate/Middleware/ScopeMiddlewareExtensions.cs</code-under-test>
/// <code-under-test>src/Whizbang.Transports.HotChocolate/Extensions/HotChocolateStartupStatusExtensions.cs</code-under-test>
/// <docs>operations/configuration/configuration-reference</docs>
public class HotChocolateOptionsConfigurationBindingTests {

  private static ServiceCollection _services(Dictionary<string, string?> settings) {
    var services = new ServiceCollection();
    services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
    return services;
  }

  [Test]
  public async Task Scope_EveryKeyBindsAsync() {
    var services = _services(new() {
      ["Whizbang:Scope:TenantIdClaimType"] = "tid",
      ["Whizbang:Scope:TenantIdHeaderName"] = "X-T",
      ["Whizbang:Scope:UserIdClaimType"] = "uid",
      ["Whizbang:Scope:UserIdHeaderName"] = "X-U",
      ["Whizbang:Scope:OrganizationIdClaimType"] = "oid2",
      ["Whizbang:Scope:OrganizationIdHeaderName"] = "X-O",
      ["Whizbang:Scope:CustomerIdClaimType"] = "cid",
      ["Whizbang:Scope:CustomerIdHeaderName"] = "X-C",
      ["Whizbang:Scope:CorrelationIdHeaderName"] = "X-Corr",
      ["Whizbang:Scope:RolesClaimType"] = "role",
      ["Whizbang:Scope:PermissionsClaimType"] = "perm",
      ["Whizbang:Scope:PermissionsAggregation"] = "Aggregate",
      ["Whizbang:Scope:GroupsClaimType"] = "grp",
      ["Whizbang:Scope:GroupsAggregation"] = "Aggregate",
      ["Whizbang:Scope:ExtensionClaimMappings:region"] = "region_claim",
      ["Whizbang:Scope:ExtensionHeaderMappings:region"] = "X-Region",
      ["Whizbang:Scope:PropagatedClaimTypes:0"] = "department",
    });
    services.AddWhizbangScope();
    await using var provider = services.BuildServiceProvider();

    var options = provider.GetRequiredService<WhizbangScopeOptions>();

    await Assert.That(options.TenantIdClaimTypes).IsEquivalentTo(["tid"]);
    await Assert.That(options.TenantIdHeaderName).IsEqualTo("X-T");
    await Assert.That(options.UserIdClaimTypes).IsEquivalentTo(["uid"]);
    await Assert.That(options.UserIdHeaderName).IsEqualTo("X-U");
    await Assert.That(options.OrganizationIdClaimTypes).IsEquivalentTo(["oid2"]);
    await Assert.That(options.OrganizationIdHeaderName).IsEqualTo("X-O");
    await Assert.That(options.CustomerIdClaimTypes).IsEquivalentTo(["cid"]);
    await Assert.That(options.CustomerIdHeaderName).IsEqualTo("X-C");
    await Assert.That(options.CorrelationIdHeaderName).IsEqualTo("X-Corr");
    await Assert.That(options.RolesClaimType).IsEqualTo("role");
    await Assert.That(options.PermissionsClaimTypes).IsEquivalentTo(["perm"]);
    await Assert.That(options.PermissionsAggregation).IsEqualTo(ClaimAggregation.Aggregate);
    await Assert.That(options.GroupsClaimTypes).IsEquivalentTo(["grp"]);
    await Assert.That(options.GroupsAggregation).IsEqualTo(ClaimAggregation.Aggregate);
    await Assert.That(options.ExtensionClaimMappings["region"]).IsEqualTo("region_claim");
    await Assert.That(options.ExtensionHeaderMappings["region"]).IsEqualTo("X-Region");
    await Assert.That(options.PropagatedClaimTypes).IsEquivalentTo(["department"]);
  }

  /// <summary>
  /// A key present with an empty value (an appsettings placeholder, or an environment variable set to nothing) is no
  /// setting. It used to replace a claim-type list with one empty name that never matches, so a request lost its
  /// tenant, user, roles or permissions with nothing logged; now every blank key leaves the default, or the code value,
  /// in place.
  /// </summary>
  [Test]
  [Arguments("")]
  [Arguments("   ")]
  public async Task Scope_BlankKeys_LeaveTheDefaultsAndTheCodeValuesAsync(string blank) {
    string[] keys = [
      "TenantIdClaimType", "TenantIdHeaderName", "UserIdClaimType", "UserIdHeaderName", "OrganizationIdClaimType",
      "CustomerIdClaimType", "CorrelationIdHeaderName", "RolesClaimType", "PermissionsClaimType",
      "PermissionsAggregation", "GroupsClaimType", "GroupsAggregation", "PermissionsClaimTypes:0",
      "ExtensionClaimMappings:region", "ExtensionHeaderMappings:region", "PropagatedClaimTypes:0",
    ];
    var services = _services(keys.ToDictionary(key => "Whizbang:Scope:" + key, _ => (string?)blank));
    services.AddWhizbangScope(code => code.TenantIdHeaderName = "X-Tenant");
    await using var provider = services.BuildServiceProvider();
    var defaults = new WhizbangScopeOptions();

    var options = provider.GetRequiredService<WhizbangScopeOptions>();

    await Assert.That(options.TenantIdClaimTypes).IsEquivalentTo(defaults.TenantIdClaimTypes);
    await Assert.That(options.TenantIdHeaderName).IsEqualTo("X-Tenant");
    await Assert.That(options.UserIdClaimTypes).IsEquivalentTo(defaults.UserIdClaimTypes);
    await Assert.That(options.UserIdHeaderName).IsNull();
    await Assert.That(options.OrganizationIdClaimTypes).IsEquivalentTo(defaults.OrganizationIdClaimTypes);
    await Assert.That(options.CustomerIdClaimTypes).IsEquivalentTo(defaults.CustomerIdClaimTypes);
    await Assert.That(options.CorrelationIdHeaderName).IsEqualTo(defaults.CorrelationIdHeaderName);
    await Assert.That(options.RolesClaimType).IsEqualTo(defaults.RolesClaimType);
    await Assert.That(options.PermissionsClaimTypes).IsEquivalentTo(defaults.PermissionsClaimTypes);
    await Assert.That(options.PermissionsAggregation).IsEqualTo(defaults.PermissionsAggregation);
    await Assert.That(options.GroupsClaimTypes).IsEquivalentTo(defaults.GroupsClaimTypes);
    await Assert.That(options.GroupsAggregation).IsEqualTo(defaults.GroupsAggregation);
    await Assert.That(options.ExtensionClaimMappings).IsEmpty();
    await Assert.That(options.ExtensionHeaderMappings).IsEmpty();
    await Assert.That(options.PropagatedClaimTypes).IsEmpty();
  }

  [Test]
  public async Task Scope_ClaimTypeLists_IndexedKeysAddToTheDefaultsAsync() {
    // The plural lists carry a default entry, and the binder adds indexed keys to it rather than
    // replacing it. The singular key is the replacement. Pinned because an operator reading only the
    // key would expect the list to be replaced.
    var services = _services(new() {
      ["Whizbang:Scope:TenantIdClaimTypes:0"] = "tid",
      ["Whizbang:Scope:GroupsClaimTypes:0"] = "roles_group",
    });
    services.AddWhizbangScope();
    await using var provider = services.BuildServiceProvider();

    var options = provider.GetRequiredService<WhizbangScopeOptions>();

    await Assert.That(options.TenantIdClaimTypes).IsEquivalentTo(["tenant_id", "tid"]);
    await Assert.That(options.GroupsClaimTypes).IsEquivalentTo(["groups", "roles_group"]);
  }

  [Test]
  public async Task Scope_ConfigurationOverridesCode_AndCodeSurvivesAbsentKeysAsync() {
    var services = _services(new() {
      ["Whizbang:Scope:TenantIdHeaderName"] = "X-From-Config",
    });
    services.AddWhizbangScope(o => {
      o.TenantIdHeaderName = "X-From-Code";
      o.UserIdHeaderName = "X-User-From-Code";
    });
    await using var provider = services.BuildServiceProvider();

    var options = provider.GetRequiredService<WhizbangScopeOptions>();

    await Assert.That(options.TenantIdHeaderName).IsEqualTo("X-From-Config");
    await Assert.That(options.UserIdHeaderName).IsEqualTo("X-User-From-Code");
  }

  [Test]
  public async Task Scope_NoConfigurationRegistered_ResolvesDefaultsAsync() {
    var services = new ServiceCollection();
    services.AddWhizbangScope();
    await using var provider = services.BuildServiceProvider();

    var options = provider.GetRequiredService<WhizbangScopeOptions>();
    await Assert.That(options.TenantIdClaimType).IsEqualTo("tenant_id");
    await Assert.That(options.TenantIdHeaderName).IsNull();
  }

  private static WhizbangStartupStatusGraphOptions _startupStatus(Dictionary<string, string?> settings, bool includeReasons) {
    var services = _services(settings);
    services.AddGraphQL().AddWhizbangStartupStatus(includeReasons);
    return services.BuildServiceProvider().GetRequiredService<WhizbangStartupStatusGraphOptions>();
  }

  [Test]
  public async Task StartupStatusGraph_IncludeReasonsBindsAsync() {
    var options = _startupStatus(new() { ["Whizbang:StartupStatusGraph:IncludeReasons"] = "true" }, includeReasons: false);

    await Assert.That(options.IncludeReasons).IsTrue();
  }

  [Test]
  public async Task StartupStatusGraph_CodeValueSurvivesAnAbsentOrUnreadableKeyAsync() {
    await Assert.That(_startupStatus([], includeReasons: true).IncludeReasons).IsTrue();
    await Assert.That(_startupStatus(new() { ["Whizbang:StartupStatusGraph:IncludeReasons"] = "maybe" }, includeReasons: true).IncludeReasons)
      .IsTrue();
  }

  [Test]
  public async Task StartupStatusGraph_NoConfigurationRegistered_KeepsTheCodeValueAsync() {
    var services = new ServiceCollection();
    services.AddGraphQL().AddWhizbangStartupStatus(includeReasons: true);

    await Assert.That(services.BuildServiceProvider().GetRequiredService<WhizbangStartupStatusGraphOptions>().IncludeReasons).IsTrue();
  }
}
