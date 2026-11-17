// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Whizbang.Core.Observability;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Observability;

/// <summary>
/// Attributes the commands EF Core runs inside a framework mechanism, as <see cref="WhizbangCommand"/> does for the
/// framework's raw commands: the text leads with the mechanism's comment, and each statement is counted, timed and
/// its rows recorded on <see cref="DbStatementMetrics"/>.
/// </summary>
/// <remarks>
/// <para>
/// Registered by <c>UseWhizbangFunctions()</c>, which every framework context calls, so no context can run without it.
/// </para>
/// <para>
/// Only commands run with a <see cref="DbMechanismScope"/> open are touched. The same context also runs the
/// application's own queries (lenses, endpoints), and those are not the framework's to attribute or count, so a
/// command with no scope open is left exactly as EF Core built it.
/// </para>
/// <para>
/// A reader is recorded when it is disposed, where EF Core reports how many rows were read and how long the reader was
/// open; a non-query when it returns, with the rows it changed; a scalar with the one row it read, or none.
/// </para>
/// </remarks>
/// <docs>operations/observability/database-attribution</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Observability/WhizbangCommandAttributionTests.cs</tests>
public sealed class WhizbangCommandAttributionInterceptor : DbCommandInterceptor {
  /// <summary>The one instance every context shares, so registering it creates no new internal service provider.</summary>
  public static WhizbangCommandAttributionInterceptor Instance { get; } = new();

  private WhizbangCommandAttributionInterceptor() { }

  /// <inheritdoc />
  public override InterceptionResult<DbDataReader> ReaderExecuting(
      DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result) {
    _annotate(command);
    return result;
  }

  /// <inheritdoc />
  public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
      DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
      CancellationToken cancellationToken = default) {
    _annotate(command);
    return ValueTask.FromResult(result);
  }

  /// <inheritdoc />
  public override InterceptionResult<int> NonQueryExecuting(
      DbCommand command, CommandEventData eventData, InterceptionResult<int> result) {
    _annotate(command);
    return result;
  }

  /// <inheritdoc />
  public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
      DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
      CancellationToken cancellationToken = default) {
    _annotate(command);
    return ValueTask.FromResult(result);
  }

  /// <inheritdoc />
  public override InterceptionResult<object> ScalarExecuting(
      DbCommand command, CommandEventData eventData, InterceptionResult<object> result) {
    _annotate(command);
    return result;
  }

  /// <inheritdoc />
  public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
      DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
      CancellationToken cancellationToken = default) {
    _annotate(command);
    return ValueTask.FromResult(result);
  }

  /// <inheritdoc />
  public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result) {
    _record(eventData.Duration, result);
    return result;
  }

  /// <inheritdoc />
  public override ValueTask<int> NonQueryExecutedAsync(
      DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default) {
    _record(eventData.Duration, result);
    return ValueTask.FromResult(result);
  }

  /// <inheritdoc />
  public override object? ScalarExecuted(DbCommand command, CommandExecutedEventData eventData, object? result) {
    _record(eventData.Duration, _scalarRows(result));
    return result;
  }

  /// <inheritdoc />
  public override ValueTask<object?> ScalarExecutedAsync(
      DbCommand command, CommandExecutedEventData eventData, object? result, CancellationToken cancellationToken = default) {
    _record(eventData.Duration, _scalarRows(result));
    return ValueTask.FromResult(result);
  }

  /// <inheritdoc />
  public override InterceptionResult DataReaderDisposing(
      DbCommand command, DataReaderDisposingEventData eventData, InterceptionResult result) {
    _record(eventData.Duration, eventData.ReadCount);
    return result;
  }

  private static long _scalarRows(object? result) => result is null or DBNull ? 0 : 1;

  private static void _annotate(DbCommand command) {
    var mechanism = DbMechanismScope.Current;
    if (mechanism is not null) {
      command.CommandText = WhizbangCommand.Annotate(command.CommandText, mechanism);
    }
  }

  private static void _record(TimeSpan duration, long rows) {
    var mechanism = DbMechanismScope.Current;
    if (mechanism is not null) {
      DbStatementMetrics.Record(mechanism, duration, rows);
    }
  }
}
