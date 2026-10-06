// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lenses;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.Tests.Generated;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Dispatch;

/// <summary>
/// Branch coverage for <see cref="DispatcherSecurityBuilder"/>'s lazily resolved security logger:
/// it comes from the dispatcher's own provider when that provider has a logger factory, falls back
/// to a null logger for a dispatcher that is not the framework's, and is resolved once per builder.
/// The empty-GUID actual-principal warning is the only thing that logs, so it drives every case.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Dispatch/DispatcherSecurityBuilder.cs</code-under-test>
[Category("Security")]
[Category("Dispatcher")]
[NotInParallel]
public class DispatcherSecurityBuilderBranchCoverageTests {
  private const string LOGGER_CATEGORY = "Whizbang.Core.Dispatch.DispatcherSecurityBuilder";

  [Test]
  public async Task AsSystem_EmptyGuidActualPrincipal_LogsTheWarningThroughTheDispatchersLoggerFactoryAsync() {
    DispatcherSecurityBuilderTestCommandReceptor.ResetCapture();
    var logCollector = new FakeLogCollector();
    using var loggerProvider = new FakeLoggerProvider(logCollector);
    using var loggerFactory = new LoggerFactory([loggerProvider]);
    var services = new ServiceCollection();
    services.AddSingleton<ILoggerFactory>(loggerFactory);
    services.AddSingleton<IServiceInstanceProvider>(
      new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()));
    services.AddSingleton<IScopeContextAccessor>(new ScopeContextAccessor());
    services.AddSingleton<ITraceStore>(new InMemoryTraceStore());
    services.AddReceptors();
    services.AddWhizbangDispatcher();
    await using var provider = services.BuildServiceProvider();
    var dispatcher = provider.GetRequiredService<IDispatcher>();

    var previous = ScopeContextAccessor.CurrentContext;
    ScopeContextAccessor.CurrentContext = new ImmutableScopeContext(
      _extraction(new PerspectiveScope { UserId = Guid.Empty.ToString(), TenantId = "tenant-1" }), shouldPropagate: true);
    try {
      await dispatcher.AsSystem().ForAllTenants().SendAsync(new DispatcherSecurityBuilderTestCommand("payload"));
    } finally {
      ScopeContextAccessor.CurrentContext = previous;
    }

    var warnings = logCollector.GetSnapshot(false)
      .Where(r => r.Category == LOGGER_CATEGORY && r.Level == LogLevel.Warning)
      .ToList();
    await Assert.That(warnings.Count).IsEqualTo(1)
      .Because("an elevation whose originating user is the empty GUID must be reported through the host's own "
        + "logger factory, where an operator will see it");
    await Assert.That(warnings[0].Message).Contains("System");
    await Assert.That(DispatcherSecurityBuilderTestCommandReceptor.CapturedContext!.ActualPrincipal)
      .IsEqualTo(Guid.Empty.ToString())
      .Because("the warning reports the context; it does not refuse or rewrite it");
  }

  [Test]
  public async Task SendAsync_NonFrameworkDispatcher_UsesTheNullLoggerOnceAndStillDispatchesEverySendAsync() {
    var dispatcher = new RecordingDispatcher();
    var builder = new DispatcherSecurityBuilder(
      dispatcher, SecurityContextType.System, effectivePrincipal: "SYSTEM", actualPrincipal: Guid.Empty.ToString());

    // Two sends on one builder: the first resolves the logger (a null logger, since this
    // dispatcher has no framework provider to ask), the second reuses it.
    await builder.SendAsync(new DispatcherSecurityBuilderTestCommand("first"));
    await builder.SendAsync(new DispatcherSecurityBuilderTestCommand("second"));

    await Assert.That(dispatcher.Captured.Count).IsEqualTo(2)
      .Because("a dispatcher without a logger factory must not stop the empty-GUID warning path from dispatching");
    foreach (var context in dispatcher.Captured) {
      await Assert.That(context).IsNotNull();
      await Assert.That(context!.ActualPrincipal).IsEqualTo(Guid.Empty.ToString());
      await Assert.That(context.EffectivePrincipal).IsEqualTo("SYSTEM");
      await Assert.That(context.ContextType).IsEqualTo(SecurityContextType.System);
    }
  }

  private static SecurityExtraction _extraction(PerspectiveScope scope) => new() {
    Scope = scope,
    Roles = new HashSet<string>(),
    Permissions = new HashSet<Permission>(),
    SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
    Claims = new Dictionary<string, string>(),
    Source = "Test"
  };

  /// <summary>
  /// A dispatcher that is not the framework's <see cref="Dispatcher"/>: it records the ambient
  /// scope context each send runs under and does nothing else.
  /// </summary>
  private sealed class RecordingDispatcher : IDispatcher {
    private static readonly IDeliveryReceipt _receipt = new StubDeliveryReceipt();

    public List<IScopeContext?> Captured { get; } = [];

    private Task<IDeliveryReceipt> _record() {
      Captured.Add(ScopeContextAccessor.CurrentContext);
      return Task.FromResult(_receipt);
    }

    public Task<IDeliveryReceipt> SendAsync<TMessage>(TMessage message) where TMessage : notnull => _record();
    public Task<IDeliveryReceipt> SendAsync(object message) => _record();
    public Task<IDeliveryReceipt> SendAsync(object message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) => _record();
    public Task<IDeliveryReceipt> SendAsync<TMessage>(TMessage message, DispatchOptions options) where TMessage : notnull => _record();
    public Task<IDeliveryReceipt> SendAsync(object message, DispatchOptions options) => _record();
    public Task<IDeliveryReceipt> SendAsync(object message, IMessageContext context, DispatchOptions options, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) => _record();

    public ValueTask<TResult> LocalInvokeAsync<TMessage, TResult>(TMessage message) where TMessage : notnull => throw new NotSupportedException();
    public ValueTask<TResult> LocalInvokeAsync<TResult>(object message) => throw new NotSupportedException();
    public ValueTask<TResult> LocalInvokeAsync<TMessage, TResult>(TMessage message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) where TMessage : notnull => throw new NotSupportedException();
    public ValueTask<TResult> LocalInvokeAsync<TResult>(object message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) => throw new NotSupportedException();
    public ValueTask LocalInvokeAsync<TMessage>(TMessage message) where TMessage : notnull => throw new NotSupportedException();
    public ValueTask LocalInvokeAsync(object message) => throw new NotSupportedException();
    public ValueTask LocalInvokeAsync<TMessage>(TMessage message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) where TMessage : notnull => throw new NotSupportedException();
    public ValueTask LocalInvokeAsync(object message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) => throw new NotSupportedException();
    public ValueTask<TResult> LocalInvokeAsync<TResult>(object message, DispatchOptions options) => throw new NotSupportedException();
    public ValueTask LocalInvokeAsync(object message, DispatchOptions options) => throw new NotSupportedException();

    public Task<IDeliveryReceipt> PublishAsync<TEvent>(TEvent eventData) => throw new NotSupportedException();
    public Task<IDeliveryReceipt> PublishAsync<TEvent>(TEvent eventData, DispatchOptions options) => throw new NotSupportedException();
    public Task<bool> PublishOnceAsync<TEvent>(string claimKey, TEvent eventData, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task CascadeMessageAsync(IMessage message, IMessageEnvelope? sourceEnvelope, DispatchModes mode, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<IEnumerable<IDeliveryReceipt>> SendManyAsync<TMessage>(IEnumerable<TMessage> messages) where TMessage : notnull => throw new NotSupportedException();
    public Task<IEnumerable<IDeliveryReceipt>> SendManyAsync(IEnumerable<object> messages) => throw new NotSupportedException();
    public ValueTask<IEnumerable<TResult>> LocalInvokeManyAsync<TResult>(IEnumerable<object> messages) => throw new NotSupportedException();
    public ValueTask<IEnumerable<IDeliveryReceipt>> LocalSendManyAsync<TMessage>(IEnumerable<TMessage> messages) where TMessage : notnull => throw new NotSupportedException();
    public ValueTask<IEnumerable<IDeliveryReceipt>> LocalSendManyAsync(IEnumerable<object> messages) => throw new NotSupportedException();
    public Task<IEnumerable<IDeliveryReceipt>> PublishManyAsync<TEvent>(IEnumerable<TEvent> events) where TEvent : notnull => throw new NotSupportedException();
    public Task<IEnumerable<IDeliveryReceipt>> PublishManyAsync(IEnumerable<object> events) => throw new NotSupportedException();

    public ValueTask<InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TMessage, TResult>(TMessage message) where TMessage : notnull => throw new NotSupportedException();
    public ValueTask<InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TResult>(object message) => throw new NotSupportedException();
    public ValueTask<InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TMessage, TResult>(TMessage message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) where TMessage : notnull => throw new NotSupportedException();
    public ValueTask<InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TResult>(object message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) => throw new NotSupportedException();
    public ValueTask<InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TResult>(object message, DispatchOptions options) => throw new NotSupportedException();
  }

  private sealed class StubDeliveryReceipt : IDeliveryReceipt {
    public MessageId MessageId { get; } = MessageId.New();
    public CorrelationId? CorrelationId => null;
    public MessageId? CausationId => null;
    public DateTimeOffset Timestamp => DateTimeOffset.UtcNow;
    public string Destination => "stub";
    public DeliveryStatus Status => DeliveryStatus.Accepted;
    public IReadOnlyDictionary<string, JsonElement> Metadata => new Dictionary<string, JsonElement>();
    public Guid? StreamId => null;
  }
}
