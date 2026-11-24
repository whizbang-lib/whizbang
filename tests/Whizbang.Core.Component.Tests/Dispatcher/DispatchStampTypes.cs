// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Attributes;
using Whizbang.Core.Component.Tests.Tangential.Owned;
using Whizbang.Core.Observability;

// For DispatchTangentialParityComponentTests: a command another service owns, so a send runs it here and
// also writes its copy.

namespace Whizbang.Core.Component.Tests.Tangential.Unowned;

/// <summary>A command whose dispatch stamps its sent time and correlation onto it, as a consumer's base command declares.</summary>
public sealed class TgStampedCommand : ICommand {
  public Guid Id { get; init; }

  [PopulateTimestamp(TimestampKind.SentAt)]
  public DateTimeOffset SentAt { get; set; }

  [PopulateFromIdentifier(IdentifierKind.CorrelationId)]
  public string? CorrelationId { get; set; }
}

public sealed class TgStampedCommandReceptor(IEnvelopeRegistry registry) : IReceptor<TgStampedCommand> {
  public ValueTask HandleAsync(TgStampedCommand message, CancellationToken cancellationToken = default) {
    TangentialProbe.Saw("sentAt", message.Id, message.SentAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
    TangentialProbe.Saw("correlation", message.Id, message.CorrelationId);
    TangentialProbe.Saw("envelopeId", message.Id, registry.TryGetEnvelope(message)?.MessageId.Value.ToString());
    return ValueTask.CompletedTask;
  }
}

/// <summary>
/// A command another service owns whose stream the dispatcher generates on every send (the default
/// <c>OnlyIfEmpty = false</c>), so a send runs it here and also writes its copy (#2539).
/// </summary>
[GenerateStreamId]
public sealed class TgGeneratedStreamCommand : ICommand {
  [StreamId]
  public Guid StreamId { get; set; }

  public required Guid Marker { get; init; }
}

public sealed class TgGeneratedStreamCommandReceptor : IReceptor<TgGeneratedStreamCommand> {
  public ValueTask HandleAsync(TgGeneratedStreamCommand message, CancellationToken cancellationToken = default) {
    TangentialProbe.Saw("commandStream", message.Marker, message.StreamId.ToString());
    return ValueTask.CompletedTask;
  }
}
