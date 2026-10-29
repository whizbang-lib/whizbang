// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Sagas.Helpers;
using Whizbang.Sagas.Models;

namespace Whizbang.Sagas.Tests;

/// <summary>
/// The host-scoped per-item stream id deriver (#1888): each container derives under the namespace its own
/// <c>AddWhizbangSagas</c> configured, however many containers share the process.
/// </summary>
[Category("Unit")]
[Category("Saga")]
public class SagaItemStreamIdDeriverTests {
  private static readonly Guid _namespace = Guid.Parse("0b36f8d4-3884-4c3c-b92b-fc6ec74775ea");
  private static readonly Guid _sagaId = Guid.Parse("11111111-1111-1111-1111-111111111111");

  [Test]
  public async Task Of_DerivesUnderTheOptionsNamespaceAsync() {
    var deriver = new SagaItemStreamIdDeriver(new SagaOptions { PerItemStreamNamespace = _namespace });

    await Assert.That(deriver.NamespaceId).IsEqualTo(_namespace);
    await Assert.That(deriver.StreamIdOf(_sagaId, "item-1")).IsEqualTo(SagaItemStreams.Of(_namespace, _sagaId, "item-1"));
  }

  [Test]
  public async Task Constructor_WithoutOptions_ThrowsAsync() {
    await Assert.That(() => new SagaItemStreamIdDeriver(null!)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task TwoContainers_EachResolveADeriverForTheirOwnNamespaceAsync() {
    await using var configured = new ServiceCollection().AddWhizbangSagas(opts => opts.PerItemStreamNamespace = _namespace).BuildServiceProvider();
    await using var byDefault = new ServiceCollection().AddWhizbangSagas().BuildServiceProvider();

    var configuredDeriver = configured.GetRequiredService<ISagaItemStreamIdDeriver>();
    var defaultDeriver = byDefault.GetRequiredService<ISagaItemStreamIdDeriver>();

    await Assert.That(configuredDeriver.NamespaceId).IsEqualTo(_namespace)
      .Because("the container registered first keeps its own namespace after another registers without one");
    await Assert.That(defaultDeriver.NamespaceId).IsEqualTo(SagaItemStreams.DefaultNamespace);
    await Assert.That(configuredDeriver.StreamIdOf(_sagaId, "item-1")).IsEqualTo(SagaItemStreams.Of(_namespace, _sagaId, "item-1"));
  }

  [Test]
  public async Task For_OptionsAContainerRegistered_DeriveUnderThoseOptionsAsync() {
    await using var provider = new ServiceCollection().AddWhizbangSagas(opts => opts.PerItemStreamNamespace = _namespace).BuildServiceProvider();

    var deriver = SagaItemStreamIdDeriver.For(provider.GetRequiredService<SagaOptions>());

    await Assert.That(deriver.NamespaceId).IsEqualTo(_namespace)
      .Because("a saga service given its host's options derives under that host's namespace");
  }

  /// <summary>
  /// Options a service built itself, and no options at all, keep the process-wide namespace the framework used before
  /// derivation became host-scoped. Options made by hand carry the framework default unless set, and reading that as the
  /// host's namespace would move a service whose host configured one onto streams it never wrote.
  /// </summary>
  [Test]
  public async Task For_OptionsNotRegisteredByAContainer_OrNone_UseTheProcessWideNamespaceAsync() {
    var handMade = SagaItemStreamIdDeriver.For(new SagaOptions { MaxConsecutiveStalls = 9 });
    var none = SagaItemStreamIdDeriver.For(null);

    await Assert.That(handMade).IsSameReferenceAs(SagaItemStreamIdDeriver.ProcessWide);
    await Assert.That(none).IsSameReferenceAs(SagaItemStreamIdDeriver.ProcessWide);
  }

  /// <summary>
  /// The process-wide deriver is bound to no options: it derives under whatever the process-wide namespace is when it
  /// is called. That value is shared with every test that registers sagas, so this asserts only what holds whichever
  /// value it reads: the id it derives is the one its reported namespace gives.
  /// </summary>
  [Test]
  public async Task ProcessWide_DerivesUnderTheNamespaceItReportsAsync() {
    var deriver = SagaItemStreamIdDeriver.ProcessWide;

    var namespaceId = deriver.NamespaceId;
    var derived = deriver.StreamIdOf(_sagaId, "item-1");

    await Assert.That(derived == SagaItemStreams.Of(namespaceId, _sagaId, "item-1") || deriver.NamespaceId != namespaceId).IsTrue();
  }

  [Test]
  public async Task Reconciler_WithADeriver_ChecksTheStreamsItDerivesAsync() {
    var deriver = new SagaItemStreamIdDeriver(new SagaOptions { PerItemStreamNamespace = _namespace });
    var reader = new KeyedReader(SagaItemStreams.Of(_namespace, _sagaId, "a"));
    IReadOnlyList<SagaItemModel> items = [new SagaItemModel { SagaId = _sagaId, SagaName = "s", ItemIdentifier = "a", State = SagaItemState.Running }];

    var reconciled = await SagaItemCompletionReconciler.ReconcileTerminalCountsAsync(_sagaId, items, reader, deriver, CancellationToken.None);
    var resolved = await SagaItemCompletionReconciler.ResolveCompletionCountsAsync(
      _sagaId, 1, new SagaItemAggregate(Total: 1, Completed: 0, Failed: 0, InProgress: 1),
      _ => Task.FromResult(items), reader, deriver, CancellationToken.None);

    await Assert.That(reconciled).IsEqualTo((1, 0));
    await Assert.That(resolved).IsEqualTo((1, 0));
  }

  [Test]
  public async Task Reconciler_WithoutADeriver_ThrowsAsync() {
    IReadOnlyList<SagaItemModel> items = [];
    await Assert.That(async () => await SagaItemCompletionReconciler.ReconcileTerminalCountsAsync(
      _sagaId, items, new KeyedReader(Guid.Empty), null!, CancellationToken.None)).Throws<ArgumentNullException>();
  }

  private sealed class KeyedReader(Guid completed) : ISagaItemTerminalReader {
    public Task<SagaItemTerminalOutcome> CheckAsync(Guid perItemStreamId, CancellationToken cancellationToken)
      => Task.FromResult(perItemStreamId == completed ? SagaItemTerminalOutcome.Completed : SagaItemTerminalOutcome.NotTerminal);
  }
}
