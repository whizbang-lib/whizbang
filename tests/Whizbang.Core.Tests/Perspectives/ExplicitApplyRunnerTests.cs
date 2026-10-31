// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>A ticket was opened.</summary>
public sealed record ExplicitTicketOpened : IEvent {
  [StreamId]
  public Guid TicketId { get; init; }
  public string Title { get; init; } = "";
}

/// <summary>A ticket was renamed.</summary>
public sealed record ExplicitTicketRenamed : IEvent {
  [StreamId]
  public Guid TicketId { get; init; }
  public string Title { get; init; } = "";
}

/// <summary>The ticket row.</summary>
public sealed record ExplicitTicket {
  [StreamId]
  public Guid Id { get; init; }
  public string Title { get; init; } = "";
}

/// <summary>Implements every Apply explicitly: the interfaces are the only way in.</summary>
public sealed class ExplicitTicketPerspective :
    IPerspectiveFor<ExplicitTicket, ExplicitTicketOpened>,
    IPerspectiveWithActionsFor<ExplicitTicket, ExplicitTicketRenamed> {
  ExplicitTicket IPerspectiveFor<ExplicitTicket, ExplicitTicketOpened>.Apply(ExplicitTicket currentData, ExplicitTicketOpened eventData) =>
    currentData with { Id = eventData.TicketId, Title = eventData.Title };

  ApplyResult<ExplicitTicket> IPerspectiveWithActionsFor<ExplicitTicket, ExplicitTicketRenamed>.Apply(
      ExplicitTicket currentData, ExplicitTicketRenamed eventData) =>
    currentData with { Title = eventData.Title };
}

/// <summary>
/// The generated runner calls each <c>Apply</c> through the interface that lists its event (#1936), so a perspective
/// that implements <c>Apply</c> only explicitly builds and runs like any other.
/// </summary>
/// <docs>fundamentals/perspectives/perspectives</docs>
public class ExplicitApplyRunnerTests {
  [Test]
  public async Task APerspectiveThatImplementsApplyOnlyExplicitly_IsRunThroughItsInterfacesAsync() {
    var events = new InMemoryEventStore();
    var rows = new InMemoryRows<ExplicitTicket>();
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<ExplicitTicketPerspective>();
    var runner = GeneratedRunners.Create(
      "Whizbang.Core.Tests.Generated.ExplicitTicketPerspectiveRunner", services.BuildServiceProvider(), events, rows);
    var ticketId = TrackedGuid.New().Value;
    await events.AppendAsync(ticketId, GeneratedRunners.Envelope(new ExplicitTicketOpened { TicketId = ticketId, Title = "draft" }));
    await events.AppendAsync(ticketId, GeneratedRunners.Envelope(new ExplicitTicketRenamed { TicketId = ticketId, Title = "final" }));

    await runner.RunAsync(ticketId, nameof(ExplicitTicketPerspective), lastProcessedEventId: null, CancellationToken.None);

    await Assert.That(rows.Row(ticketId).Title).IsEqualTo("final")
      .Because("both explicit implementations ran, the IPerspectiveFor one and then the IPerspectiveWithActionsFor one");
    await Assert.That(rows.Row(ticketId).Id).IsEqualTo(ticketId);
  }
}
