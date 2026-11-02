// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Options;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Rejects <see cref="EventStoreMetadataOptions"/> the write paths cannot act on, when the host starts: a negative
/// staging window, or a write form that is not one of the three.
/// </summary>
/// <docs>data/event-store#stored-metadata</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/EventStoreMetadataSettingsTests.cs:Validator_FailsOnAnInvalidValue_AndPassesTheDefaultsAsync</tests>
public sealed class EventStoreMetadataOptionsValidator : IValidateOptions<EventStoreMetadataOptions> {
  /// <inheritdoc />
  public ValidateOptionsResult Validate(string? name, EventStoreMetadataOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    var failures = EventStoreMetadataSettings.Validate(options);
    return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
  }
}
