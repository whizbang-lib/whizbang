// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Whizbang.Core.Messaging;

// Types for MediatorResultLookupComponentTests (#1418). Each command's receptor returns a value that is
// neither a command nor an event and has no receptor of its exact type; the tests show it reaches the
// receptors of an interface it implements, as a mediator would. The namespace is the service's own
// domain, so an unhandled result is refused rather than sent elsewhere.

namespace Whizbang.Core.Component.Tests.MediatorLookup.Owned;

/// <summary>The receptors that ran, per id. Each test uses a fresh id.</summary>
public static class MediatorProbe {
  private static readonly ConcurrentDictionary<Guid, ConcurrentQueue<string>> _ran = new();

  public static void Record(Guid id, string receptor) => _ran.GetOrAdd(id, static _ => new()).Enqueue(receptor);

  public static string[] Ran(Guid id) => _ran.TryGetValue(id, out var q) ? [.. q] : [];
}

/// <summary>An answer only another assembly's receptor is declared for (a fake lookup stands in for it).</summary>
public interface IMdForeignAnswer : IMessage {
  Guid Id { get; }
}

public record MdForeignAnswer(Guid Id) : IMdForeignAnswer;

/// <summary>An answer only a runtime registration handles, through this interface.</summary>
public interface IMdRuntimeAnswer : IMessage {
  Guid Id { get; }
}

public record MdRuntimeAnswer(Guid Id) : IMdRuntimeAnswer;

/// <summary>An answer nothing handles, by any type.</summary>
public record MdUnhandledAnswer(Guid Id) : IMessage;

public record MdAskForForeignAnswer(Guid Id) : ICommand;

public record MdAskForRuntimeAnswer(Guid Id) : ICommand;

public record MdAskForUnhandledAnswer(Guid Id) : ICommand;

public sealed class MdAskForForeignAnswerReceptor : IReceptor<MdAskForForeignAnswer, MdForeignAnswer> {
  public ValueTask<MdForeignAnswer> HandleAsync(MdAskForForeignAnswer message, CancellationToken cancellationToken = default) =>
    ValueTask.FromResult(new MdForeignAnswer(message.Id));
}

public sealed class MdAskForRuntimeAnswerReceptor : IReceptor<MdAskForRuntimeAnswer, MdRuntimeAnswer> {
  public ValueTask<MdRuntimeAnswer> HandleAsync(MdAskForRuntimeAnswer message, CancellationToken cancellationToken = default) =>
    ValueTask.FromResult(new MdRuntimeAnswer(message.Id));
}

public sealed class MdAskForUnhandledAnswerReceptor : IReceptor<MdAskForUnhandledAnswer, MdUnhandledAnswer> {
  public ValueTask<MdUnhandledAnswer> HandleAsync(MdAskForUnhandledAnswer message, CancellationToken cancellationToken = default) {
    MediatorProbe.Record(message.Id, "asked");
    return ValueTask.FromResult(new MdUnhandledAnswer(message.Id));
  }
}

/// <summary>
/// Registered at runtime for the interface. Generic, so the generator does not discover it, as the other
/// runtime receptors in these tests are.
/// </summary>
public sealed class MdRuntimeInterfaceReceptor<TAnswer> : IReceptor<TAnswer> where TAnswer : IMdRuntimeAnswer {
  public ValueTask HandleAsync(TAnswer message, CancellationToken cancellationToken = default) {
    MediatorProbe.Record(message.Id, "runtime-interface-receptor");
    return ValueTask.CompletedTask;
  }
}
