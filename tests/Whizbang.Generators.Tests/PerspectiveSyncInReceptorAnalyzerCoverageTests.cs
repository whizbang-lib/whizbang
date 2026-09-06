using System.Diagnostics.CodeAnalysis;
using Whizbang.Generators.Analyzers;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Coverage for <see cref="PerspectiveSyncInReceptorAnalyzer"/> paths the existing
/// <c>PerspectiveSyncInReceptorAnalyzerTests</c> never exercise: an unrelated type exposing a
/// same-named method, a sync-awaiting call made from a struct (not a class), a class whose only
/// interfaces are unrelated to receptors, and an unrelated attribute sitting alongside a real
/// <c>[FireAt]</c>.
/// </summary>
/// <tests>Whizbang.Generators.Tests/Analyzers/PerspectiveSyncInReceptorAnalyzerTests.cs</tests>
[Category("Analyzers")]
public class PerspectiveSyncInReceptorAnalyzerCoverageTests {
  /// <summary>
  /// Verifies that calling <c>WaitAsync</c> on an unrelated type (here, <c>SemaphoreSlim</c>) is
  /// not flagged, even though the method name matches. Without this type check, any receptor
  /// using ordinary concurrency primitives like <c>SemaphoreSlim.WaitAsync()</c> would be
  /// misreported as a perspective-sync deadlock risk.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task SemaphoreSlimWaitAsync_UnrelatedType_NoDiagnosticAsync() {
    const string source = """
        using System.Threading;
        using System.Threading.Tasks;
        using Whizbang.Core;

        namespace TestApp;

        public class MyEvent : IEvent { }

        public class SemaphoreReceptor : IReceptor<MyEvent> {
          private readonly SemaphoreSlim _semaphore = new(1, 1);

          public async ValueTask HandleAsync(MyEvent message, CancellationToken ct) {
            await _semaphore.WaitAsync(ct);
            _semaphore.Release();
          }
        }
        """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveSyncInReceptorAnalyzer>(source);

    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ900")).IsEmpty()
      .Because("SemaphoreSlim.WaitAsync has nothing to do with IPerspectiveSyncAwaiter despite the matching method name");
  }

  /// <summary>
  /// Verifies that a sync-awaiting call made from inside a struct (not a class) is not flagged.
  /// Receptors are always classes in this framework, so walking up from the call site to find a
  /// containing class must correctly bail out instead of matching the struct itself or crashing
  /// once it walks past every containing symbol.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task StructContainerAwaitingSync_NoDiagnosticAsync() {
    const string source = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using Whizbang.Core.Perspectives.Sync;

        namespace TestApp;

        public struct StructAwaiterUser {
          private readonly IPerspectiveSyncAwaiter _awaiter;
          public StructAwaiterUser(IPerspectiveSyncAwaiter awaiter) => _awaiter = awaiter;

          public async ValueTask DoWorkAsync(CancellationToken ct) {
            await _awaiter.WaitForStreamAsync(typeof(object), Guid.NewGuid(), null, TimeSpan.FromSeconds(5), null, ct);
          }
        }
        """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveSyncInReceptorAnalyzer>(source);

    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ900")).IsEmpty()
      .Because("a struct can never be a receptor, so walking up from the call site finds no containing class at all");
  }

  /// <summary>
  /// Verifies that a class whose only implemented interface is unrelated to receptors (here,
  /// <c>IDisposable</c>) is not flagged, even while injecting and calling
  /// <c>IPerspectiveSyncAwaiter</c>. Without this check, any type that happens to also be
  /// disposable would be mistaken for a receptor.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task NonReceptorInterfaceOnClass_NoDiagnosticAsync() {
    const string source = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using Whizbang.Core.Perspectives.Sync;

        namespace TestApp;

        public class DisposableNonReceptor : IDisposable {
          private readonly IPerspectiveSyncAwaiter _awaiter;
          public DisposableNonReceptor(IPerspectiveSyncAwaiter awaiter) => _awaiter = awaiter;

          public void Dispose() { }

          public async ValueTask DoWorkAsync(CancellationToken ct) {
            await _awaiter.WaitForStreamAsync(typeof(object), Guid.NewGuid(), null, TimeSpan.FromSeconds(5), null, ct);
          }
        }
        """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveSyncInReceptorAnalyzer>(source);

    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ900")).IsEmpty()
      .Because("IDisposable is not a receptor interface, so the interface scan must reject it and reach the end of the list");
  }

  /// <summary>
  /// Verifies that an unrelated attribute (<c>[Obsolete]</c>) sitting alongside a real
  /// <c>[FireAt(PostInboxDetached)]</c> does not interfere with reading the real stage. If the
  /// attribute scan stopped instead of continuing past a non-<c>[FireAt]</c> attribute, a
  /// <c>[FireAt]</c> declared after an unrelated attribute would be missed and the receptor would
  /// wrongly fall back to the (unsafe) Inline defaults.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task UnrelatedAttributeAlongsideFireAt_DetachedStage_NoDiagnosticAsync() {
    const string source = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using Whizbang.Core;
        using Whizbang.Core.Messaging;
        using Whizbang.Core.Perspectives.Sync;

        namespace TestApp;

        public class MyEvent : IEvent { }

        [Obsolete("legacy")]
        [FireAt(LifecycleStage.PostInboxDetached)]
        public class LegacyDetachedReceptor : IReceptor<MyEvent> {
          private readonly IPerspectiveSyncAwaiter _awaiter;
          public LegacyDetachedReceptor(IPerspectiveSyncAwaiter awaiter) => _awaiter = awaiter;

          public async ValueTask HandleAsync(MyEvent message, CancellationToken ct) {
            await _awaiter.WaitForStreamAsync(typeof(object), Guid.NewGuid(), null, TimeSpan.FromSeconds(5), null, ct);
          }
        }
        """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveSyncInReceptorAnalyzer>(source);

    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ900")).IsEmpty()
      .Because("the real [FireAt(PostInboxDetached)] must still be found and honored despite the unrelated [Obsolete] attribute preceding it");
  }
}
