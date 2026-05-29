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
/// Fills in the branches of <see cref="ScopedMessageContext"/> that
/// <c>ScopedMessageContextTests</c> didn't already lock down:
/// CausationId fallback, ScopeContext priority chain, CallerInfo
/// pass-through, and the "both null" sentinel paths. These property
/// branches drive the second half of ScopedMessageContext to coverage.
/// </summary>
/// <docs>fundamentals/security/message-security#scoped-message-context</docs>
public class ScopedMessageContextExtraTests {

  [Test]
  public async Task CausationId_WithoutMessageContext_GeneratesNewIdAsync() {
    var scopeAccessor = new ScopeContextAccessor();
    var messageAccessor = new MessageContextAccessor();
    var ctx = new ScopedMessageContext(messageAccessor, scopeAccessor);

    var id1 = ctx.CausationId;
    var id2 = ctx.CausationId;

    // No source → fallback generates a fresh id on each read.
    await Assert.That(id1.Value).IsNotEqualTo(Guid.Empty);
    await Assert.That(id1.Value).IsNotEqualTo(id2.Value);
  }

  [Test]
  public async Task ScopeContext_WithInitiatingContextHasScopeContext_ReturnsInitiatingScopeAsync() {
    var scopeAccessor = new ScopeContextAccessor();
    var messageAccessor = new MessageContextAccessor();

    var initiatingScope = _MakeScope("initiating-user", "initiating-tenant");
    var currentScope = _MakeScope("current-user", "current-tenant");
    scopeAccessor.InitiatingContext = new _CapturingContext { ScopeContext = initiatingScope };
    scopeAccessor.Current = currentScope;

    var ctx = new ScopedMessageContext(messageAccessor, scopeAccessor);

    // InitiatingContext.ScopeContext wins over scopeAccessor.Current.
    await Assert.That(ctx.ScopeContext).IsSameReferenceAs(initiatingScope);
  }

  [Test]
  public async Task ScopeContext_WithoutInitiatingContext_FallsBackToCurrentAsync() {
    var scopeAccessor = new ScopeContextAccessor();
    var messageAccessor = new MessageContextAccessor();
    var currentScope = _MakeScope("current-user", "current-tenant");
    scopeAccessor.Current = currentScope;

    var ctx = new ScopedMessageContext(messageAccessor, scopeAccessor);

    await Assert.That(ctx.ScopeContext).IsSameReferenceAs(currentScope);
  }

  [Test]
  public async Task ScopeContext_WithInitiatingContextButNoInnerScope_FallsBackToCurrentAsync() {
    // InitiatingContext is non-null but its ScopeContext is null → keep walking.
    var scopeAccessor = new ScopeContextAccessor();
    var messageAccessor = new MessageContextAccessor();

    var currentScope = _MakeScope("current-user", "current-tenant");
    scopeAccessor.InitiatingContext = new _CapturingContext { ScopeContext = null };
    scopeAccessor.Current = currentScope;

    var ctx = new ScopedMessageContext(messageAccessor, scopeAccessor);

    await Assert.That(ctx.ScopeContext).IsSameReferenceAs(currentScope);
  }

  [Test]
  public async Task TenantId_WithAllSourcesNull_ReturnsNullAsync() {
    var scopeAccessor = new ScopeContextAccessor();
    var messageAccessor = new MessageContextAccessor();
    var ctx = new ScopedMessageContext(messageAccessor, scopeAccessor);

    await Assert.That(ctx.TenantId).IsNull();
  }

  [Test]
  public async Task CallerInfo_WithMessageContextCallerInfo_ReturnsItAsync() {
    var scopeAccessor = new ScopeContextAccessor();
    var messageAccessor = new MessageContextAccessor();
    var caller = new _CapturingCallerInfo {
      CallerMemberName = "DoThing",
      CallerFilePath = "/x/y.cs",
      CallerLineNumber = 42,
    };
    messageAccessor.Current = new _CapturingContext { CallerInfo = caller };

    var ctx = new ScopedMessageContext(messageAccessor, scopeAccessor);

    await Assert.That(ctx.CallerInfo).IsSameReferenceAs(caller);
  }

  [Test]
  public async Task CallerInfo_WithoutMessageContext_ReturnsNullAsync() {
    var scopeAccessor = new ScopeContextAccessor();
    var messageAccessor = new MessageContextAccessor();
    var ctx = new ScopedMessageContext(messageAccessor, scopeAccessor);

    await Assert.That(ctx.CallerInfo).IsNull();
  }

  // --------- helpers ---------

  private static ImmutableScopeContext _MakeScope(string? userId, string? tenantId) {
    var extraction = new SecurityExtraction {
      Scope = new PerspectiveScope { UserId = userId, TenantId = tenantId },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>(),
      Source = "TestSource",
    };
    return new ImmutableScopeContext(extraction, shouldPropagate: true);
  }

  /// <summary>
  /// Minimal <see cref="IMessageContext"/> fake that lets tests set
  /// <see cref="ScopeContext"/> and <see cref="CallerInfo"/> directly,
  /// which the existing nested <c>TestMessageContext</c> hard-coded to null.
  /// </summary>
  private sealed class _CapturingContext : IMessageContext {
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

  private sealed class _CapturingCallerInfo : ICallerInfo {
    public string CallerMemberName { get; init; } = "";
    public string CallerFilePath { get; init; } = "";
    public int CallerLineNumber { get; init; }
  }
}
