// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;
using Whizbang.Data.EFCore.Postgres.Perspectives;
using Whizbang.Data.EFCore.Postgres.QueryTranslation;

namespace Whizbang.Data.EFCore.Postgres.Tests.BranchCoverage.EFCore;

/// <summary>
/// Branch coverage for two lookup tables: the stored-form JSON reader/writer chosen per temporal
/// kind (every kind, and none), and the containment overload chosen per member type (byte, a
/// nullable byte, a type checked after byte, and an unsupported type).
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/Perspectives/CanonicalTemporalJsonReaderWriters.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/QueryTranslation/JsonbContainment.cs</code-under-test>
[Category("Shard2")]
public class TemporalAndContainmentLookupTests {

  [Test]
  public async Task TypeFor_EveryKind_MapsToItsReaderWriter_AndNoneToNullAsync() {
    await Assert.That(CanonicalTemporalJsonReaderWriters.TypeFor(StoredTemporalKind.Instant))
      .IsEqualTo(typeof(CanonicalTemporalJsonReaderWriters.Instant));
    await Assert.That(CanonicalTemporalJsonReaderWriters.TypeFor(StoredTemporalKind.OffsetInstant))
      .IsEqualTo(typeof(CanonicalTemporalJsonReaderWriters.OffsetInstant));
    await Assert.That(CanonicalTemporalJsonReaderWriters.TypeFor(StoredTemporalKind.Day))
      .IsEqualTo(typeof(CanonicalTemporalJsonReaderWriters.Day));
    await Assert.That(CanonicalTemporalJsonReaderWriters.TypeFor(StoredTemporalKind.TimeOfDay))
      .IsEqualTo(typeof(CanonicalTemporalJsonReaderWriters.TimeOfDay));
    await Assert.That(CanonicalTemporalJsonReaderWriters.TypeFor(StoredTemporalKind.Duration))
      .IsEqualTo(typeof(CanonicalTemporalJsonReaderWriters.Duration));
    await Assert.That(CanonicalTemporalJsonReaderWriters.TypeFor(null)).IsNull()
      .Because("a member with no temporal kind keeps the provider's own reader/writer");
  }

  [Test]
  public async Task OverloadFor_Byte_NullableByte_LaterAndUnsupportedTypesAsync() {
    var forByte = JsonbContainment.OverloadFor(typeof(byte));
    var forNullableByte = JsonbContainment.OverloadFor(typeof(byte?));
    var forDateTime = JsonbContainment.OverloadFor(typeof(DateTime));
    var forTimeSpan = JsonbContainment.OverloadFor(typeof(TimeSpan));

    await Assert.That(forByte!.GetParameters()[0].ParameterType).IsEqualTo(typeof(byte));
    await Assert.That(forNullableByte).IsEqualTo(forByte)
      .Because("a nullable member resolves to its underlying overload");
    await Assert.That(forDateTime!.GetParameters()[0].ParameterType).IsEqualTo(typeof(DateTime));
    await Assert.That(forTimeSpan).IsNull()
      .Because("containment is not safe for a type with no overload");
  }
}
