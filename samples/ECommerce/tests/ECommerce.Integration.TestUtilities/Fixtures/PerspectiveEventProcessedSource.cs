// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Workers;

namespace ECommerce.Integration.TestUtilities.Fixtures;

/// <summary>
/// Something that reports perspective applies the way <see cref="PerspectiveWorker.OnPerspectiveEventProcessed"/> does:
/// a host's perspective worker, or a fake in a test of the waits built on it (#2237).
/// </summary>
public interface IPerspectiveEventProcessedSource {
  /// <summary>Raised once per (perspective, stream) batch, naming the events it reports.</summary>
  event PerspectiveEventProcessedHandler? OnPerspectiveEventProcessed;
}

/// <summary>A host's <see cref="PerspectiveWorker"/> as an <see cref="IPerspectiveEventProcessedSource"/>.</summary>
public sealed class PerspectiveWorkerEventSource : IPerspectiveEventProcessedSource {
  private readonly PerspectiveWorker _worker;

  private PerspectiveWorkerEventSource(PerspectiveWorker worker) {
    _worker = worker;
  }

  /// <summary>The worker as a source, or <see langword="null"/> when the host runs none.</summary>
  public static IPerspectiveEventProcessedSource? Of(PerspectiveWorker? worker) =>
    worker is null ? null : new PerspectiveWorkerEventSource(worker);

  /// <inheritdoc />
  public event PerspectiveEventProcessedHandler? OnPerspectiveEventProcessed {
    add => _worker.OnPerspectiveEventProcessed += value;
    remove => _worker.OnPerspectiveEventProcessed -= value;
  }
}

/// <summary>
/// How many distinct events <paramref name="Perspective"/> must apply on each stream a wait names: for a product created
/// with stock, 1 for the catalog (ProductCreated) and 2 for inventory levels (ProductCreated, InventoryRestocked).
/// </summary>
/// <param name="Perspective">The perspective type; matched by the CLR name the worker reports.</param>
/// <param name="EventsPerStream">Distinct events it must apply on every stream.</param>
public sealed record PerspectiveApplyExpectation(Type Perspective, int EventsPerStream);
