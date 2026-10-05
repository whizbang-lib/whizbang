// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Observability;
using Whizbang.Core.Tests.Generated;
using Whizbang.Core.Tests.Observability;

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>
/// What an operator sees when the dispatcher runs with tracing and metrics switched on: every dispatch
/// overload starts a "Dispatch &lt;type&gt;" span carrying the message type, message id and correlation
/// id; a send is counted as dispatched under the "send" pattern with its duration recorded; a failed
/// send is counted as an error tagged with the exception type. Without a listener and without
/// registered metrics the same calls must still succeed, which the rest of the dispatcher suite proves;
/// these tests cover the instrumented arm.
/// </summary>
/// <remarks>
/// The messages are this class's own, so spans and measurements from other tests (an ActivityListener
/// is process-wide) can never be mistaken for these, and the cases run one at a time so a case only
/// ever sees its own spans. Metrics go to a meter factory of the test's own.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Dispatcher.cs</code-under-test>
[NotInParallel(nameof(DispatcherObservabilityTests))]
public class DispatcherObservabilityTests {
  public record ObservedCommand(Guid Id);
  public record ObservedResult(Guid Id);
  public record ObservedFailingCommand(Guid Id);

  public sealed class ObservedReceptor : IReceptor<ObservedCommand, ObservedResult> {
    public ValueTask<ObservedResult> HandleAsync(ObservedCommand message, CancellationToken cancellationToken = default) =>
      ValueTask.FromResult(new ObservedResult(message.Id));
  }

  public sealed class ObservedFailingReceptor : IReceptor<ObservedFailingCommand, ObservedResult> {
    public ValueTask<ObservedResult> HandleAsync(ObservedFailingCommand message, CancellationToken cancellationToken = default) =>
      throw new TimeoutException("the downstream call timed out");
  }

  private sealed class Instrumented : IDisposable {
    private readonly ActivityListener _listener;
    private readonly List<Activity> _stopped = [];
    private readonly TestMeterFactory _meters = new();
    public MetricAssertionHelper Metrics { get; }
    public IDispatcher Dispatcher { get; }

    public Instrumented() {
      _listener = new ActivityListener {
        ShouldListenTo = s => s.Name == "Whizbang.Execution",
        Sample = (ref _) => ActivitySamplingResult.AllDataAndRecorded,
        ActivityStopped = a => {
          if (a.OperationName.StartsWith("Dispatch Observed", StringComparison.Ordinal)) {
            lock (_stopped) { _stopped.Add(a); }
          }
        }
      };
      ActivitySource.AddActivityListener(_listener);

      var services = new ServiceCollection();
      services.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()));
      services.AddSingleton(new DispatcherMetrics(new WhizbangMetrics(_meters)));
      services.AddReceptors();
      services.AddWhizbangDispatcher();
      Dispatcher = services.BuildServiceProvider().GetRequiredService<IDispatcher>();
      Metrics = new MetricAssertionHelper([.. _meters.CreatedMeters]);
    }

    /// <summary>The one span this test's dispatch produced for the message id (or, unfiltered, for the type).</summary>
    public Activity SpanFor(string operation, string? messageId = null) {
      lock (_stopped) {
        return _stopped.Single(a => a.OperationName == operation
                                 && (messageId is null || Equals(a.GetTagItem("whizbang.message.id"), messageId)));
      }
    }

    public double Counted(string instrument, string tag, string value) =>
      Metrics.GetByName(instrument).Where(m => m.Tags.GetValueOrDefault(tag) == value).Sum(m => m.Value);

    public void Dispose() {
      _listener.Dispose();
      Metrics.Dispose();
      _meters.Dispose();
    }
  }

  private static async Task _assertSpanAsync(Activity span, string? correlationId = null) {
    await Assert.That((string?)span.GetTagItem("whizbang.message.type")).Contains("Observed");
    await Assert.That(span.GetTagItem("whizbang.message.id")).IsNotNull();
    if (correlationId is not null) {
      await Assert.That(span.GetTagItem("whizbang.correlation.id")).IsEqualTo(correlationId);
    } else {
      await Assert.That(span.GetTagItem("whizbang.correlation.id")).IsNotNull();
    }
  }

  private static async Task _assertSendCountedAsync(Instrumented instrumented) {
    await Assert.That(instrumented.Counted("whizbang.dispatcher.messages_dispatched", "pattern", "send")).IsEqualTo(1)
      .Because("one send is one dispatched message, under the send pattern");
    await Assert.That(instrumented.Metrics.GetByName("whizbang.dispatcher.send.duration")).Count().IsEqualTo(1);
  }

  [Test]
  public async Task SendAsync_WithContext_TracesAndCountsTheSendAsync() {
    using var instrumented = new Instrumented();
    var context = MessageContext.New();

    var receipt = await instrumented.Dispatcher.SendAsync(new ObservedCommand(Guid.CreateVersion7()), context);

    await _assertSpanAsync(instrumented.SpanFor("Dispatch ObservedCommand", receipt.MessageId.ToString()), context.CorrelationId.ToString());
    await _assertSendCountedAsync(instrumented);
  }

  [Test]
  public async Task SendAsync_WithContextAndOptions_TracesAndCountsTheSendAsync() {
    using var instrumented = new Instrumented();
    var context = MessageContext.New();

    var receipt = await instrumented.Dispatcher.SendAsync(new ObservedCommand(Guid.CreateVersion7()), context, new DispatchOptions());

    await _assertSpanAsync(instrumented.SpanFor("Dispatch ObservedCommand", receipt.MessageId.ToString()), context.CorrelationId.ToString());
    await _assertSendCountedAsync(instrumented);
  }

  [Test]
  public async Task SendAsync_GenericWithOptions_TracesAndCountsTheSendAsync() {
    using var instrumented = new Instrumented();

    var receipt = await instrumented.Dispatcher.SendAsync(new ObservedCommand(Guid.CreateVersion7()), new DispatchOptions());

    await _assertSpanAsync(instrumented.SpanFor("Dispatch ObservedCommand", receipt.MessageId.ToString()));
    await _assertSendCountedAsync(instrumented);
  }

  /// <summary>A failed send is counted once as an error, tagged with what failed, and rethrown.</summary>
  [Test]
  [Arguments("context")]
  [Arguments("context+options")]
  [Arguments("generic")]
  [Arguments("generic+options")]
  public async Task SendAsync_ReceptorThrows_CountsTheErrorByExceptionTypeAsync(string overload) {
    using var instrumented = new Instrumented();
    var message = new ObservedFailingCommand(Guid.CreateVersion7());

    Func<Task> send = overload switch {
      "context" => () => instrumented.Dispatcher.SendAsync(message, MessageContext.New()),
      "context+options" => () => instrumented.Dispatcher.SendAsync(message, MessageContext.New(), new DispatchOptions()),
      "generic" => () => instrumented.Dispatcher.SendAsync(message),
      _ => () => instrumented.Dispatcher.SendAsync(message, new DispatchOptions())
    };

    await Assert.That(send).ThrowsExactly<TimeoutException>();
    await Assert.That(instrumented.Counted("whizbang.dispatcher.errors", "error_type", nameof(TimeoutException))).IsEqualTo(1);
    await Assert.That(instrumented.Counted("whizbang.dispatcher.messages_dispatched", "pattern", "send")).IsEqualTo(0)
      .Because("a send that failed was not dispatched");
  }

  [Test]
  [Arguments("generic")]
  [Arguments("object")]
  [Arguments("generic+context")]
  [Arguments("object+options")]
  public async Task LocalInvokeAsync_WithResult_TracesTheInvocationAsync(string overload) {
    using var instrumented = new Instrumented();
    var message = new ObservedCommand(Guid.CreateVersion7());

    var result = overload switch {
      "generic" => await instrumented.Dispatcher.LocalInvokeAsync<ObservedCommand, ObservedResult>(message),
      "object" => await instrumented.Dispatcher.LocalInvokeAsync<ObservedResult>(message),
      "generic+context" => await instrumented.Dispatcher.LocalInvokeAsync<ObservedCommand, ObservedResult>(message, MessageContext.New()),
      _ => await instrumented.Dispatcher.LocalInvokeAsync<ObservedResult>(message, new DispatchOptions())
    };

    await Assert.That(result.Id).IsEqualTo(message.Id);
    await _assertSpanAsync(instrumented.SpanFor("Dispatch ObservedCommand"));
  }

  [Test]
  [Arguments("generic")]
  [Arguments("object+options")]
  public async Task LocalInvokeWithReceiptAsync_TracesTheInvocationAsync(string overload) {
    using var instrumented = new Instrumented();
    var message = new ObservedCommand(Guid.CreateVersion7());

    var invoked = overload == "generic"
      ? await instrumented.Dispatcher.LocalInvokeWithReceiptAsync<ObservedCommand, ObservedResult>(message)
      : await instrumented.Dispatcher.LocalInvokeWithReceiptAsync<ObservedResult>(message, new DispatchOptions());

    await Assert.That(invoked.Value.Id).IsEqualTo(message.Id);
    await _assertSpanAsync(instrumented.SpanFor("Dispatch ObservedCommand", invoked.Receipt.MessageId.ToString()));
  }
}
