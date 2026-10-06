// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Offloads;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Offloads;

/// <summary>
/// Branch backfill for <see cref="BodyClaimRehydrator"/>: an <c>IOptions</c> registration whose
/// <c>Value</c> is null falls back to the default 100-second download timeout.
/// </summary>
public class BodyClaimRehydratorBranchCoverageTests {

  [Test]
  public async Task MaybeRehydrateAsync_OptionsValueNull_UsesTheDefaultDownloadTimeoutAsync() {
    var services = new ServiceCollection();
    services.AddKeyedSingleton<IMessageBodyStore>("memory", (_, _) => new ThrowingStore("memory"));
    services.AddSingleton<IOptions<MessageBodyOffloadOptions>>(new NullValueOptions());
    await using var sp = services.BuildServiceProvider();
    var claim = new MessageBodyClaim(
      ProviderName: "memory", StorageKey: "test://null-options",
      Size: 4, ContentHash: "sha256-AB",
      ContentType: "application/json", UploadedAt: DateTimeOffset.UtcNow);
    var claimEnvelope = _wrapInClaimEnvelope(claim);

    // A pre-canceled caller token cancels the linked download token, so a NON-cancellation store
    // failure takes the timeout wording, which names the timeout the rehydrator resolved.
    using var cts = new CancellationTokenSource();
    await cts.CancelAsync();

    var ex = await Assert.That(async () => await BodyClaimRehydrator.MaybeRehydrateAsync(
        claimEnvelope, claimEnvelope.GetType().AssemblyQualifiedName,
        Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions(), sp, cts.Token))
      .Throws<BodyClaimDownloadException>();
    await Assert.That(ex!.Message).Contains("exceeded the 100s download timeout")
      .Because("a registered options accessor with a null Value must fall back to the 100-second default, not dereference null");
  }

  private static MessageEnvelope<BodyClaimEnvelopePayload> _wrapInClaimEnvelope(MessageBodyClaim claim) {
    var sentinel = new BodyClaimEnvelopePayload(claim, "application/json", "OriginalType, MyAssembly");
    return new MessageEnvelope<BodyClaimEnvelopePayload> {
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
      MessageId = MessageId.New(),
      Payload = sentinel,
      Hops = [new MessageHop { Type = HopType.Current, Timestamp = DateTimeOffset.UtcNow, ServiceInstance = ServiceInstanceInfo.Unknown }],
    };
  }

  private sealed class NullValueOptions : IOptions<MessageBodyOffloadOptions> {
    public MessageBodyOffloadOptions Value => null!;
  }

  private sealed class ThrowingStore(string providerName) : IMessageBodyStore {
    public string ProviderName { get; } = providerName;
    public Task<MessageBodyClaim> UploadAsync(ReadOnlyMemory<byte> body, string contentType, MessageBodyUploadOptions? options = null, CancellationToken cancellationToken = default)
      => Task.FromResult(new MessageBodyClaim(ProviderName, "k", body.Length, "sha256-X", contentType, DateTimeOffset.UtcNow));
    public Task<ReadOnlyMemory<byte>> DownloadAsync(MessageBodyClaim claim, MessageBodyDownloadOptions? options = null, CancellationToken cancellationToken = default)
      => throw new InvalidOperationException("simulated provider failure");
    public Task DeleteAsync(MessageBodyClaim claim, MessageBodyDeleteOptions? options = null, CancellationToken cancellationToken = default)
      => Task.CompletedTask;
  }
}
