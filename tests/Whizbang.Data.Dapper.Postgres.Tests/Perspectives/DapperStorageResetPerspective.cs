// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.Dapper.Postgres.Tests.Perspectives;

// A perspective whose generated Dapper schema DapperTableStorageResetTests applies, so the reset statements it
// checks are the shipped ones (#1747).

/// <summary>A model that declares one autovacuum setting and none of the other governed storage parameters.</summary>
[PerspectiveTableStorage(AutovacuumVacuumThreshold = 500)]
public record DapperStorageResetModel {
  [StreamId]
  public Guid Id { get; init; }

  public string? Name { get; init; }
}

public record DapperStorageResetNoted([property: StreamId] Guid Id) : IEvent;

public class DapperStorageResetPerspective : IPerspectiveFor<DapperStorageResetModel, DapperStorageResetNoted> {
  public DapperStorageResetModel Apply(DapperStorageResetModel currentData, DapperStorageResetNoted eventData) => currentData;
}
