// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Attributes;
using Whizbang.Core.Component.Tests.Tangential.Owned;

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

public sealed class TgStampedCommandReceptor : IReceptor<TgStampedCommand> {
  public ValueTask HandleAsync(TgStampedCommand message, CancellationToken cancellationToken = default) {
    TangentialProbe.Saw("sentAt", message.Id, message.SentAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
    TangentialProbe.Saw("correlation", message.Id, message.CorrelationId);
    return ValueTask.CompletedTask;
  }
}
