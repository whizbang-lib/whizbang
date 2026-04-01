using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Whizbang.Generators.Analyzers;

namespace Whizbang.Generators.Tests.Analyzers;

/// <summary>
/// Tests for PerspectiveSyncInReceptorAnalyzer WHIZ900.
/// Verifies that WaitForStreamAsync/WaitAsync calls inside Inline-stage receptors produce errors.
/// </summary>
/// <docs>operations/diagnostics/whiz900</docs>
/// <tests>Whizbang.Generators.Tests/Analyzers/PerspectiveSyncInReceptorAnalyzerTests.cs</tests>
[Category("Analyzers")]
public class PerspectiveSyncInReceptorAnalyzerTests {
  // ========================================
  // WHIZ900: No-diagnostic (safe) cases
  // ========================================

  /// <summary>
  /// Non-receptor class calling WaitForStreamAsync — no diagnostic.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_NonReceptorClass_NoDiagnosticAsync() {
    const string source = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using Whizbang.Core;
        using Whizbang.Core.Perspectives.Sync;

        namespace TestApp;

        public class RegularService {
          private readonly IPerspectiveSyncAwaiter _awaiter;
          public RegularService(IPerspectiveSyncAwaiter awaiter) => _awaiter = awaiter;

          public async Task DoWorkAsync(CancellationToken ct) {
            await _awaiter.WaitForStreamAsync(typeof(object), Guid.NewGuid(), null, TimeSpan.FromSeconds(5), null, ct);
          }
        }
        """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveSyncInReceptorAnalyzer>(source);

    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ900")).IsEmpty();
  }

  /// <summary>
  /// Receptor NOT calling WaitForStreamAsync — no diagnostic.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_ReceptorWithoutSyncCall_NoDiagnosticAsync() {
    const string source = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using Whizbang.Core;

        namespace TestApp;

        public class MyCommand : IMessage { }
        public class MyEvent : IEvent { }

        public class SafeReceptor : IReceptor<MyCommand, MyEvent> {
          public ValueTask<MyEvent> HandleAsync(MyCommand message, CancellationToken ct) {
            return new ValueTask<MyEvent>(new MyEvent());
          }
        }
        """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveSyncInReceptorAnalyzer>(source);

    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ900")).IsEmpty();
  }

  /// <summary>
  /// Receptor with [FireAt(PostInboxDetached)] calling WaitForStreamAsync — no diagnostic (safe).
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_DetachedStageReceptor_NoDiagnosticAsync() {
    const string source = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using Whizbang.Core;
        using Whizbang.Core.Messaging;
        using Whizbang.Core.Perspectives.Sync;

        namespace TestApp;

        public class MyEvent : IEvent { }

        [FireAt(LifecycleStage.PostInboxDetached)]
        public class DetachedReceptor : IReceptor<MyEvent> {
          private readonly IPerspectiveSyncAwaiter _awaiter;
          public DetachedReceptor(IPerspectiveSyncAwaiter awaiter) => _awaiter = awaiter;

          public async ValueTask HandleAsync(MyEvent message, CancellationToken ct) {
            await _awaiter.WaitForStreamAsync(typeof(object), Guid.NewGuid(), null, TimeSpan.FromSeconds(5), null, ct);
          }
        }
        """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveSyncInReceptorAnalyzer>(source);

    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ900")).IsEmpty();
  }

  /// <summary>
  /// Receptor with multiple Detached stages — no diagnostic.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_MultipleDetachedStages_NoDiagnosticAsync() {
    const string source = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using Whizbang.Core;
        using Whizbang.Core.Messaging;
        using Whizbang.Core.Perspectives.Sync;

        namespace TestApp;

        public class MyEvent : IEvent { }

        [FireAt(LifecycleStage.PostInboxDetached)]
        [FireAt(LifecycleStage.PostAllPerspectivesDetached)]
        public class MultiDetachedReceptor : IReceptor<MyEvent> {
          private readonly IPerspectiveSyncAwaiter _awaiter;
          public MultiDetachedReceptor(IPerspectiveSyncAwaiter awaiter) => _awaiter = awaiter;

          public async ValueTask HandleAsync(MyEvent message, CancellationToken ct) {
            await _awaiter.WaitForStreamAsync(typeof(object), Guid.NewGuid(), null, TimeSpan.FromSeconds(5), null, ct);
          }
        }
        """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveSyncInReceptorAnalyzer>(source);

    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ900")).IsEmpty();
  }

  // ========================================
  // WHIZ900: Diagnostic (deadlock) cases
  // ========================================

  /// <summary>
  /// Receptor with no [FireAt] (defaults to Inline) calling WaitForStreamAsync — WHIZ900 error.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_DefaultStageReceptor_WaitForStreamAsync_ReportsDiagnosticAsync() {
    const string source = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using Whizbang.Core;
        using Whizbang.Core.Perspectives.Sync;

        namespace TestApp;

        public class MyEvent : IEvent { }

        public class DefaultStageReceptor : IReceptor<MyEvent> {
          private readonly IPerspectiveSyncAwaiter _awaiter;
          public DefaultStageReceptor(IPerspectiveSyncAwaiter awaiter) => _awaiter = awaiter;

          public async ValueTask HandleAsync(MyEvent message, CancellationToken ct) {
            await _awaiter.WaitForStreamAsync(typeof(object), Guid.NewGuid(), null, TimeSpan.FromSeconds(5), null, ct);
          }
        }
        """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveSyncInReceptorAnalyzer>(source);

    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ900")).Count().IsEqualTo(1);
    var diagnostic = diagnostics.First(d => d.Id == "WHIZ900");
    await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Error);

    var message = diagnostic.GetMessage(CultureInfo.InvariantCulture);
    await Assert.That(message).Contains("DefaultStageReceptor");
    await Assert.That(message).Contains("WaitForStreamAsync");
    await Assert.That(message).Contains("Inline");
  }

  /// <summary>
  /// Receptor with [FireAt(PostInboxInline)] calling WaitForStreamAsync — WHIZ900 error.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_InlineStageReceptor_WaitForStreamAsync_ReportsDiagnosticAsync() {
    const string source = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using Whizbang.Core;
        using Whizbang.Core.Messaging;
        using Whizbang.Core.Perspectives.Sync;

        namespace TestApp;

        public class MyEvent : IEvent { }

        [FireAt(LifecycleStage.PostInboxInline)]
        public class InlineStageReceptor : IReceptor<MyEvent> {
          private readonly IPerspectiveSyncAwaiter _awaiter;
          public InlineStageReceptor(IPerspectiveSyncAwaiter awaiter) => _awaiter = awaiter;

          public async ValueTask HandleAsync(MyEvent message, CancellationToken ct) {
            await _awaiter.WaitForStreamAsync(typeof(object), Guid.NewGuid(), null, TimeSpan.FromSeconds(5), null, ct);
          }
        }
        """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveSyncInReceptorAnalyzer>(source);

    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ900")).Count().IsEqualTo(1);
    var diagnostic = diagnostics.First(d => d.Id == "WHIZ900");
    await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Error);

    var message = diagnostic.GetMessage(CultureInfo.InvariantCulture);
    await Assert.That(message).Contains("InlineStageReceptor");
    await Assert.That(message).Contains("WaitForStreamAsync");
    await Assert.That(message).Contains("PostInboxInline");
    await Assert.That(message).Contains("PostInboxDetached");
  }

  /// <summary>
  /// Receptor with no [FireAt] calling WaitAsync — WHIZ900 error.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_DefaultStageReceptor_WaitAsync_ReportsDiagnosticAsync() {
    const string source = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using Whizbang.Core;
        using Whizbang.Core.Perspectives.Sync;

        namespace TestApp;

        public class MyEvent : IEvent { }

        public class WaitAsyncReceptor : IReceptor<MyEvent> {
          private readonly IPerspectiveSyncAwaiter _awaiter;
          public WaitAsyncReceptor(IPerspectiveSyncAwaiter awaiter) => _awaiter = awaiter;

          public async ValueTask HandleAsync(MyEvent message, CancellationToken ct) {
            await _awaiter.WaitAsync(typeof(object), new PerspectiveSyncOptions(), ct);
          }
        }
        """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveSyncInReceptorAnalyzer>(source);

    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ900")).Count().IsEqualTo(1);
    var diagnostic = diagnostics.First(d => d.Id == "WHIZ900");
    await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Error);

    var message = diagnostic.GetMessage(CultureInfo.InvariantCulture);
    await Assert.That(message).Contains("WaitAsyncReceptor");
    await Assert.That(message).Contains("WaitAsync");
  }

  /// <summary>
  /// Receptor with mixed stages (one Inline, one Detached) calling WaitForStreamAsync — WHIZ900 error
  /// because at least one stage is Inline.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_MixedStages_OneInline_ReportsDiagnosticAsync() {
    const string source = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using Whizbang.Core;
        using Whizbang.Core.Messaging;
        using Whizbang.Core.Perspectives.Sync;

        namespace TestApp;

        public class MyEvent : IEvent { }

        [FireAt(LifecycleStage.PostInboxDetached)]
        [FireAt(LifecycleStage.PostAllPerspectivesInline)]
        public class MixedStageReceptor : IReceptor<MyEvent> {
          private readonly IPerspectiveSyncAwaiter _awaiter;
          public MixedStageReceptor(IPerspectiveSyncAwaiter awaiter) => _awaiter = awaiter;

          public async ValueTask HandleAsync(MyEvent message, CancellationToken ct) {
            await _awaiter.WaitForStreamAsync(typeof(object), Guid.NewGuid(), null, TimeSpan.FromSeconds(5), null, ct);
          }
        }
        """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveSyncInReceptorAnalyzer>(source);

    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ900")).Count().IsEqualTo(1);
    var diagnostic = diagnostics.First(d => d.Id == "WHIZ900");

    var message = diagnostic.GetMessage(CultureInfo.InvariantCulture);
    await Assert.That(message).Contains("MixedStageReceptor");
    await Assert.That(message).Contains("PostAllPerspectivesInline");
  }

  /// <summary>
  /// ISyncReceptor (not IReceptor) calling WaitForStreamAsync at Inline stage — WHIZ900 error.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_SyncReceptor_InlineStage_ReportsDiagnosticAsync() {
    const string source = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using Whizbang.Core;
        using Whizbang.Core.Messaging;
        using Whizbang.Core.Perspectives.Sync;

        namespace TestApp;

        public class MyCommand : IMessage { }
        public class MyResponse { }

        [FireAt(LifecycleStage.PreOutboxInline)]
        public class SyncReceptorWithWait : ISyncReceptor<MyCommand, MyResponse> {
          private readonly IPerspectiveSyncAwaiter _awaiter;
          public SyncReceptorWithWait(IPerspectiveSyncAwaiter awaiter) => _awaiter = awaiter;

          public MyResponse Handle(MyCommand message) {
            _awaiter.WaitForStreamAsync(typeof(object), Guid.NewGuid(), null, TimeSpan.FromSeconds(5), null, default).GetAwaiter().GetResult();
            return new MyResponse();
          }
        }
        """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveSyncInReceptorAnalyzer>(source);

    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ900")).Count().IsEqualTo(1);
    var diagnostic = diagnostics.First(d => d.Id == "WHIZ900");

    var message = diagnostic.GetMessage(CultureInfo.InvariantCulture);
    await Assert.That(message).Contains("SyncReceptorWithWait");
    await Assert.That(message).Contains("PreOutboxInline");
    await Assert.That(message).Contains("PreOutboxDetached");
  }

  /// <summary>
  /// Two-type-arg IReceptor (command → response) at default stage calling WaitForStreamAsync — WHIZ900 error.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_TwoArgReceptor_DefaultStage_ReportsDiagnosticAsync() {
    const string source = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using Whizbang.Core;
        using Whizbang.Core.Perspectives.Sync;

        namespace TestApp;

        public class MyCommand : IMessage { }
        public class MyEvent : IEvent { }

        public class TwoArgReceptor : IReceptor<MyCommand, MyEvent> {
          private readonly IPerspectiveSyncAwaiter _awaiter;
          public TwoArgReceptor(IPerspectiveSyncAwaiter awaiter) => _awaiter = awaiter;

          public async ValueTask<MyEvent> HandleAsync(MyCommand message, CancellationToken ct) {
            await _awaiter.WaitForStreamAsync(typeof(object), Guid.NewGuid(), null, TimeSpan.FromSeconds(5), null, ct);
            return new MyEvent();
          }
        }
        """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveSyncInReceptorAnalyzer>(source);

    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ900")).Count().IsEqualTo(1);
    var diagnostic = diagnostics.First(d => d.Id == "WHIZ900");
    await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Error);

    var message = diagnostic.GetMessage(CultureInfo.InvariantCulture);
    await Assert.That(message).Contains("TwoArgReceptor");
    await Assert.That(message).Contains("WaitForStreamAsync");
  }
}
