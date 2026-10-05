// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Threading.Tasks;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.EFCore.Postgres.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Locks the auto-wiring invariant after #967: the consumer assembly's generated
/// <c>[ModuleInitializer]</c> joins its <c>PerspectivePersistenceJsonContext</c> to the registry's
/// persistence profile, and that registration alone is what the atomic upsert serializes with. No
/// callback has to fire and no process-wide slot has to be set, so a perspective model with a
/// <c>[WhizbangId]</c> property serializes in the EF-compatible <c>{"Value":"&lt;guid&gt;"}</c>
/// nested-object form the moment the assembly has loaded.
/// </summary>
/// <remarks>
/// A pure unit test, with no database and no DI container: the wiring is structural. The end-to-end SQL
/// semantics are proven by the integration tests that write through the atomic path.
/// </remarks>
[Category("Shard2")]
public class PerspectivePersistenceAutoWireTests {
  [Test]
  public async Task RegistryPersistenceOptions_WithNoStartupHookRun_ProduceEFCompatibleByteFormatAsync() {
    var testGuid = System.Guid.Parse("019e244a-6bda-78a9-a08f-a1011c9c31dd");
    var order = new Order {
      OrderId = new TestOrderId(testGuid),
      Amount = 100.00m,
      Status = "Created"
    };

    var json = PerspectiveDocumentSerialization.Serialize(order);

    // Byte-format invariant matches what EF Core 10's ComplexProperty.ToJson writes.
    await Assert.That(json).Contains("\"OrderId\":{\"Value\":\"019e244a-6bda-78a9-a08f-a1011c9c31dd\"}");
    await Assert.That(json).DoesNotContain("\"OrderId\":\"019e244a-6bda-78a9-a08f-a1011c9c31dd\"");
  }
}
