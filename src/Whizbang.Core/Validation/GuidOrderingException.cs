// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Validation;

/// <summary>
/// Thrown when an id that must be time-ordered (UUIDv7) is refused: an event or message id at an entry point, at
/// <see cref="Configuration.GuidOrderingSeverity.Error"/> (the default) or empty at any severity, or a stream id when
/// stream ids are raised to <see cref="Configuration.GuidOrderingSeverity.Error"/> (#2528).
/// </summary>
/// <docs>fundamentals/identity/whizbang-ids#time-ordering-checks</docs>
/// <tests>tests/Whizbang.Core.Tests/Validation/GuidOrderingExceptionTests.cs</tests>
public sealed class GuidOrderingException : Exception {
  /// <summary>
  /// Creates a new GuidOrderingException.
  /// </summary>
  public GuidOrderingException() { }

  /// <summary>
  /// Creates a new GuidOrderingException.
  /// </summary>
  /// <param name="message">The error message.</param>
  public GuidOrderingException(string message) : base(message) { }

  /// <summary>
  /// Creates a new GuidOrderingException with inner exception.
  /// </summary>
  /// <param name="message">The error message.</param>
  /// <param name="innerException">The inner exception.</param>
  public GuidOrderingException(string message, Exception innerException)
      : base(message, innerException) { }

  /// <summary>
  /// Creates the exception for a refused id, naming it, its message type and the entry point that refused it.
  /// </summary>
  /// <param name="id">The refused id.</param>
  /// <param name="messageType">The type of the message the id belongs to.</param>
  /// <param name="entryPoint">The entry point that refused it.</param>
  /// <param name="message">The error message.</param>
  public GuidOrderingException(Guid id, string messageType, string entryPoint, string message) : base(message) {
    Id = id;
    MessageType = messageType;
    EntryPoint = entryPoint;
  }

  /// <summary>The refused id, or <see cref="Guid.Empty"/> when the exception was not raised for one id.</summary>
  public Guid Id { get; }

  /// <summary>The type of the message the refused id belongs to, or null.</summary>
  public string? MessageType { get; }

  /// <summary>The entry point that refused the id, or null.</summary>
  public string? EntryPoint { get; }

  /// <summary>The version of the refused id as received (0 for the empty id).</summary>
  public int Version => Id.Version;
}
