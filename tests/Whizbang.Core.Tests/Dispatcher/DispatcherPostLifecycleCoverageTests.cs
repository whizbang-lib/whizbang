#pragma warning disable CA1707

using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.Tags;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>
/// Coverage tests for Dispatcher PostLifecycle stage invocation paths.
/// Covers _invokePostLifecycleReceptorsAsync, _hasPostLifecycleReceptors,
/// and the sync void receptor + PostLifecycle async fallback path.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Dispatcher.cs</code-under-test>
[Category("Dispatcher")]
[Category("Coverage")]
public class DispatcherPostLifecycleCoverageTests {

  public record PostLifecycleCommand(string Data);
  public record PostLifecycleSyncCommand(string Data);
  public record PostLifecycleWithResultCommand(string Data);
  public record PostLifecycleResult(string Value);

  private static readonly List<string> _invocations = [];
  private static readonly Lock _lock = new();

  private static void _track(string invocation) {
    lock (_lock) { _invocations.Add(invocation); }
  }

  private static void _reset() {
    lock (_lock) { _invocations.Clear(); }
  }

  private static List<string> _snapshot() {
    lock (_lock) { return [.. _invocations]; }
  }

  // ========================================
  // Dispatcher subclass with PostLifecycle receptors registered
  // ========================================

  private sealed class PostLifecycleDispatcher(IServiceProvider sp) : Core.Dispatcher(sp, new ServiceInstanceProvider(configuration: null),
             receptorRegistry: sp.GetService<IReceptorRegistry>()) {
    protected override ReceptorInvoker<TResult>? GetReceptorInvoker<TResult>(object message, Type messageType) {
      if (messageType == typeof(PostLifecycleWithResultCommand)) {
        ValueTask<TResult> invoker(object msg) {
          _track("with-result");
          return ValueTask.FromResult((TResult)(object)new PostLifecycleResult("done"));
        }
        return invoker;
      }

      return null;
    }

    protected override VoidReceptorInvoker? GetVoidReceptorInvoker(object message, Type messageType) {
      if (messageType == typeof(PostLifecycleCommand)) {
        return msg => {
          _track("async-void");
          return ValueTask.CompletedTask;
        };
      }

      return null;
    }

    protected override ReceptorPublisher<TEvent> GetReceptorPublisher<TEvent>(TEvent eventData, Type eventType) {
      return evt => Task.CompletedTask;
    }

    protected override Func<object, IMessageEnvelope?, CancellationToken, Task>? GetUntypedReceptorPublisher(Type eventType) {
      return null;
    }

    protected override SyncReceptorInvoker<TResult>? GetSyncReceptorInvoker<TResult>(object message, Type messageType) {
      return null;
    }

    protected override VoidSyncReceptorInvoker? GetVoidSyncReceptorInvoker(object message, Type messageType) {
      if (messageType == typeof(PostLifecycleSyncCommand)) {
        return msg => _track("sync-void");
      }

      return null;
    }

    protected override Func<object, ValueTask<object?>>? GetReceptorInvokerAny(object message, Type messageType) {
      return null;
    }

    protected override DispatchModes? GetReceptorDefaultRouting(Type messageType) {
      return null;
    }
  }

  // ========================================
  // Service provider builder with PostLifecycle receptors
  // ========================================

  private static ServiceProvider _buildProviderWithPostLifecycleReceptors(
      bool registerAsyncReceptor = true,
      bool registerInlineReceptor = false,
      bool registerTagProcessor = false) {
    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity(options => options.AllowAnonymous = true);

    var registry = new TestReceptorRegistry();

    if (registerAsyncReceptor) {
      registry.AddReceptor(LifecycleStage.PostLifecycleDetached, new ReceptorInfo(
        MessageType: typeof(PostLifecycleCommand),
        ReceptorId: "test_post_lifecycle_async",
        InvokeAsync: (sp, msg, envelope, callerInfo, ct) => {
          _track("post-lifecycle-async");
          return ValueTask.FromResult<object?>(null);
        }
      ));
      registry.AddReceptor(LifecycleStage.PostLifecycleDetached, new ReceptorInfo(
        MessageType: typeof(PostLifecycleSyncCommand),
        ReceptorId: "test_post_lifecycle_async_sync",
        InvokeAsync: (sp, msg, envelope, callerInfo, ct) => {
          _track("post-lifecycle-async-for-sync");
          return ValueTask.FromResult<object?>(null);
        }
      ));
      registry.AddReceptor(LifecycleStage.PostLifecycleDetached, new ReceptorInfo(
        MessageType: typeof(PostLifecycleWithResultCommand),
        ReceptorId: "test_post_lifecycle_async_result",
        InvokeAsync: (sp, msg, envelope, callerInfo, ct) => {
          _track("post-lifecycle-async-result");
          return ValueTask.FromResult<object?>(null);
        }
      ));
    }

    if (registerInlineReceptor) {
      registry.AddReceptor(LifecycleStage.PostLifecycleInline, new ReceptorInfo(
        MessageType: typeof(PostLifecycleCommand),
        ReceptorId: "test_post_lifecycle_inline",
        InvokeAsync: (sp, msg, envelope, callerInfo, ct) => {
          _track("post-lifecycle-inline");
          return ValueTask.FromResult<object?>(null);
        }
      ));
      registry.AddReceptor(LifecycleStage.PostLifecycleInline, new ReceptorInfo(
        MessageType: typeof(PostLifecycleSyncCommand),
        ReceptorId: "test_post_lifecycle_inline_sync",
        InvokeAsync: (sp, msg, envelope, callerInfo, ct) => {
          _track("post-lifecycle-inline-for-sync");
          return ValueTask.FromResult<object?>(null);
        }
      ));
      registry.AddReceptor(LifecycleStage.PostLifecycleInline, new ReceptorInfo(
        MessageType: typeof(PostLifecycleWithResultCommand),
        ReceptorId: "test_post_lifecycle_inline_result",
        InvokeAsync: (sp, msg, envelope, callerInfo, ct) => {
          _track("post-lifecycle-inline-result");
          return ValueTask.FromResult<object?>(null);
        }
      ));
    }

    if (registerTagProcessor) {
      services.AddSingleton<IMessageTagProcessor>(new TrackingTagProcessor());
    }

    services.AddSingleton<IReceptorRegistry>(registry);
    services.AddScoped<IReceptorInvoker>(sp =>
      new ReceptorInvoker(sp.GetRequiredService<IReceptorRegistry>(), sp));

    return services.BuildServiceProvider();
  }

  // ========================================
  // Tests: _invokePostLifecycleReceptorsAsync full path
  // ========================================

  [Test]
  [NotInParallel]
  public async Task LocalInvokeAsync_Void_FiresPostLifecycleDetached_WhenReceptorsRegisteredAsync() {
    _reset();
    var provider = _buildProviderWithPostLifecycleReceptors();
    var dispatcher = new PostLifecycleDispatcher(provider);

    await dispatcher.LocalInvokeAsync(new PostLifecycleCommand("test"));

    var invocations = _snapshot();
    await Assert.That(invocations).Contains("async-void");
    await Assert.That(invocations).Contains("post-lifecycle-async");
  }

  [Test]
  [NotInParallel]
  public async Task LocalInvokeAsync_Void_FiresPostLifecycleInline_WhenInlineReceptorsRegisteredAsync() {
    _reset();
    var provider = _buildProviderWithPostLifecycleReceptors(registerAsyncReceptor: false, registerInlineReceptor: true);
    var dispatcher = new PostLifecycleDispatcher(provider);

    await dispatcher.LocalInvokeAsync(new PostLifecycleCommand("test"));

    var invocations = _snapshot();
    await Assert.That(invocations).Contains("async-void");
    await Assert.That(invocations).Contains("post-lifecycle-inline");
  }

  [Test]
  [NotInParallel]
  public async Task LocalInvokeAsync_Void_FiresBothAsyncAndInline_WhenBothRegisteredAsync() {
    _reset();
    var provider = _buildProviderWithPostLifecycleReceptors(registerAsyncReceptor: true, registerInlineReceptor: true);
    var dispatcher = new PostLifecycleDispatcher(provider);

    await dispatcher.LocalInvokeAsync(new PostLifecycleCommand("test"));

    var invocations = _snapshot();
    await Assert.That(invocations).Contains("post-lifecycle-async");
    await Assert.That(invocations).Contains("post-lifecycle-inline");
  }

  // ========================================
  // Tests: Sync void receptor + PostLifecycle async fallback
  // ========================================

  [Test]
  [NotInParallel]
  public async Task LocalInvokeAsync_SyncVoid_FiresPostLifecycle_WhenReceptorsRegisteredAsync() {
    _reset();
    var provider = _buildProviderWithPostLifecycleReceptors();
    var dispatcher = new PostLifecycleDispatcher(provider);

    await dispatcher.LocalInvokeAsync(new PostLifecycleSyncCommand("test"));

    var invocations = _snapshot();
    await Assert.That(invocations).Contains("sync-void");
    await Assert.That(invocations).Contains("post-lifecycle-async-for-sync");
  }

  [Test]
  [NotInParallel]
  public async Task LocalInvokeAsync_SyncVoid_FiresPostLifecycleInline_WhenInlineRegisteredAsync() {
    _reset();
    var provider = _buildProviderWithPostLifecycleReceptors(registerAsyncReceptor: false, registerInlineReceptor: true);
    var dispatcher = new PostLifecycleDispatcher(provider);

    await dispatcher.LocalInvokeAsync(new PostLifecycleSyncCommand("test"));

    var invocations = _snapshot();
    await Assert.That(invocations).Contains("sync-void");
    await Assert.That(invocations).Contains("post-lifecycle-inline-for-sync");
  }

  // ========================================
  // Tests: With-result receptor + PostLifecycle
  // ========================================

  [Test]
  [NotInParallel]
  public async Task LocalInvokeAsync_WithResult_FiresPostLifecycle_WhenReceptorsRegisteredAsync() {
    _reset();
    var provider = _buildProviderWithPostLifecycleReceptors();
    var dispatcher = new PostLifecycleDispatcher(provider);

    var result = await dispatcher.LocalInvokeAsync<PostLifecycleResult>(new PostLifecycleWithResultCommand("test"));

    var invocations = _snapshot();
    await Assert.That(result).IsNotNull();
    await Assert.That(result.Value).IsEqualTo("done");
    await Assert.That(invocations).Contains("with-result");
    await Assert.That(invocations).Contains("post-lifecycle-async-result");
  }

  // ========================================
  // Tests: Tag processing at PostLifecycleInline
  // ========================================

  [Test]
  [NotInParallel]
  public async Task LocalInvokeAsync_Void_ProcessesTags_WhenTagProcessorRegisteredAsync() {
    _reset();
    var provider = _buildProviderWithPostLifecycleReceptors(
      registerAsyncReceptor: true, registerInlineReceptor: true, registerTagProcessor: true);
    var dispatcher = new PostLifecycleDispatcher(provider);

    await dispatcher.LocalInvokeAsync(new PostLifecycleCommand("test"));

    var invocations = _snapshot();
    await Assert.That(invocations).Contains("tag-processed");
  }

  // ========================================
  // Tests: No receptors — early exit path
  // ========================================

  [Test]
  [NotInParallel]
  public async Task LocalInvokeAsync_Void_SkipsPostLifecycle_WhenNoReceptorsRegisteredAsync() {
    _reset();
    var provider = _buildProviderWithPostLifecycleReceptors(registerAsyncReceptor: false, registerInlineReceptor: false);
    var dispatcher = new PostLifecycleDispatcher(provider);

    await dispatcher.LocalInvokeAsync(new PostLifecycleCommand("test"));

    var invocations = _snapshot();
    await Assert.That(invocations).Contains("async-void");
    await Assert.That(invocations).DoesNotContain("post-lifecycle-async");
    await Assert.That(invocations).DoesNotContain("post-lifecycle-inline");
  }

  // ========================================
  // Tests: _hasPostLifecycleReceptors branches
  // ========================================

  [Test]
  [NotInParallel]
  public async Task HasPostLifecycleReceptors_ReturnsFalse_WhenNoRegistryAsync() {
    _reset();
    // Build provider WITHOUT IReceptorRegistry — _hasPostLifecycleReceptors returns false
    // so sync receptor takes the fast path (no PostLifecycle invocation)
    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity(options => options.AllowAnonymous = true);
    var provider = services.BuildServiceProvider();
    var dispatcher = new PostLifecycleDispatcher(provider);

    // Sync command — takes fast sync path, no PostLifecycle fired
    await dispatcher.LocalInvokeAsync(new PostLifecycleSyncCommand("test"));

    var invocations = _snapshot();
    await Assert.That(invocations).Contains("sync-void");
    await Assert.That(invocations).DoesNotContain("post-lifecycle-async");
    await Assert.That(invocations).DoesNotContain("post-lifecycle-inline");
  }

  // ========================================
  // Tests: scopedInvoker null path in _invokePostLifecycleReceptorsAsync
  // ========================================

  [Test]
  [NotInParallel]
  public async Task LocalInvokeAsync_Void_SkipsPostLifecycle_WhenScopedInvokerNotAvailableAsync() {
    _reset();
    // Register PostLifecycle receptors in the registry but do NOT register IReceptorInvoker
    // in the service provider. This covers the scopedInvoker-is-null branch.
    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity(options => options.AllowAnonymous = true);
    var registry = new TestReceptorRegistry();
    registry.AddReceptor(LifecycleStage.PostLifecycleDetached, new ReceptorInfo(
      MessageType: typeof(PostLifecycleCommand),
      ReceptorId: "test_unreachable",
      InvokeAsync: (sp, msg, envelope, callerInfo, ct) => {
        _track("should-not-fire");
        return ValueTask.FromResult<object?>(null);
      }
    ));
    services.AddSingleton<IReceptorRegistry>(registry);
    // Deliberately NOT registering IReceptorInvoker

    var provider = services.BuildServiceProvider();
    var dispatcher = new PostLifecycleDispatcher(provider);

    await dispatcher.LocalInvokeAsync(new PostLifecycleCommand("test"));

    var invocations = _snapshot();
    await Assert.That(invocations).Contains("async-void");
    await Assert.That(invocations).DoesNotContain("should-not-fire");
  }

  // ========================================
  // Tests: Coordinator path (ILifecycleCoordinator registered)
  // ========================================

  private static ServiceProvider _buildProviderWithCoordinator(
      bool registerAsyncReceptor = true,
      bool registerInlineReceptor = false,
      bool registerTagProcessor = false) {
    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity(options => options.AllowAnonymous = true);

    var registry = new TestReceptorRegistry();

    if (registerAsyncReceptor) {
      registry.AddReceptor(LifecycleStage.PostLifecycleDetached, new ReceptorInfo(
        MessageType: typeof(PostLifecycleCommand),
        ReceptorId: "coord_post_lifecycle_async",
        InvokeAsync: (sp, msg, envelope, callerInfo, ct) => {
          _track("coord-post-lifecycle-async");
          return ValueTask.FromResult<object?>(null);
        }));
      registry.AddReceptor(LifecycleStage.PostLifecycleDetached, new ReceptorInfo(
        MessageType: typeof(PostLifecycleSyncCommand),
        ReceptorId: "coord_post_lifecycle_async_sync",
        InvokeAsync: (sp, msg, envelope, callerInfo, ct) => {
          _track("coord-post-lifecycle-async-for-sync");
          return ValueTask.FromResult<object?>(null);
        }));
    }

    if (registerInlineReceptor) {
      registry.AddReceptor(LifecycleStage.PostLifecycleInline, new ReceptorInfo(
        MessageType: typeof(PostLifecycleCommand),
        ReceptorId: "coord_post_lifecycle_inline",
        InvokeAsync: (sp, msg, envelope, callerInfo, ct) => {
          _track("coord-post-lifecycle-inline");
          return ValueTask.FromResult<object?>(null);
        }));
    }

    if (registerTagProcessor) {
      services.AddSingleton<IMessageTagProcessor>(new TrackingTagProcessor());
    }

    services.AddSingleton<IReceptorRegistry>(registry);
    services.AddScoped<IReceptorInvoker>(sp =>
      new ReceptorInvoker(sp.GetRequiredService<IReceptorRegistry>(), sp));
    // Register the coordinator — this exercises the coordinator path in Dispatcher
    services.AddSingleton<ILifecycleCoordinator, LifecycleCoordinator>();

    return services.BuildServiceProvider();
  }

  [Test]
  [NotInParallel]
  public async Task LocalInvokeAsync_WithCoordinator_FiresPostLifecycleDetached_ViaCoordinatorAsync() {
    _reset();
    var provider = _buildProviderWithCoordinator();
    var dispatcher = new PostLifecycleDispatcher(provider);

    await dispatcher.LocalInvokeAsync(new PostLifecycleCommand("test"));
    var coordinator = provider.GetRequiredService<ILifecycleCoordinator>();
    await ((LifecycleCoordinator)coordinator).DrainAllDetachedAsync();

    var invocations = _snapshot();
    await Assert.That(invocations).Contains("async-void");
    await Assert.That(invocations).Contains("coord-post-lifecycle-async")
      .Because("Coordinator path should fire PostLifecycleDetached receptors");
  }

  [Test]
  [NotInParallel]
  public async Task LocalInvokeAsync_WithCoordinator_FiresBothAsyncAndInline_ViaCoordinatorAsync() {
    _reset();
    var provider = _buildProviderWithCoordinator(registerAsyncReceptor: true, registerInlineReceptor: true);
    var dispatcher = new PostLifecycleDispatcher(provider);

    await dispatcher.LocalInvokeAsync(new PostLifecycleCommand("test"));
    var coordinator = provider.GetRequiredService<ILifecycleCoordinator>();
    await ((LifecycleCoordinator)coordinator).DrainAllDetachedAsync();

    var invocations = _snapshot();
    await Assert.That(invocations).Contains("coord-post-lifecycle-async");
    await Assert.That(invocations).Contains("coord-post-lifecycle-inline");
  }

  [Test]
  [NotInParallel]
  public async Task LocalInvokeAsync_SyncVoid_WithCoordinator_FiresPostLifecycleDetached_ViaCoordinatorAsync() {
    _reset();
    var provider = _buildProviderWithCoordinator();
    var dispatcher = new PostLifecycleDispatcher(provider);

    await dispatcher.LocalInvokeAsync(new PostLifecycleSyncCommand("test"));
    var coordinator = provider.GetRequiredService<ILifecycleCoordinator>();
    await ((LifecycleCoordinator)coordinator).DrainAllDetachedAsync();

    var invocations = _snapshot();
    await Assert.That(invocations).Contains("sync-void");
    await Assert.That(invocations).Contains("coord-post-lifecycle-async-for-sync");
  }

  [Test]
  [NotInParallel]
  public async Task LocalInvokeAsync_WithCoordinator_AndTagProcessor_FiresTagsAsync() {
    _reset();
    var provider = _buildProviderWithCoordinator(registerTagProcessor: true);
    var dispatcher = new PostLifecycleDispatcher(provider);

    await dispatcher.LocalInvokeAsync(new PostLifecycleCommand("test"));
    var coordinator = provider.GetRequiredService<ILifecycleCoordinator>();
    await ((LifecycleCoordinator)coordinator).DrainAllDetachedAsync();

    var invocations = _snapshot();
    await Assert.That(invocations).Contains("coord-post-lifecycle-async");
    await Assert.That(invocations).Contains("tag-processed")
      .Because("Coordinator path should also process tags via ReceptorInvoker");
  }

  // ========================================
  // Test helpers
  // ========================================

  private sealed class TestReceptorRegistry : IReceptorRegistry {
    private readonly Dictionary<(Type, LifecycleStage), List<ReceptorInfo>> _receptors = [];

    public void AddReceptor(LifecycleStage stage, ReceptorInfo receptor) {
      var key = (receptor.MessageType, stage);
      if (!_receptors.TryGetValue(key, out var list)) {
        list = [];
        _receptors[key] = list;
      }
      list.Add(receptor);
    }

    public IReadOnlyList<ReceptorInfo> GetReceptorsFor(Type messageType, LifecycleStage stage) {
      var key = (messageType, stage);
      return _receptors.TryGetValue(key, out var list) ? list : Array.Empty<ReceptorInfo>();
    }

    public void Register<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage { }
    public bool Unregister<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage => false;
    public void Register<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage { }
    public bool Unregister<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage => false;
  }

  private sealed class TrackingTagProcessor : IMessageTagProcessor {
    public ValueTask ProcessTagsAsync(
        object message, Type messageType,
        LifecycleStage stage, IScopeContext? scope,
        CancellationToken cancellationToken = default) {
      _track("tag-processed");
      return ValueTask.CompletedTask;
    }
  }
}
