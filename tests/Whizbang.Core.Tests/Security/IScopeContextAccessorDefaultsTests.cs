using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Security;

#pragma warning disable CA1707
#pragma warning disable IDE1006

/// <summary>
/// Covers the default interface methods on
/// <see cref="IScopeContextAccessor"/>: <c>UserId</c>, <c>TenantId</c>,
/// and <c>ScopeContext</c>. Concrete implementations
/// (<c>ScopeContextAccessor</c>) test the storage paths; this file pins the
/// default-impl property bodies via a minimal accessor that overrides ONLY
/// the two required get/set members, leaving the three derived properties
/// to fall through to their interface defaults.
/// </summary>
/// <docs>fundamentals/security/security#scope-context-accessor</docs>
public class IScopeContextAccessorDefaultsTests {

  [Test]
  public async Task UserId_WithInitiatingContext_ReturnsInitiatingUserIdAsync() {
    IScopeContextAccessor accessor = new _MinimalAccessor {
      InitiatingContext = new _StubMessageContext { UserId = "alice", TenantId = "acme" },
    };

    // Default UserId getter: InitiatingContext?.UserId.
    await Assert.That(accessor.UserId).IsEqualTo("alice");
  }

  [Test]
  public async Task UserId_WithoutInitiatingContext_ReturnsNullAsync() {
    IScopeContextAccessor accessor = new _MinimalAccessor();
    await Assert.That(accessor.UserId).IsNull();
  }

  [Test]
  public async Task TenantId_WithInitiatingContext_ReturnsInitiatingTenantIdAsync() {
    IScopeContextAccessor accessor = new _MinimalAccessor {
      InitiatingContext = new _StubMessageContext { UserId = "alice", TenantId = "acme" },
    };
    await Assert.That(accessor.TenantId).IsEqualTo("acme");
  }

  [Test]
  public async Task TenantId_WithoutInitiatingContext_ReturnsNullAsync() {
    IScopeContextAccessor accessor = new _MinimalAccessor();
    await Assert.That(accessor.TenantId).IsNull();
  }

  [Test]
  public async Task ScopeContext_ReturnsCurrent_ByDefaultAsync() {
    var currentScope = new _StubScopeContext();
    IScopeContextAccessor accessor = new _MinimalAccessor {
      Current = currentScope,
    };
    await Assert.That(accessor.ScopeContext).IsSameReferenceAs(currentScope);
  }

  [Test]
  public async Task ScopeContext_WithoutCurrent_ReturnsNullAsync() {
    IScopeContextAccessor accessor = new _MinimalAccessor();
    await Assert.That(accessor.ScopeContext).IsNull();
  }

  /// <summary>
  /// Minimal implementation that overrides only the two required members.
  /// UserId / TenantId / ScopeContext fall through to interface defaults —
  /// which is exactly what this test file is asserting.
  /// </summary>
  private sealed class _MinimalAccessor : IScopeContextAccessor {
    public IScopeContext? Current { get; set; }
    public IMessageContext? InitiatingContext { get; set; }
  }

  private sealed class _StubMessageContext : IMessageContext {
    public MessageId MessageId { get; init; } = MessageId.New();
    public CorrelationId CorrelationId { get; init; } = CorrelationId.New();
    public MessageId CausationId { get; init; } = MessageId.New();
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public string? UserId { get; init; }
    public string? TenantId { get; init; }
    public IReadOnlyDictionary<string, object> Metadata { get; init; } = new Dictionary<string, object>();
    public IScopeContext? ScopeContext { get; init; }
    public ICallerInfo? CallerInfo { get; init; }
  }

  private sealed class _StubScopeContext : IScopeContext {
    public PerspectiveScope Scope => new();
    public IReadOnlySet<string> Roles => new HashSet<string>();
    public IReadOnlySet<Permission> Permissions => new HashSet<Permission>();
    public IReadOnlySet<SecurityPrincipalId> SecurityPrincipals => new HashSet<SecurityPrincipalId>();
    public IReadOnlyDictionary<string, string> Claims => new Dictionary<string, string>();
    public string? ActualPrincipal => null;
    public string? EffectivePrincipal => null;
    public SecurityContextType ContextType => SecurityContextType.User;
    public bool HasPermission(Permission permission) => false;
    public bool HasAnyPermission(params Permission[] permissions) => false;
    public bool HasAllPermissions(params Permission[] permissions) => false;
    public bool HasRole(string roleName) => false;
    public bool HasAnyRole(params string[] roleNames) => false;
    public bool IsMemberOfAny(params SecurityPrincipalId[] principals) => false;
    public bool IsMemberOfAll(params SecurityPrincipalId[] principals) => false;
  }
}
