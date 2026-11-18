// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>The contract against the in-memory fallback ledger.</summary>
/// <code-under-test>src/Whizbang.Core/Messaging/IntegrityRepairLedger.cs</code-under-test>
[InheritsTests]
public class InMemoryIntegrityRepairLedgerContractTests : IntegrityRepairLedgerContractTests {
  /// <inheritdoc />
  protected override Task<IIntegrityRepairLedger> CreateLedgerAsync() =>
    Task.FromResult<IIntegrityRepairLedger>(new IntegrityRepairLedger());
}
