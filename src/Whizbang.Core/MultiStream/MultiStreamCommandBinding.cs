// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;

namespace Whizbang.Core.MultiStream;

/// <summary>The binding for one command type. See <see cref="IMultiStreamCommandBinding"/>.</summary>
/// <typeparam name="TCommand">The command type.</typeparam>
/// <docs>fundamentals/dispatcher/multi-stream-commands#registration</docs>
/// <tests>tests/Whizbang.Core.Tests/MultiStream/MultiStreamCommandRegistrationTests.cs:InvokeAsync_ReadsThePayloadBackAndCallsTheHandlerForThatStreamAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/MultiStream/MultiStreamCommandRegistrationTests.cs:InvokeAsync_APayloadThatReadsAsNull_ThrowsAsync</tests>
/// <tests>tests/Whizbang.Core.Component.Tests/MultiStream/MultiStreamCommandReceptorRegistrarTests.cs:StartAsync_RegistersTheIntakeAtTheDefaultReceptorStagesAsync</tests>
public sealed class MultiStreamCommandBinding<TCommand> : IMultiStreamCommandBinding where TCommand : IMultiStreamCommand {
  /// <inheritdoc/>
  public string CommandType { get; } = MultiStreamCommandNames.Of(typeof(TCommand));

  /// <inheritdoc/>
  public async ValueTask InvokeAsync(IServiceProvider services, string payload, Guid streamId, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(services);
    var command = _readBack(payload);
    var handler = services.GetRequiredService<IMultiStreamCommandHandler<TCommand>>();
    await handler.HandleStreamAsync(command, streamId, cancellationToken).ConfigureAwait(false);
  }

  // The last payload read back and its command. The streams of a run share its payload, so a run is read back once,
  // not once per stream; a payload read per stream made a large run's cost grow with its stream count.
  private sealed record ReadBack(string Payload, TCommand Command);

  private ReadBack? _lastReadBack;

  private TCommand _readBack(string payload) {
    var last = Volatile.Read(ref _lastReadBack);
    if (last is not null && (ReferenceEquals(last.Payload, payload) || string.Equals(last.Payload, payload, StringComparison.Ordinal))) {
      return last.Command;
    }
    var command = MultiStreamJson.Deserialize<TCommand>(payload)
      ?? throw new InvalidOperationException($"The stored payload of a {CommandType} run reads back as nothing; the stream cannot be worked.");
    MessageParseDiagnostics.Record(command.RunId, MessageParseSource.MultiStreamRun, MessageParseKind.Payload);
    Volatile.Write(ref _lastReadBack, new ReadBack(payload, command));
    return command;
  }

  /// <inheritdoc/>
  public void RegisterIntake(IReceptorRegistry registry, IServiceProvider services) {
    ArgumentNullException.ThrowIfNull(registry);
    ArgumentNullException.ThrowIfNull(services);
    var intake = new MultiStreamCommandIntake<TCommand>(
      services.GetRequiredService<IMultiStreamCommandStore>(),
      services.GetService<ILogger<MultiStreamCommandIntake<TCommand>>>() ?? NullLogger<MultiStreamCommandIntake<TCommand>>.Instance);
    registry.Register(intake, LifecycleStage.LocalImmediateInline);
    registry.Register(intake, LifecycleStage.PreOutboxInline);
    registry.Register(intake, LifecycleStage.PostInboxInline);
  }
}
