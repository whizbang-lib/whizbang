// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;

namespace Whizbang.RepoAnalyzers.Tests;

/// <summary>
/// WHIZ611: a channel wait raced in <c>Task.WhenAny</c> is abandoned whenever another task wins, and the channel keeps
/// an abandoned wait until its token is canceled. A wait whose token nothing cancels once the race is decided stays
/// queued for as long as that token lives; on the host's stopping token, that is the life of the process (#2613).
/// </summary>
public class AbandonedChannelWaitAnalyzerTests {
  private const string RULE_ID = "WHIZ611";

  private const string USINGS = """
    using System;
    using System.Threading;
    using System.Threading.Channels;
    using System.Threading.Tasks;

    """;

  [Test]
  public async Task Descriptor_IsTheRepositoryRuleReportedAsAnErrorAsync() {
    var descriptor = new AbandonedChannelWaitAnalyzer().SupportedDiagnostics.Single();

    await Assert.That(descriptor.Id).IsEqualTo(RULE_ID);
    await Assert.That(descriptor.DefaultSeverity).IsEqualTo(DiagnosticSeverity.Error);
    await Assert.That(descriptor.IsEnabledByDefault).IsTrue();
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task WithoutTheChannelsAssembly_ReportsNothingAsync() {
    var diagnostics = await RepoAnalyzerTestHelper.GetDiagnosticsAsync<AbandonedChannelWaitAnalyzer>("""
      using System.Threading.Tasks;
      static class C {
        static Task M(Task a, Task b) => Task.WhenAny(a, b);
      }
      """);

    await Assert.That(diagnostics).IsEmpty();
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task WaitRacedDirectly_OnALongLivedToken_ReportsAsync() {
    var diagnostics = await _analyzeAsync("""
      static class C {
        static async Task M(ChannelReader<int> reader, TimeSpan idle, CancellationToken stopping) {
          await Task.WhenAny(reader.WaitToReadAsync(stopping).AsTask(), Task.Delay(idle, stopping));
        }
      }
      """);

    await Assert.That(diagnostics).Count().IsEqualTo(1);
    await Assert.That(diagnostics[0].Id).IsEqualTo(RULE_ID);
    await Assert.That(diagnostics[0].GetMessage(System.Globalization.CultureInfo.InvariantCulture))
      .Contains("WaitToReadAsync");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task WaitHeldInALocal_ThenRaced_ReportsAsync() {
    var diagnostics = await _analyzeAsync("""
      static class C {
        static async Task M(ChannelReader<int> reader, TimeSpan idle, CancellationToken stopping) {
          while (!stopping.IsCancellationRequested) {
            var wait = reader.WaitToReadAsync(stopping).AsTask();
            await Task.WhenAny(wait, Task.Delay(idle, stopping));
          }
        }
      }
      """);

    await Assert.That(diagnostics).Count().IsEqualTo(1);
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task WaitChosenByAConditional_ThenRaced_ReportsAsync() {
    // The shape of the perspective consumer loop's drain wait before #2613.
    var diagnostics = await _analyzeAsync("""
      static class C {
        static async Task M(ChannelReader<int>? reader, TimeSpan idle, CancellationToken stopping) {
          var wait = reader is null
            ? new TaskCompletionSource<bool>().Task
            : reader.WaitToReadAsync(stopping).AsTask();
          await Task.WhenAny(wait, Task.Delay(idle, stopping));
        }
      }
      """);

    await Assert.That(diagnostics).Count().IsEqualTo(1);
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task WaitAssignedToATaskLocal_ThenRaced_ReportsAsync() {
    var diagnostics = await _analyzeAsync("""
      static class C {
        static async Task M(ChannelReader<int> reader, TimeSpan idle, CancellationToken stopping) {
          Task wait;
          wait = reader.WaitToReadAsync(stopping).AsTask();
          await Task.WhenAny(new[] { wait, Task.Delay(idle, stopping) });
        }
      }
      """);

    await Assert.That(diagnostics).Count().IsEqualTo(1);
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task WaitRacedInsideALambda_ReportsAsync() {
    var diagnostics = await _analyzeAsync("""
      static class C {
        static Func<Task> M(ChannelReader<int> reader, TimeSpan idle, CancellationToken stopping) =>
          () => Task.WhenAny(reader.WaitToReadAsync(stopping).AsTask(), Task.Delay(idle, stopping));
      }
      """);

    await Assert.That(diagnostics).Count().IsEqualTo(1);
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task WriterWait_Raced_ReportsAsync() {
    var diagnostics = await _analyzeAsync("""
      static class C {
        static Task M(ChannelWriter<int> writer, TimeSpan idle, CancellationToken stopping) =>
          Task.WhenAny(writer.WaitToWriteAsync(stopping).AsTask(), Task.Delay(idle, stopping));
      }
      """);

    await Assert.That(diagnostics).Count().IsEqualTo(1);
    await Assert.That(diagnostics[0].GetMessage(System.Globalization.CultureInfo.InvariantCulture))
      .Contains("WaitToWriteAsync");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task WaitOnADerivedReader_Raced_ReportsAsync() {
    var diagnostics = await _analyzeAsync("""
      sealed class Wrapping(ChannelReader<int> inner) : ChannelReader<int> {
        public override bool TryRead(out int item) => inner.TryRead(out item);
        public override ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default) =>
          inner.WaitToReadAsync(cancellationToken);
      }
      static class C {
        static Task M(Wrapping reader, TimeSpan idle, CancellationToken stopping) =>
          Task.WhenAny(reader.WaitToReadAsync(stopping).AsTask(), Task.Delay(idle, stopping));
      }
      """);

    await Assert.That(diagnostics).Count().IsEqualTo(1);
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task WaitWithNoToken_Raced_ReportsAsync() {
    var diagnostics = await _analyzeAsync("""
      static class C {
        static Task M(ChannelReader<int> reader, TimeSpan idle) =>
          Task.WhenAny(reader.WaitToReadAsync().AsTask(), Task.Delay(idle));
      }
      """);

    await Assert.That(diagnostics).Count().IsEqualTo(1);
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task WaitOnALinkedToken_CanceledWithCancelAsync_ReportsNothingAsync() {
    var diagnostics = await _analyzeAsync("""
      static class C {
        static async Task M(ChannelReader<int> reader, TimeSpan idle, CancellationToken stopping) {
          using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(stopping);
          var wait = reader.WaitToReadAsync(waitCts.Token).AsTask();
          await Task.WhenAny(wait, Task.Delay(idle, waitCts.Token));
          await waitCts.CancelAsync();
        }
      }
      """);

    await Assert.That(diagnostics).IsEmpty();
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task WaitOnALinkedToken_CanceledWithCancel_ReportsNothingAsync() {
    var diagnostics = await _analyzeAsync("""
      static class C {
        static async Task M(ChannelReader<int> reader, TimeSpan idle, CancellationToken stopping) {
          using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(stopping);
          await Task.WhenAny(reader.WaitToReadAsync(waitCts.Token).AsTask(), Task.Delay(idle, waitCts.Token));
          waitCts.Cancel();
        }
      }
      """);

    await Assert.That(diagnostics).IsEmpty();
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task WaitOnALinkedToken_WhoseSourceIsOnlyDisposed_ReportsAsync() {
    // Disposing a source does not cancel its token, so the abandoned wait stays queued.
    var diagnostics = await _analyzeAsync("""
      static class C {
        static async Task M(ChannelReader<int> reader, TimeSpan idle, CancellationToken stopping) {
          using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(stopping);
          await Task.WhenAny(reader.WaitToReadAsync(waitCts.Token).AsTask(), Task.Delay(idle, waitCts.Token));
          waitCts.Dispose();
        }
      }
      """);

    await Assert.That(diagnostics).Count().IsEqualTo(1);
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task WaitOnOneSourcesToken_WhenAnotherSourceIsCanceled_ReportsAsync() {
    var diagnostics = await _analyzeAsync("""
      static class C {
        static async Task M(ChannelReader<int> reader, TimeSpan idle, CancellationToken stopping) {
          using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(stopping);
          using var timerCts = CancellationTokenSource.CreateLinkedTokenSource(stopping);
          await Task.WhenAny(reader.WaitToReadAsync(waitCts.Token).AsTask(), Task.Delay(idle, timerCts.Token));
          await timerCts.CancelAsync();
        }
      }
      """);

    await Assert.That(diagnostics).Count().IsEqualTo(1);
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task WaitOnAFieldSourcesToken_Raced_ReportsAsync() {
    // A source the method does not own outlives the race; only a local the method cancels is accepted.
    var diagnostics = await _analyzeAsync("""
      sealed class C(ChannelReader<int> reader) {
        private readonly CancellationTokenSource _stop = new();
        async Task M(TimeSpan idle) {
          await Task.WhenAny(reader.WaitToReadAsync(_stop.Token).AsTask(), Task.Delay(idle));
          _stop.Cancel();
        }
      }
      """);

    await Assert.That(diagnostics).Count().IsEqualTo(1);
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task WaitOnATokenPropertyOfAnotherType_Raced_ReportsAsync() {
    var diagnostics = await _analyzeAsync("""
      sealed class Holder {
        public CancellationToken Token { get; init; }
        public void Cancel() { }
      }
      static class C {
        static async Task M(ChannelReader<int> reader, TimeSpan idle) {
          var holder = new Holder();
          await Task.WhenAny(reader.WaitToReadAsync(holder.Token).AsTask(), Task.Delay(idle));
          holder.Cancel();
        }
      }
      """);

    await Assert.That(diagnostics).Count().IsEqualTo(1);
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task WaitAwaitedToTheEnd_IsNotRaced_ReportsNothingAsync() {
    var diagnostics = await _analyzeAsync("""
      static class C {
        static async Task<bool> M(ChannelReader<int> reader, CancellationToken stopping) {
          var wait = reader.WaitToReadAsync(stopping).AsTask();
          return await wait;
        }
      }
      """);

    await Assert.That(diagnostics).IsEmpty();
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task WaitHandedToAnotherMethod_InsideWhenAny_ReportsNothingAsync() {
    // The nearest call that takes the wait is not WhenAny: that method decides what becomes of it.
    var diagnostics = await _analyzeAsync("""
      static class C {
        static Task Settle(Task<bool> wait) => wait;
        static Task M(ChannelReader<int> reader, TimeSpan idle, CancellationToken stopping) =>
          Task.WhenAny(Settle(reader.WaitToReadAsync(stopping).AsTask()), Task.Delay(idle, stopping));
      }
      """);

    await Assert.That(diagnostics).IsEmpty();
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task AsTaskOfAnotherValueTask_Raced_ReportsNothingAsync() {
    var diagnostics = await _analyzeAsync("""
      static class C {
        static ValueTask<bool> Probe(CancellationToken token) => ValueTask.FromResult(true);
        static Task M(TimeSpan idle, CancellationToken stopping) =>
          Task.WhenAny(Probe(stopping).AsTask(), Task.Delay(idle, stopping));
      }
      """);

    await Assert.That(diagnostics).IsEmpty();
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task AsTaskOfAValueTaskLocal_Raced_ReportsNothingAsync() {
    // The analyzer follows the wait from the call that makes it; a value task held first is not traced.
    var diagnostics = await _analyzeAsync("""
      static class C {
        static Task M(ChannelReader<int> reader, TimeSpan idle, CancellationToken stopping) {
          var pending = reader.WaitToReadAsync(stopping);
          return Task.WhenAny(pending.AsTask(), Task.Delay(idle, stopping));
        }
      }
      """);

    await Assert.That(diagnostics).IsEmpty();
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task WaitHeldInAFieldThenRaced_ReportsNothingAsync() {
    // Reuse across passes keeps the pending wait in a field or a helper; only a fresh wait per race is reported.
    var diagnostics = await _analyzeAsync("""
      sealed class C(ChannelReader<int> reader) {
        private Task<bool>? _pending;
        Task M(TimeSpan idle, CancellationToken stopping) {
          if (_pending is not { IsCompleted: false }) {
            _pending = reader.WaitToReadAsync(stopping).AsTask();
          }
          return Task.WhenAny(_pending, Task.Delay(idle, stopping));
        }
      }
      """);

    await Assert.That(diagnostics).IsEmpty();
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task WaitInALocalNeverRaced_ReportsNothingAsync() {
    var diagnostics = await _analyzeAsync("""
      static class C {
        static async Task M(ChannelReader<int> reader, Task other, CancellationToken stopping) {
          var wait = reader.WaitToReadAsync(stopping).AsTask();
          await Task.WhenAny(other, Task.CompletedTask);
          await wait;
        }
      }
      """);

    await Assert.That(diagnostics).IsEmpty();
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task WaitHandedToAWhenAnyThatIsNotTasks_ReportsNothingAsync() {
    var diagnostics = await _analyzeAsync("""
      static class C {
        static Task WhenAny(Task a, Task b) => a;
        static Task M(ChannelReader<int> reader, TimeSpan idle, CancellationToken stopping) =>
          WhenAny(reader.WaitToReadAsync(stopping).AsTask(), Task.Delay(idle, stopping));
      }
      """);

    await Assert.That(diagnostics).IsEmpty();
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task ReadAsyncThatIsNotAChannels_Raced_ReportsNothingAsync() {
    var diagnostics = await _analyzeAsync("""
      static class C {
        static ValueTask<bool> ReadAsync(CancellationToken token) => ValueTask.FromResult(true);
        static Task M(TimeSpan idle, CancellationToken stopping) =>
          Task.WhenAny(ReadAsync(stopping).AsTask(), Task.Delay(idle, stopping));
      }
      """);

    await Assert.That(diagnostics).IsEmpty();
  }

  [RequiresAssemblyFiles()]
  private static async Task<IReadOnlyList<Diagnostic>> _analyzeAsync(string body) {
    var source = USINGS + body;
    var compilationErrors = RepoAnalyzerTestHelper.CreateCompilation(source, withChannelsReference: true)
      .GetDiagnostics()
      .Where(d => d.Severity == DiagnosticSeverity.Error)
      .Select(d => d.ToString())
      .ToList();
    await Assert.That(compilationErrors).IsEmpty();

    return await RepoAnalyzerTestHelper.GetDiagnosticsAsync<AbandonedChannelWaitAnalyzer>(
      source, withChannelsReference: true);
  }
}
