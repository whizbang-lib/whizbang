// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.CodeAnalysis;

namespace Whizbang.RepoAnalyzers.Tests;

/// <summary>
/// WHIZ612: a wait raced in <c>Task.WhenAny</c> (or <c>Task.WaitAny</c>) by hand. The loser of the race stays registered
/// wherever it waits (a channel's reader queue, a semaphore's waiters, the timer queue, a token's callbacks) until it
/// completes or its token is canceled; on a worker's stopping token that is the life of the process (#728, #2613). Every
/// such race in the library goes through <c>Whizbang.Core.Async.WakeSet</c>, whose own source file is the one exemption.
/// </summary>
/// <remarks>
/// A wait is a task that completes when something happens elsewhere: <c>Task.Delay</c>, a channel wait, a semaphore
/// wait, a <c>TaskCompletionSource</c>'s task, a <c>WakeSignal</c> wait, or a method marked <c>[WaitSource]</c>, followed
/// through locals, conditionals and task adapters. Anything else is taken as compute and not reported.
/// </remarks>
public class HandWrittenWaitRaceAnalyzerTests {
  // The analyzed samples spell the delay through this constant so the repository text checks do not read them as real waits in a test.
  private const string DELAY = "Task.Delay";

  private const string RULE_ID = "WHIZ612";

  private const string WAKE_SET_PATH = "src/Whizbang.Core/Async/WakeSet.cs";

  private const string USINGS = """
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Channels;
    using System.Threading.Tasks;

    namespace Whizbang.Core.Async {
      [AttributeUsage(AttributeTargets.Method)]
      public sealed class WaitSourceAttribute : Attribute;

      public sealed class WakeSignal {
        public Task WaitAsync(CancellationToken token) => Task.CompletedTask;
      }
    }

    public interface IGate {
      [Whizbang.Core.Async.WaitSource]
      Task WaitForReadyAsync(CancellationToken token);

      Task DoWorkAsync(CancellationToken token);
    }

    """;

  [Test]
  public async Task Descriptor_IsTheRepositoryRuleReportedAsAnErrorAsync() {
    var descriptor = new HandWrittenWaitRaceAnalyzer().SupportedDiagnostics.Single();

    await Assert.That(descriptor.Id).IsEqualTo(RULE_ID);
    await Assert.That(descriptor.DefaultSeverity).IsEqualTo(DiagnosticSeverity.Error);
    await Assert.That(descriptor.IsEnabledByDefault).IsTrue();
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task ADelayRaced_ReportsTheRaceAndNamesTheWaitAsync() {
    var diagnostics = await _analyzeAsync($$"""
      static class C {
        static Task M(Task work, TimeSpan idle, CancellationToken stopping) => Task.WhenAny(work, {{DELAY}}(idle, stopping));
      }
      """);

    await Assert.That(diagnostics).Count().IsEqualTo(1);
    await Assert.That(diagnostics[0].Id).IsEqualTo(RULE_ID);
    var message = diagnostics[0].GetMessage(CultureInfo.InvariantCulture);
    await Assert.That(message).Contains("WhenAny");
    await Assert.That(message).Contains("Delay");
    await Assert.That(message).Contains("WakeSet");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task AChannelWaitRaced_ReportsAsync() {
    var diagnostics = await _analyzeAsync("""
      static class C {
        static Task M(ChannelReader<int> reader, Task work, CancellationToken stopping) =>
          Task.WhenAny(reader.WaitToReadAsync(stopping).AsTask(), work);
      }
      """);

    await Assert.That(diagnostics).Count().IsEqualTo(1);
    await Assert.That(diagnostics[0].GetMessage(CultureInfo.InvariantCulture)).Contains("WaitToReadAsync");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task AWriterWaitAndAWaitOnADerivedReader_Raced_ReportAsync() {
    var diagnostics = await _analyzeAsync("""
      sealed class Counting(ChannelReader<int> inner) : ChannelReader<int> {
        public override bool TryRead(out int item) => inner.TryRead(out item);
        public override ValueTask<bool> WaitToReadAsync(CancellationToken token = default) => inner.WaitToReadAsync(token);
      }
      static class C {
        static Task M(Counting reader, ChannelWriter<int> writer, Task work, CancellationToken stopping) =>
          Task.WhenAny(work, reader.WaitToReadAsync(stopping).AsTask(), writer.WaitToWriteAsync(stopping).AsTask());
      }
      """);

    await Assert.That(diagnostics).Count().IsEqualTo(1);
  }

  /// <summary>The per-race token canceled once the race is decided was once accepted; no shape is accepted now.</summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task AWaitOnALinkedTokenTheMethodCancels_StillReportsAsync() {
    var diagnostics = await _analyzeAsync($$"""
      static class C {
        static async Task M(ChannelReader<int> reader, TimeSpan window, CancellationToken stopping) {
          using var race = CancellationTokenSource.CreateLinkedTokenSource(stopping);
          var arrival = reader.WaitToReadAsync(race.Token).AsTask();
          var deadline = {{DELAY}}(window, race.Token);
          await Task.WhenAny(arrival, deadline);
          await race.CancelAsync();
        }
      }
      """);

    await Assert.That(diagnostics).Count().IsEqualTo(1);
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task ASemaphoreWaitRaced_ReportsAsync() {
    var diagnostics = await _analyzeAsync("""
      static class C {
        static Task M(SemaphoreSlim shared, SemaphoreSlim reserve, CancellationToken token) =>
          Task.WhenAny(shared.WaitAsync(token), reserve.WaitAsync(1000, token));
      }
      """);

    await Assert.That(diagnostics).Count().IsEqualTo(1);
    await Assert.That(diagnostics[0].GetMessage(CultureInfo.InvariantCulture)).Contains("WaitAsync");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task ATaskCompletionSourcesTaskRaced_ReportsAsync() {
    var diagnostics = await _analyzeAsync("""
      static class C {
        static Task M(TaskCompletionSource signal, Task work) => Task.WhenAny(work, signal.Task);
        static Task N(TaskCompletionSource<bool> signal, Task work) => Task.WhenAny(work, signal.Task);
      }
      """);

    await Assert.That(diagnostics).Count().IsEqualTo(2);
    await Assert.That(diagnostics[0].GetMessage(CultureInfo.InvariantCulture)).Contains("Task");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task AWakeSignalWaitRaced_ReportsAsync() {
    var diagnostics = await _analyzeAsync("""
      static class C {
        static Task M(Whizbang.Core.Async.WakeSignal wake, Task work, CancellationToken token) =>
          Task.WhenAny(work, wake.WaitAsync(token));
      }
      """);

    await Assert.That(diagnostics).Count().IsEqualTo(1);
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task AMethodMarkedAsAWaitSource_Raced_ReportsAsync() {
    var diagnostics = await _analyzeAsync("""
      sealed class Gate : IGate {
        [Whizbang.Core.Async.WaitSource]
        public Task WaitForReadyAsync(CancellationToken token) => Task.CompletedTask;
        public Task DoWorkAsync(CancellationToken token) => Task.CompletedTask;
      }
      sealed class Derived : Base {
        public override Task WaitAsync(CancellationToken token) => Task.CompletedTask;
      }
      abstract class Base {
        [Whizbang.Core.Async.WaitSource]
        public abstract Task WaitAsync(CancellationToken token);
      }
      static class C {
        static Task ThroughTheInterface(IGate gate, Task work, CancellationToken token) =>
          Task.WhenAny(work, gate.WaitForReadyAsync(token));
        static Task ThroughTheClass(Gate gate, Task work, CancellationToken token) =>
          Task.WhenAny(work, gate.WaitForReadyAsync(token));
        static Task ThroughAnOverride(Derived waiter, Task work, CancellationToken token) =>
          Task.WhenAny(work, waiter.WaitAsync(token));
      }
      """);

    await Assert.That(diagnostics).Count().IsEqualTo(3);
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task AWaitThroughTaskAdapters_Raced_ReportsAsync() {
    var diagnostics = await _analyzeAsync($$"""
      static class C {
        static Task M(SemaphoreSlim gate, TimeSpan idle, Task work, CancellationToken token) =>
          Task.WhenAny(work, gate.WaitAsync(token).ContinueWith(_ => true, TaskScheduler.Default));
        static Task N(TimeSpan idle, Task work, CancellationToken token) =>
          Task.WhenAny(work, {{DELAY}}(idle, token).WaitAsync(token));
      }
      """);

    await Assert.That(diagnostics).Count().IsEqualTo(2);
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task AWaitChosenByAConditionalOrACoalesce_Raced_ReportsAsync() {
    var diagnostics = await _analyzeAsync($$"""
      static class C {
        static Task M(bool listening, Task signal, Task work, TimeSpan idle, CancellationToken token) =>
          Task.WhenAny(work, listening ? signal : {{DELAY}}(idle, token));
        static Task N(Task? signal, Task work, TimeSpan idle, CancellationToken token) =>
          Task.WhenAny(work, signal ?? {{DELAY}}(idle, token));
        static Task O(bool listening, Task signal, Task work, TimeSpan idle, CancellationToken token) =>
          Task.WhenAny(work, listening ? {{DELAY}}(idle, token) : signal);
        static Task P(TaskCompletionSource? signal, Task work) =>
          Task.WhenAny(work, signal?.Task ?? work);
        static Task Q(bool listening, Task? signal, Task work) =>
          Task.WhenAny(work, listening ? work : signal ?? work);
      }
      """);

    await Assert.That(diagnostics).Count().IsEqualTo(4);
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task AWaitHeldInALocal_AssignedOrInitialized_ThenRaced_ReportsAsync() {
    var diagnostics = await _analyzeAsync($$"""
      static class C {
        static async Task M(ChannelReader<int> reader, Task work, TimeSpan idle, CancellationToken token) {
          while (!token.IsCancellationRequested) {
            var wait = reader.WaitToReadAsync(token).AsTask();
            await Task.WhenAny(wait, work);
          }
        }
        static Task N(Task work, TimeSpan idle, CancellationToken token) {
          Task deadline;
          deadline = work;
          deadline = {{DELAY}}(idle, token);
          return Task.WhenAny(work, deadline);
        }
        static Task O(Task work, TaskCompletionSource signal) {
          Task? cached = null;
          cached ??= signal.Task;
          return Task.WhenAny(work, cached);
        }
      }
      """);

    await Assert.That(diagnostics).Count().IsEqualTo(3);
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task WaitsInAnArrayOrACollectionExpression_Raced_ReportAsync() {
    var diagnostics = await _analyzeAsync($$"""
      static class C {
        static Task M(Task work, TimeSpan idle, CancellationToken token) =>
          Task.WhenAny(new[] { work, {{DELAY}}(idle, token) });
        static Task N(Task work, TimeSpan idle, CancellationToken token) =>
          Task.WhenAny([work, {{DELAY}}(idle, token)]);
        static Task O(Task work, TimeSpan idle, CancellationToken token) {
          var both = new Task[] { work, {{DELAY}}(idle, token) };
          return Task.WhenAny(both);
        }
      }
      """);

    await Assert.That(diagnostics).Count().IsEqualTo(3);
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task AWaitRacedInWaitAny_ReportsAsync() {
    var diagnostics = await _analyzeAsync($$"""
      static class C {
        static int M(Task work, TimeSpan idle, CancellationToken token) => Task.WaitAny(work, {{DELAY}}(idle, token));
      }
      """);

    await Assert.That(diagnostics).Count().IsEqualTo(1);
    await Assert.That(diagnostics[0].GetMessage(CultureInfo.InvariantCulture)).Contains("WaitAny");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task AWaitRacedInsideALambda_ReportsAsync() {
    var diagnostics = await _analyzeAsync($$"""
      static class C {
        static Func<Task> M(Task work, TimeSpan idle, CancellationToken token) => () => {
          var deadline = {{DELAY}}(idle, token);
          return Task.WhenAny(work, deadline);
        };
      }
      """);

    await Assert.That(diagnostics).Count().IsEqualTo(1);
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task OnlyComputeRaced_OrTasksTheRuleCannotPlace_ReportsNothingAsync() {
    var diagnostics = await _analyzeAsync("""
      static class C {
        static Task Work(CancellationToken token) => Task.CompletedTask;
        static Task M(IGate gate, Task fromCaller, IEnumerable<Task> many, CancellationToken token) =>
          Task.WhenAny(Work(token), gate.DoWorkAsync(token), fromCaller);
        static Task N(IEnumerable<Task> many) => Task.WhenAny(many);
        static Task O(Task work) => Task.WhenAny(work);
        static Task Q(Task work, Task other) => Task.WhenAny(new[] { work, other });
        sealed class Holder { public Task Work { get; } = System.Threading.Tasks.Task.CompletedTask; }
        static Task S(Holder holder) => Task.WhenAny(holder.Work);
        static Task R() => Task.WhenAny(new Task[2]);
        static Task P(Task work) {
          var copy = work;
          copy = copy.ContinueWith(t => t, TaskScheduler.Default);
          return Task.WhenAny(copy);
        }
      }
      """);

    await Assert.That(diagnostics).IsEmpty();
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task AWaitNeverRaced_OrHandedToAnotherMethod_ReportsNothingAsync() {
    var diagnostics = await _analyzeAsync($$"""
      static class C {
        static async Task M(ChannelReader<int> reader, TimeSpan idle, CancellationToken token) {
          await reader.WaitToReadAsync(token);
          var deadline = {{DELAY}}(idle, token);
          await deadline;
          await Task.WhenAny(Wrap({{DELAY}}(idle, token)));
        }
        static Task Wrap(Task inner) => inner;
      }
      """);

    await Assert.That(diagnostics).IsEmpty();
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task LookalikesThatAreNotTheFrameworksWaits_ReportNothingAsync() {
    var diagnostics = await _analyzeAsync($$"""
      static class C {
        static Task WhenAny(Task a, Task b) => a;
        static ValueTask<bool> WaitToReadAsync(CancellationToken token) => ValueTask.FromResult(true);
        static Task Delay(TimeSpan due) => Task.CompletedTask;
        sealed class Source { public Task Task => System.Threading.Tasks.Task.CompletedTask; }
        sealed class Semaphore { public Task WaitAsync() => System.Threading.Tasks.Task.CompletedTask; }
        static Task AsTask(Task inner) => inner;
        static Task M(Task work, TimeSpan idle, CancellationToken token) =>
          WhenAny(work, {{DELAY}}(idle, token));
        static Task N(Task work, Source source, Semaphore semaphore, TimeSpan idle, CancellationToken token) =>
          Task.WhenAny(work, WaitToReadAsync(token).AsTask(), Delay(idle), source.Task, semaphore.WaitAsync(), AsTask(work));
      }
      """);

    await Assert.That(diagnostics).IsEmpty();
  }

  /// <summary>
  /// The one exemption: WakeSet's own source file, by its exact repository path, is where the race is written once.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task TheRaceInWakeSetsOwnFile_IsTheOneExemptionAsync() {
    var source = $$"""
      static class C {
        static Task M(Task work, TimeSpan idle, CancellationToken token) => Task.WhenAny(work, {{DELAY}}(idle, token));
      }
      """;

    var inWakeSet = await _analyzeAsync(source, WAKE_SET_PATH);
    var inWakeSetOnABuildServer = await _analyzeAsync(source, "/home/builds/whizbang/" + WAKE_SET_PATH);
    var inWakeSetOnWindows = await _analyzeAsync(source, @"C:\src\whizbang\src\Whizbang.Core\Async\WakeSet.cs");
    var besideWakeSet = await _analyzeAsync(source, "src/Whizbang.Core/Async/WakeSetHelpers.cs");
    var aWakeSetElsewhere = await _analyzeAsync(source, "src/Whizbang.Core/Workers/WakeSet.cs");
    var aNameEndingInWakeSet = await _analyzeAsync(source, "/home/builds/notsrc/Whizbang.Core/Async/WakeSet.cs");

    await Assert.That(inWakeSet).IsEmpty();
    await Assert.That(inWakeSetOnABuildServer).IsEmpty();
    await Assert.That(inWakeSetOnWindows).IsEmpty();
    await Assert.That(besideWakeSet).Count().IsEqualTo(1);
    await Assert.That(aWakeSetElsewhere).Count().IsEqualTo(1);
    await Assert.That(aNameEndingInWakeSet).Count().IsEqualTo(1);
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task WithoutTheChannelsAssembly_StillReportsTheOtherWaitsAsync() {
    var diagnostics = await RepoAnalyzerTestHelper.GetDiagnosticsAsync<HandWrittenWaitRaceAnalyzer>($$"""
      using System;
      using System.Threading;
      using System.Threading.Tasks;
      static class C {
        static Task M(Task work, TimeSpan idle, CancellationToken token) => Task.WhenAny(work, {{DELAY}}(idle, token));
      }
      """);

    await Assert.That(diagnostics).Count().IsEqualTo(1);
  }

  [RequiresAssemblyFiles()]
  private static async Task<IReadOnlyList<Diagnostic>> _analyzeAsync(string body, string path = "") {
    var source = USINGS + body;
    var compilationErrors = RepoAnalyzerTestHelper.CreateCompilation(source, withChannelsReference: true, path: path)
      .GetDiagnostics()
      .Where(d => d.Severity == DiagnosticSeverity.Error)
      .Select(d => d.ToString())
      .ToList();
    await Assert.That(compilationErrors).IsEmpty();

    return await RepoAnalyzerTestHelper.GetDiagnosticsAsync<HandWrittenWaitRaceAnalyzer>(
      source, withChannelsReference: true, path: path);
  }
}
