// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.Security.Extractors;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Security;

/// <summary>
/// Branch backfill for <see cref="MessageHopSecurityExtractor"/>: built by hand with no logger, the
/// extractor falls back to a null logger and still extracts, including on its logging paths.
/// </summary>
public class MessageHopSecurityExtractorBranchCoverageTests {

  private sealed record TestMessage(string Value);

  [Test]
  public async Task ExtractAsync_ConstructedWithoutALogger_StillExtractsTheHopScopeAsync() {
    var extractor = new MessageHopSecurityExtractor(logger: null!);
    var envelope = _envelope(ScopeDelta.FromSecurityContext(new SecurityContext { TenantId = "tenant-123", UserId = "user-456" }));

    var result = await extractor.ExtractAsync(envelope, new MessageSecurityOptions(), CancellationToken.None);

    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Scope.TenantId).IsEqualTo("tenant-123");
    await Assert.That(result.Scope.UserId).IsEqualTo("user-456");
  }

  [Test]
  public async Task ExtractAsync_ConstructedWithoutALogger_NoScope_ReturnsNullAsync() {
    var extractor = new MessageHopSecurityExtractor(logger: null!);

    var result = await extractor.ExtractAsync(_envelope(scope: null), new MessageSecurityOptions(), CancellationToken.None);

    await Assert.That(result).IsNull()
      .Because("the no-scope path logs before returning; with the null-logger fallback that logging cannot fail");
  }

  private static MessageEnvelope<TestMessage> _envelope(ScopeDelta? scope) => new() {
    MessageId = MessageId.New(),
    Payload = new TestMessage("test-payload"),
    Hops = [
      new MessageHop {
        Type = HopType.Current,
        ServiceInstance = ServiceInstanceInfo.Unknown,
        Timestamp = DateTimeOffset.UtcNow,
        Scope = scope,
      },
    ],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
  };
}
