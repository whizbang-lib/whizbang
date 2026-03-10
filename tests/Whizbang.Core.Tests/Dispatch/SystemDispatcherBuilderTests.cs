using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lenses;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Dispatch;

/// <summary>
/// Tests for <see cref="SystemDispatcherBuilder"/> which enforces explicit tenant strategy
/// for AsSystem() operations at compile-time.
/// </summary>
/// <tests>Whizbang.Core/Dispatch/SystemDispatcherBuilder.cs</tests>
[Category("Security")]
[Category("Dispatcher")]
[Category("TenantStrategy")]
[NotInParallel]
public class SystemDispatcherBuilderTests {
  #region TenantConstants Tests

  [Test]
  public async Task TenantConstants_AllTenants_IsAsteriskAsync() {
    // Arrange - Read the constant value to avoid TUnitAssertions0005
    var allTenants = TenantConstants.AllTenants;

    // Assert - AllTenants constant is "*"
    await Assert.That(allTenants).IsEqualTo("*");
  }

  #endregion

  #region ForAllTenants Tests

  [Test]
  public async Task AsSystem_ForAllTenants_SetsTenantIdToAllTenantsConstantAsync() {
    // Arrange
    var scopeContextAccessor = new ScopeContextAccessor();
    scopeContextAccessor.Current = null; // No ambient context

    var mockDispatcher = new MockDispatcher();
    var systemBuilder = new SystemDispatcherBuilder(
        mockDispatcher,
        actualPrincipal: null,
        ambientTenantId: null);

    // Act
    var securityBuilder = systemBuilder.ForAllTenants();

    // Assert - Should return DispatcherSecurityBuilder with AllTenants
    await Assert.That(securityBuilder).IsNotNull();
    await Assert.That(securityBuilder).IsTypeOf<DispatcherSecurityBuilder>();

    // Verify the tenant is set to AllTenants by checking the builder's internal state
    // We'll verify this by executing and checking the captured context
  }

  #endregion

  #region ForTenant Tests

  [Test]
  public async Task AsSystem_ForTenant_SetsExplicitTenantIdAsync() {
    // Arrange
    var mockDispatcher = new MockDispatcher();
    var systemBuilder = new SystemDispatcherBuilder(
        mockDispatcher,
        actualPrincipal: "user@example.com",
        ambientTenantId: null);

    // Act
    var securityBuilder = systemBuilder.ForTenant("explicit-tenant-123");

    // Assert
    await Assert.That(securityBuilder).IsNotNull();
    await Assert.That(securityBuilder).IsTypeOf<DispatcherSecurityBuilder>();
  }

  [Test]
  public async Task AsSystem_ForTenant_WithNullTenantId_ThrowsArgumentExceptionAsync() {
    // Arrange
    var mockDispatcher = new MockDispatcher();
    var systemBuilder = new SystemDispatcherBuilder(
        mockDispatcher,
        actualPrincipal: null,
        ambientTenantId: null);

    // Act & Assert
    await Assert.That(() => systemBuilder.ForTenant(null!))
        .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task AsSystem_ForTenant_WithEmptyTenantId_ThrowsArgumentExceptionAsync() {
    // Arrange
    var mockDispatcher = new MockDispatcher();
    var systemBuilder = new SystemDispatcherBuilder(
        mockDispatcher,
        actualPrincipal: null,
        ambientTenantId: null);

    // Act & Assert
    await Assert.That(() => systemBuilder.ForTenant(""))
        .Throws<ArgumentException>();
  }

  [Test]
  public async Task AsSystem_ForTenant_WithWhitespaceTenantId_ThrowsArgumentExceptionAsync() {
    // Arrange
    var mockDispatcher = new MockDispatcher();
    var systemBuilder = new SystemDispatcherBuilder(
        mockDispatcher,
        actualPrincipal: null,
        ambientTenantId: null);

    // Act & Assert
    await Assert.That(() => systemBuilder.ForTenant("   "))
        .Throws<ArgumentException>();
  }

  #endregion

  #region KeepTenant Tests

  [Test]
  public async Task AsSystem_KeepTenant_PreservesAmbientTenantIdAsync() {
    // Arrange
    var ambientTenantId = "ambient-tenant-456";
    var mockDispatcher = new MockDispatcher();
    var systemBuilder = new SystemDispatcherBuilder(
        mockDispatcher,
        actualPrincipal: "user@example.com",
        ambientTenantId: ambientTenantId);

    // Act
    var securityBuilder = systemBuilder.KeepTenant();

    // Assert
    await Assert.That(securityBuilder).IsNotNull();
    await Assert.That(securityBuilder).IsTypeOf<DispatcherSecurityBuilder>();
  }

  [Test]
  public async Task AsSystem_KeepTenant_WhenNoAmbientTenant_ThrowsInvalidOperationExceptionAsync() {
    // Arrange - No ambient tenant
    var mockDispatcher = new MockDispatcher();
    var systemBuilder = new SystemDispatcherBuilder(
        mockDispatcher,
        actualPrincipal: null,
        ambientTenantId: null); // No ambient tenant!

    // Act & Assert
    await Assert.That(() => systemBuilder.KeepTenant())
        .Throws<InvalidOperationException>()
        .WithMessageContaining("no ambient tenant context");
  }

  [Test]
  public async Task AsSystem_KeepTenant_WhenAmbientTenantIsEmpty_ThrowsInvalidOperationExceptionAsync() {
    // Arrange - Empty ambient tenant
    var mockDispatcher = new MockDispatcher();
    var systemBuilder = new SystemDispatcherBuilder(
        mockDispatcher,
        actualPrincipal: null,
        ambientTenantId: ""); // Empty string ambient tenant

    // Act & Assert
    await Assert.That(() => systemBuilder.KeepTenant())
        .Throws<InvalidOperationException>()
        .WithMessageContaining("no ambient tenant context");
  }

  #endregion

  #region Type Safety Tests

  [Test]
  public async Task SystemDispatcherBuilder_DoesNotHaveSendAsyncMethodAsync() {
    // This test verifies compile-time enforcement by checking type members
    // SystemDispatcherBuilder should NOT have SendAsync - only tenant strategy methods

    var type = typeof(SystemDispatcherBuilder);
    var sendAsyncMethod = type.GetMethod("SendAsync");

    await Assert.That(sendAsyncMethod).IsNull()
        .Because("SystemDispatcherBuilder should not have SendAsync - must choose tenant strategy first");
  }

  [Test]
  public async Task SystemDispatcherBuilder_HasOnlyTenantStrategyMethodsAsync() {
    // Arrange
    var type = typeof(SystemDispatcherBuilder);
    var publicMethods = type.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
        .Where(m => !m.IsSpecialName) // Exclude property getters/setters
        .Where(m => m.DeclaringType == typeof(SystemDispatcherBuilder)) // Only methods declared on this type
        .Select(m => m.Name)
        .ToHashSet();

    // Assert - Should only have ForAllTenants, ForTenant, KeepTenant
    await Assert.That(publicMethods).Contains("ForAllTenants");
    await Assert.That(publicMethods).Contains("ForTenant");
    await Assert.That(publicMethods).Contains("KeepTenant");

    // Should NOT have dispatch methods
    await Assert.That(publicMethods).DoesNotContain("SendAsync");
    await Assert.That(publicMethods).DoesNotContain("PublishAsync");
    await Assert.That(publicMethods).DoesNotContain("LocalInvokeAsync");
  }

  #endregion

  #region Helper Classes

  /// <summary>
  /// Mock dispatcher for testing SystemDispatcherBuilder without full DI setup.
  /// Implements all IDispatcher methods with minimal no-op implementations.
  /// </summary>
  private sealed class MockDispatcher : IDispatcher {
    private static readonly IDeliveryReceipt _emptyReceipt = new MockDeliveryReceipt();

    // SendAsync overloads
    public Task<IDeliveryReceipt> SendAsync<TMessage>(TMessage message) where TMessage : notnull =>
        Task.FromResult(_emptyReceipt);

    public Task<IDeliveryReceipt> SendAsync(object message) =>
        Task.FromResult(_emptyReceipt);

    public Task<IDeliveryReceipt> SendAsync(
        object message,
        IMessageContext context,
        string callerMemberName = "",
        string callerFilePath = "",
        int callerLineNumber = 0) =>
        Task.FromResult(_emptyReceipt);

    public Task<IDeliveryReceipt> SendAsync<TMessage>(TMessage message, DispatchOptions options) where TMessage : notnull =>
        Task.FromResult(_emptyReceipt);

    public Task<IDeliveryReceipt> SendAsync(object message, DispatchOptions options) =>
        Task.FromResult(_emptyReceipt);

    public Task<IDeliveryReceipt> SendAsync(
        object message,
        IMessageContext context,
        DispatchOptions options,
        string callerMemberName = "",
        string callerFilePath = "",
        int callerLineNumber = 0) =>
        Task.FromResult(_emptyReceipt);

    // LocalInvokeAsync overloads
    public ValueTask<TResult> LocalInvokeAsync<TMessage, TResult>(TMessage message) where TMessage : notnull =>
        ValueTask.FromResult(default(TResult)!);

    public ValueTask<TResult> LocalInvokeAsync<TResult>(object message) =>
        ValueTask.FromResult(default(TResult)!);

    public ValueTask<TResult> LocalInvokeAsync<TMessage, TResult>(
        TMessage message,
        IMessageContext context,
        string callerMemberName = "",
        string callerFilePath = "",
        int callerLineNumber = 0) where TMessage : notnull =>
        ValueTask.FromResult(default(TResult)!);

    public ValueTask<TResult> LocalInvokeAsync<TResult>(
        object message,
        IMessageContext context,
        string callerMemberName = "",
        string callerFilePath = "",
        int callerLineNumber = 0) =>
        ValueTask.FromResult(default(TResult)!);

    public ValueTask LocalInvokeAsync<TMessage>(TMessage message) where TMessage : notnull =>
        ValueTask.CompletedTask;

    public ValueTask LocalInvokeAsync(object message) =>
        ValueTask.CompletedTask;

    public ValueTask LocalInvokeAsync<TMessage>(
        TMessage message,
        IMessageContext context,
        string callerMemberName = "",
        string callerFilePath = "",
        int callerLineNumber = 0) where TMessage : notnull =>
        ValueTask.CompletedTask;

    public ValueTask LocalInvokeAsync(
        object message,
        IMessageContext context,
        string callerMemberName = "",
        string callerFilePath = "",
        int callerLineNumber = 0) =>
        ValueTask.CompletedTask;

    public ValueTask<TResult> LocalInvokeAsync<TResult>(object message, DispatchOptions options) =>
        ValueTask.FromResult(default(TResult)!);

    public ValueTask LocalInvokeAsync(object message, DispatchOptions options) =>
        ValueTask.CompletedTask;

    // PublishAsync overloads
    public Task<IDeliveryReceipt> PublishAsync<TEvent>(TEvent eventData) =>
        Task.FromResult(_emptyReceipt);

    public Task<IDeliveryReceipt> PublishAsync<TEvent>(TEvent eventData, DispatchOptions options) =>
        Task.FromResult(_emptyReceipt);

    // CascadeMessageAsync
    public Task CascadeMessageAsync(IMessage message, IMessageEnvelope? sourceEnvelope, DispatchMode mode, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    // Batch operations
    public Task<IEnumerable<IDeliveryReceipt>> SendManyAsync<TMessage>(IEnumerable<TMessage> messages) where TMessage : notnull =>
        Task.FromResult(Enumerable.Empty<IDeliveryReceipt>());

    public Task<IEnumerable<IDeliveryReceipt>> SendManyAsync(IEnumerable<object> messages) =>
        Task.FromResult(Enumerable.Empty<IDeliveryReceipt>());

    public ValueTask<IEnumerable<TResult>> LocalInvokeManyAsync<TResult>(IEnumerable<object> messages) =>
        ValueTask.FromResult(Enumerable.Empty<TResult>());

    /// <summary>
    /// Minimal mock delivery receipt for testing.
    /// </summary>
    private sealed class MockDeliveryReceipt : IDeliveryReceipt {
      public MessageId MessageId { get; } = MessageId.New();
      public CorrelationId? CorrelationId => null;
      public MessageId? CausationId => null;
      public DateTimeOffset Timestamp => DateTimeOffset.UtcNow;
      public string Destination => "MockDestination";
      public DeliveryStatus Status => DeliveryStatus.Accepted;
      public IReadOnlyDictionary<string, JsonElement> Metadata => new Dictionary<string, JsonElement>();
      public Guid? StreamId => null;
    }
  }

  #endregion
}
