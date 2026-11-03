// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;

namespace Whizbang.Sagas.Tests;

/// <summary>
/// The process-wide per-item stream namespace (<see cref="SagaItemStreams.AppDefaultNamespace"/>) as tests share it:
/// the <see cref="NOT_IN_PARALLEL_KEY"/> every test that changes or asserts on it runs under, and a scope that changes
/// it the way another registration in the process would and puts the prior value back when disposed.
/// </summary>
/// <remarks>
/// <para>
/// Every <c>AddWhizbangSagas</c> call sets the process-wide namespace, and tests run in parallel. A test that derives a
/// per-item stream id twice through that value (once in the code under test, once to key its stub) reads whichever
/// namespace the last registration anywhere in the process left, and the two reads can differ. That was a flaky
/// failure ("Reconciler queried unknown per-item stream") in a test that never registered sagas itself.
/// </para>
/// <para>
/// So a test that is not about the process-wide value derives through an explicit namespace or
/// <see cref="ISagaItemStreamIdDeriver"/>, and calls <see cref="RegisterElsewhere"/> between its derivations to prove
/// the value it no longer reads cannot move it. A test that does change the value (every <c>AddWhizbangSagas</c>
/// call) runs under <see cref="NOT_IN_PARALLEL_KEY"/> and restores it, so the tests that assert on it see only their
/// own writes.
/// </para>
/// </remarks>
internal sealed class ProcessWideSagaNamespace : IDisposable {
  /// <summary>The <c>[NotInParallel]</c> key for every test that changes or asserts on the process-wide namespace.</summary>
  public const string NOT_IN_PARALLEL_KEY = "AppDefaultNamespace";

  private static readonly Guid _elsewhere = Guid.Parse("5e1f0a3c-7d2b-4c8e-9a6f-0b1c2d3e4f50");
  private static readonly Guid _elsewhereAgain = Guid.Parse("6f2a1b4d-8e3c-4d9f-ab70-1c2d3e4f5061");

  private readonly Guid _prior = SagaItemStreams.AppDefaultNamespace;

  /// <summary>How many times <see cref="RegisterElsewhere"/> changed the namespace.</summary>
  public int Registrations { get; private set; }

  /// <summary>
  /// Registers sagas in a container of its own with a namespace different from the current one, as a second host or
  /// a parallel test does, so the process-wide value changes under the caller.
  /// </summary>
  public void RegisterElsewhere() {
    var next = SagaItemStreams.AppDefaultNamespace == _elsewhere ? _elsewhereAgain : _elsewhere;
    new ServiceCollection().AddWhizbangSagas(opts => opts.PerItemStreamNamespace = next);
    Registrations++;
  }

  /// <summary>Puts back the namespace that was in effect when the scope began.</summary>
  public void Dispose() => SagaItemStreams.AppDefaultNamespace = _prior;
}
