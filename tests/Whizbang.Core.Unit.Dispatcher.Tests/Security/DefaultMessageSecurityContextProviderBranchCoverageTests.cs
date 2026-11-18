// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Security;

/// <summary>
/// Branch backfill for <see cref="DefaultMessageSecurityContextProvider"/>'s extraction timeout
/// filter: a caller cancellation that arrives while an extractor is running surfaces as a
/// cancellation, never as a timeout.
/// </summary>
[Category("Security")]
public class DefaultMessageSecurityContextProviderBranchCoverageTests {

  private sealed record TestMessage(string Value);

  [Test]
  public async Task EstablishContextAsync_CallerCancelsDuringExtraction_PropagatesCancellationNotTimeoutAsync() {
    using var callerCts = new CancellationTokenSource();
    var extractor = new CancelingExtractor(callerCts);
    var provider = new DefaultMessageSecurityContextProvider(
      extractors: [extractor],
      callbacks: [],
      options: new MessageSecurityOptions { AllowAnonymous = false, Timeout = TimeSpan.FromMinutes(5) });
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    await using var sp = services.BuildServiceProvider();
    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage("test"),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
    };

    await Assert.That(async () => await provider.EstablishContextAsync(envelope, sp, callerCts.Token))
      .ThrowsExactly<OperationCanceledException>()
      .Because("the timeout translation applies only when the timeout fired; a caller cancellation must stay a cancellation");
    await Assert.That(extractor.Entered).IsTrue()
      .Because("precondition: the cancellation arrived inside the extraction, past the up-front cancellation check");
  }

  /// <summary>Cancels the caller's token from inside the extraction, then observes it.</summary>
  private sealed class CancelingExtractor(CancellationTokenSource callerCts) : ISecurityContextExtractor {
    public bool Entered { get; private set; }
    public int Priority => 100;

    public async ValueTask<SecurityExtraction?> ExtractAsync(
        IMessageEnvelope envelope, MessageSecurityOptions options, CancellationToken cancellationToken = default) {
      Entered = true;
      await callerCts.CancelAsync();
      cancellationToken.ThrowIfCancellationRequested();
      return null;
    }
  }
}
