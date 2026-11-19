// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.Security.Extractors;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Security;

/// <summary>
/// The scope a message carries on its hop (#1854). A request's scope extensions travel with it, and a claim
/// travels only when the application listed its type, so a receiving service sees the facts the originating
/// request mapped into its scope and never a token or a personal claim it was not given on purpose.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Security/HopScope.cs</code-under-test>
[Category("Security")]
public class HopScopeTests {
  private const string TENANT = "tenant-hop";
  private const string USER = "user-hop";
  private const string ACTING_ADMIN_KEY = "acting-admin";
  private const string LISTED_CLAIM = "department";
  private const string UNLISTED_CLAIM = "email";

  /// <summary>A scope context the way the HTTP edge establishes it: an extension, two claims, one listed.</summary>
  internal static ImmutableScopeContext EdgeContext(
      string? tenant = TENANT,
      string? user = USER,
      bool shouldPropagate = true,
      IReadOnlySet<string>? propagatedClaimTypes = null) {
    var scope = new PerspectiveScope {
      TenantId = tenant,
      UserId = user,
      CustomerId = "customer-hop",
      OrganizationId = "org-hop",
      Extensions = [new ScopeExtension(ACTING_ADMIN_KEY, "true")],
    };
    return new ImmutableScopeContext(new SecurityExtraction {
      Scope = scope,
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string> {
        [LISTED_CLAIM] = "finance",
        [UNLISTED_CLAIM] = "someone@example.test",
      },
      Source = "HttpContext",
      PropagatedClaimTypes = propagatedClaimTypes ?? new HashSet<string> { LISTED_CLAIM },
    }, shouldPropagate);
  }

  private static ScopeContext _restore(ScopeDelta? delta) => delta!.ApplyTo(null);

  // === FromAmbient ===

  [Test]
  public async Task FromAmbient_CarriesTheScopeExtensionsAsync() {
    ScopeContextAccessor.CurrentContext = EdgeContext();

    var restored = _restore(HopScope.FromAmbient());

    await Assert.That(restored.Scope.Extensions.Select(e => (e.Key, e.Value)))
      .IsEquivalentTo([(ACTING_ADMIN_KEY, (string?)"true")])
      .Because("an extension the request mapped into its scope is a fact about the caller the receiving service needs");
  }

  [Test]
  public async Task FromAmbient_CarriesTheWholeScopeNotJustTenantAndUserAsync() {
    ScopeContextAccessor.CurrentContext = EdgeContext();

    var restored = _restore(HopScope.FromAmbient());

    await Assert.That(restored.Scope.TenantId).IsEqualTo(TENANT);
    await Assert.That(restored.Scope.UserId).IsEqualTo(USER);
    await Assert.That(restored.Scope.CustomerId).IsEqualTo("customer-hop");
    await Assert.That(restored.Scope.OrganizationId).IsEqualTo("org-hop");
  }

  [Test]
  public async Task FromAmbient_AListedClaimCrossesAndAnUnlistedOneDoesNotAsync() {
    ScopeContextAccessor.CurrentContext = EdgeContext();

    var restored = _restore(HopScope.FromAmbient());

    await Assert.That(restored.Claims.ContainsKey(LISTED_CLAIM)).IsTrue()
      .Because("the application listed this claim type, so a downstream service needs it");
    await Assert.That(restored.Claims[LISTED_CLAIM]).IsEqualTo("finance");
    await Assert.That(restored.Claims.ContainsKey(UNLISTED_CLAIM)).IsFalse()
      .Because("a claim nobody listed is never copied into message metadata");
  }

  [Test]
  public async Task FromAmbient_SeveralListedClaims_AllCrossAsync() {
    ScopeContextAccessor.CurrentContext = EdgeContext(propagatedClaimTypes: new HashSet<string> { LISTED_CLAIM, UNLISTED_CLAIM });

    var restored = _restore(HopScope.FromAmbient());

    await Assert.That(restored.Claims.Keys).IsEquivalentTo([LISTED_CLAIM, UNLISTED_CLAIM]);
  }

  [Test]
  public async Task FromAmbient_ListedTypeTheRequestDoesNotHave_CarriesNoClaimsAsync() {
    ScopeContextAccessor.CurrentContext = EdgeContext(propagatedClaimTypes: new HashSet<string> { "absent" });

    var delta = HopScope.FromAmbient();

    await Assert.That(delta!.Collections).IsNull();
  }

  [Test]
  public async Task FromAmbient_NoClaimListed_CarriesNoClaimsAsync() {
    ScopeContextAccessor.CurrentContext = EdgeContext(propagatedClaimTypes: new HashSet<string>());

    var delta = HopScope.FromAmbient();

    await Assert.That(delta).IsNotNull();
    await Assert.That(delta!.Collections).IsNull()
      .Because("with an empty allow-list (the default) the hop carries the scope and no claim at all");
  }

  [Test]
  public async Task FromAmbient_NonPropagatingContext_ReturnsNullAsync() {
    ScopeContextAccessor.CurrentContext = EdgeContext(shouldPropagate: false);

    await Assert.That(HopScope.FromAmbient()).IsNull()
      .Because("a context that opted out of propagation stays out of outgoing messages, extensions included");
  }

  [Test]
  public async Task FromAmbient_NoAmbientContext_ReturnsNullAsync() {
    ScopeContextAccessor.CurrentContext = null;
    ScopeContextAccessor.CurrentInitiatingContext = null;

    await Assert.That(HopScope.FromAmbient()).IsNull();
  }

  [Test]
  public async Task FromAmbient_UserOnlyScope_CarriesItAsync() {
    ScopeContextAccessor.CurrentContext = EdgeContext(tenant: null);

    var restored = _restore(HopScope.FromAmbient());

    await Assert.That(restored.Scope.UserId).IsEqualTo(USER);
    await Assert.That(restored.Scope.Extensions.Select(e => e.Key)).IsEquivalentTo([ACTING_ADMIN_KEY]);
  }

  [Test]
  public async Task FromAmbient_ScopeWithNoTenantOrUser_ReturnsNullAsync() {
    ScopeContextAccessor.CurrentContext = EdgeContext(tenant: null, user: null);

    await Assert.That(HopScope.FromAmbient()).IsNull()
      .Because("a scope with no authority stays indistinguishable from none, so the unscoped markers still apply");
  }

  // === FromMessageContext ===

  [Test]
  public async Task FromMessageContext_OwnScopeContextOfTheSameIdentity_CarriesExtensionsAndListedClaimsAsync() {
    ScopeContextAccessor.CurrentContext = null;
    ScopeContextAccessor.CurrentInitiatingContext = null;
    var context = new MessageContext { TenantId = TENANT, UserId = USER, ScopeContext = EdgeContext() };

    var restored = _restore(HopScope.FromMessageContext(context));

    await Assert.That(restored.Scope.Extensions.Select(e => e.Key)).IsEquivalentTo([ACTING_ADMIN_KEY]);
    await Assert.That(restored.Claims.Keys).IsEquivalentTo([LISTED_CLAIM]);
  }

  [Test]
  public async Task FromMessageContext_NoScopeContext_TakesExtensionsFromTheAmbientScopeOfTheSameIdentityAsync() {
    // MessageContext.Create(cascade) carries the ids only; the scope they came from is the ambient one.
    ScopeContextAccessor.CurrentContext = EdgeContext();
    var context = new MessageContext { TenantId = TENANT, UserId = USER };

    var restored = _restore(HopScope.FromMessageContext(context));

    await Assert.That(restored.Scope.Extensions.Select(e => e.Key)).IsEquivalentTo([ACTING_ADMIN_KEY]);
    await Assert.That(restored.Claims.Keys).IsEquivalentTo([LISTED_CLAIM]);
  }

  [Test]
  public async Task FromMessageContext_TenantOnlyContextAndTenantOnlyScope_CarriesExtensionsAsync() {
    ScopeContextAccessor.CurrentContext = EdgeContext(user: null);
    var context = new MessageContext { TenantId = TENANT };

    var restored = _restore(HopScope.FromMessageContext(context));

    await Assert.That(restored.Scope.UserId).IsNull();
    await Assert.That(restored.Scope.Extensions.Select(e => e.Key)).IsEquivalentTo([ACTING_ADMIN_KEY]);
  }

  [Test]
  public async Task FromMessageContext_AnotherIdentity_CarriesItsIdsWithoutTheAmbientExtensionsAsync() {
    ScopeContextAccessor.CurrentContext = EdgeContext();
    var context = new MessageContext { TenantId = "tenant-other", UserId = USER };

    var restored = _restore(HopScope.FromMessageContext(context));

    await Assert.That(restored.Scope.TenantId).IsEqualTo("tenant-other");
    await Assert.That(restored.Scope.UserId).IsEqualTo(USER);
    await Assert.That(restored.Scope.Extensions).IsEmpty()
      .Because("extensions and claims travel only with the identity they were established for");
    await Assert.That(restored.Claims).IsEmpty();
  }

  [Test]
  public async Task FromMessageContext_UserOnlyContextAgainstATenantScope_CarriesItsIdsOnlyAsync() {
    ScopeContextAccessor.CurrentContext = EdgeContext(tenant: TENANT, user: null);
    var context = new MessageContext { UserId = USER };

    var restored = _restore(HopScope.FromMessageContext(context));

    await Assert.That(restored.Scope.TenantId).IsNull();
    await Assert.That(restored.Scope.UserId).IsEqualTo(USER);
    await Assert.That(restored.Scope.Extensions).IsEmpty();
  }

  [Test]
  public async Task FromMessageContext_UserOnlyContextAndUserOnlyScope_CarriesExtensionsAsync() {
    ScopeContextAccessor.CurrentContext = EdgeContext(tenant: null);
    var context = new MessageContext { UserId = USER };

    var restored = _restore(HopScope.FromMessageContext(context));

    await Assert.That(restored.Scope.TenantId).IsNull();
    await Assert.That(restored.Scope.Extensions.Select(e => e.Key)).IsEquivalentTo([ACTING_ADMIN_KEY]);
  }

  [Test]
  public async Task FromMessageContext_NoScopeContextAndNoAmbient_CarriesItsIdsAsync() {
    ScopeContextAccessor.CurrentContext = null;
    ScopeContextAccessor.CurrentInitiatingContext = null;
    var context = new MessageContext { TenantId = TENANT, UserId = USER };

    var restored = _restore(HopScope.FromMessageContext(context));

    await Assert.That(restored.Scope.TenantId).IsEqualTo(TENANT);
    await Assert.That(restored.Scope.UserId).IsEqualTo(USER);
    await Assert.That(restored.Scope.Extensions).IsEmpty();
    await Assert.That(restored.Claims).IsEmpty();
  }

  [Test]
  public async Task FromMessageContext_NonPropagatingScopeContext_CarriesItsIdsOnlyAsync() {
    ScopeContextAccessor.CurrentContext = null;
    ScopeContextAccessor.CurrentInitiatingContext = null;
    var context = new MessageContext { TenantId = TENANT, UserId = USER, ScopeContext = EdgeContext(shouldPropagate: false) };

    var restored = _restore(HopScope.FromMessageContext(context));

    await Assert.That(restored.Scope.TenantId).IsEqualTo(TENANT);
    await Assert.That(restored.Scope.Extensions).IsEmpty()
      .Because("a scope context that opted out of propagation lends nothing beyond the ids the context names");
    await Assert.That(restored.Claims).IsEmpty();
  }

  [Test]
  public async Task FromMessageContext_PlainScopeContext_CarriesExtensionsButNoClaimAsync() {
    ScopeContextAccessor.CurrentContext = null;
    ScopeContextAccessor.CurrentInitiatingContext = null;
    var plain = new ScopeContext {
      Scope = EdgeContext().Scope,
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string> { [LISTED_CLAIM] = "finance" },
    };
    var context = new MessageContext { TenantId = TENANT, UserId = USER, ScopeContext = plain };

    var restored = _restore(HopScope.FromMessageContext(context));

    await Assert.That(restored.Scope.Extensions.Select(e => e.Key)).IsEquivalentTo([ACTING_ADMIN_KEY]);
    await Assert.That(restored.Claims).IsEmpty()
      .Because("only a context that names its propagated claim types can send a claim");
  }

  [Test]
  public async Task FromMessageContext_NoIds_ReturnsNullAsync() {
    ScopeContextAccessor.CurrentContext = EdgeContext();

    await Assert.That(HopScope.FromMessageContext(new MessageContext())).IsNull()
      .Because("a context with no tenant or user leaves the ambient scope to decide, as before");
  }

  [Test]
  public async Task FromMessageContext_Null_ThrowsAsync() {
    await Assert.That(() => HopScope.FromMessageContext(null!)).Throws<ArgumentNullException>();
  }

  // === FromEnvelope ===

  [Test]
  public async Task FromEnvelope_CarriesExtensionsAndEveryClaimOnItsHopsAsync() {
    ScopeContextAccessor.CurrentContext = EdgeContext();
    var envelope = _envelopeWithHopScope(HopScope.FromAmbient());
    ScopeContextAccessor.CurrentContext = null;

    var restored = _restore(HopScope.FromEnvelope(envelope));

    await Assert.That(restored.Scope.Extensions.Select(e => e.Key)).IsEquivalentTo([ACTING_ADMIN_KEY]);
    await Assert.That(restored.Claims.Keys).IsEquivalentTo([LISTED_CLAIM])
      .Because("a claim on a hop was listed where the chain started, so it keeps travelling");
  }

  [Test]
  public async Task FromEnvelope_NullOrScopeless_ReturnsNullAsync() {
    await Assert.That(HopScope.FromEnvelope(null)).IsNull();
    await Assert.That(HopScope.FromEnvelope(_envelopeWithHopScope(null))).IsNull();
  }

  // === The receive side lists what it restored ===

  [Test]
  public async Task PromoteToPropagating_HopScope_ListsEveryClaimItCarriesAsync() {
    ScopeContextAccessor.CurrentContext = EdgeContext();
    var restored = _restore(HopScope.FromAmbient());

    var promoted = ImmutableScopeContext.PromoteToPropagating(restored);

    await Assert.That(promoted.PropagatedClaimTypes).IsEquivalentTo([LISTED_CLAIM])
      .Because("a receiving service passes on the claims it was given, so the next hop carries them too");
  }

  [Test]
  public async Task PromoteToPropagating_ImmutableSource_KeepsItsListAsync() {
    var promoted = ImmutableScopeContext.PromoteToPropagating(EdgeContext());

    await Assert.That(promoted.PropagatedClaimTypes).IsEquivalentTo([LISTED_CLAIM])
      .Because("promoting a context the edge established must not widen what it lets leave the process");
  }

  [Test]
  public async Task MessageHopSecurityExtractor_ListsEveryClaimItRestoresAsync() {
    ScopeContextAccessor.CurrentContext = EdgeContext();
    var envelope = _envelopeWithHopScope(HopScope.FromAmbient());
    ScopeContextAccessor.CurrentContext = null;
    var extractor = new MessageHopSecurityExtractor(NullLogger<MessageHopSecurityExtractor>.Instance);

    var extraction = await extractor.ExtractAsync(envelope, new MessageSecurityOptions());

    await Assert.That(extraction!.Claims.Keys).IsEquivalentTo([LISTED_CLAIM]);
    await Assert.That(extraction.PropagatedClaimTypes).IsEquivalentTo([LISTED_CLAIM]);
    await Assert.That(extraction.Scope.Extensions.Select(e => e.Key)).IsEquivalentTo([ACTING_ADMIN_KEY]);
  }

  [Test]
  public async Task MessageHopSecurityExtractor_NoClaims_ListsNoneAsync() {
    ScopeContextAccessor.CurrentContext = EdgeContext(propagatedClaimTypes: new HashSet<string>());
    var envelope = _envelopeWithHopScope(HopScope.FromAmbient());
    ScopeContextAccessor.CurrentContext = null;
    var extractor = new MessageHopSecurityExtractor(NullLogger<MessageHopSecurityExtractor>.Instance);

    var extraction = await extractor.ExtractAsync(envelope, new MessageSecurityOptions());

    await Assert.That(extraction!.PropagatedClaimTypes).IsEmpty();
  }

  // === The wire ===

  [Test]
  public async Task HopScope_SurvivesTheWireAsync() {
    ScopeContextAccessor.CurrentContext = EdgeContext();
    var envelope = _envelopeWithHopScope(HopScope.FromAmbient());
    ScopeContextAccessor.CurrentContext = null;
    var options = JsonContextRegistry.CreateCombinedOptions();
    var typeInfo = options.GetTypeInfo(typeof(MessageEnvelope<JsonElement>));

    var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, typeInfo);
    var received = (MessageEnvelope<JsonElement>)JsonSerializer.Deserialize(bytes, typeInfo)!;
    var restored = received.GetCurrentScope()!;

    await Assert.That(restored.Scope.Extensions.Select(e => (e.Key, e.Value)))
      .IsEquivalentTo([(ACTING_ADMIN_KEY, (string?)"true")])
      .Because("the extension is serialized on the hop, not just carried in process");
    await Assert.That(restored.Claims.Keys).IsEquivalentTo([LISTED_CLAIM]);
    await Assert.That(restored.Claims[LISTED_CLAIM]).IsEqualTo("finance");
  }

  private static MessageEnvelope<JsonElement> _envelopeWithHopScope(ScopeDelta? scope) => new() {
    MessageId = new MessageId(TrackedGuid.New()),
    Payload = JsonSerializer.SerializeToElement(1),
    Hops = [new MessageHop {
      Type = HopType.Current,
      ServiceInstance = ServiceInstanceInfo.Unknown,
      Timestamp = DateTimeOffset.UnixEpoch,
      Scope = scope,
    }],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
  };
}
