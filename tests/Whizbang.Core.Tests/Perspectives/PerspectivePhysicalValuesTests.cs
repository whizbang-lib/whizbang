using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// An enumeration in a physical column is stored as its underlying number. <see cref="PerspectivePhysicalValues"/>
/// is the one place that turns an enum value into that scalar, and names the column-safe scalar type for each
/// underlying type (an unsigned or byte-sized type widens to the next signed Postgres integer), so the per-event
/// upsert, the collective apply and replay all bind the same value.
/// </summary>
/// <docs>fundamentals/perspectives/physical-fields</docs>
/// <tests>Whizbang.Core/Perspectives/PerspectivePhysicalValues.cs</tests>
public class PerspectivePhysicalValuesTests {
  private enum IntKind { Zero, One, Two }
  private enum ByteKind : byte { A = 7 }
  private enum SByteKind : sbyte { A = -3 }
  private enum ShortKind : short { A = 300 }
  private enum UShortKind : ushort { A = 60000 }
  private enum UIntKind : uint { A = 4000000000 }
  private enum LongKind : long { A = 5000000000 }
  private enum ULongKind : ulong { A = 18000000000000000000 }

  [Test]
  public async Task ToColumnScalar_IntEnum_IsTheIntegerAsync() {
    var value = PerspectivePhysicalValues.ToColumnScalar(IntKind.Two);
    await Assert.That(value).IsTypeOf<int>();
    await Assert.That(value).IsEqualTo(2);
  }

  [Test]
  public async Task ToColumnScalar_NarrowAndUnsignedEnums_WidenToASignedColumnTypeAsync() {
    await Assert.That(PerspectivePhysicalValues.ToColumnScalar(ByteKind.A)).IsEqualTo((short)7);
    await Assert.That(PerspectivePhysicalValues.ToColumnScalar(SByteKind.A)).IsEqualTo((short)-3);
    await Assert.That(PerspectivePhysicalValues.ToColumnScalar(ShortKind.A)).IsEqualTo((short)300);
    await Assert.That(PerspectivePhysicalValues.ToColumnScalar(UShortKind.A)).IsEqualTo(60000);
    await Assert.That(PerspectivePhysicalValues.ToColumnScalar(UIntKind.A)).IsEqualTo(4000000000L);
    await Assert.That(PerspectivePhysicalValues.ToColumnScalar(LongKind.A)).IsEqualTo(5000000000L);
    await Assert.That(PerspectivePhysicalValues.ToColumnScalar(ULongKind.A)).IsEqualTo(18000000000000000000m)
      .Because("Postgres has no unsigned 64-bit integer; numeric holds every ulong exactly.");
  }

  [Test]
  public async Task ToColumnScalar_NonEnumAndNull_PassThroughAsync() {
    await Assert.That(PerspectivePhysicalValues.ToColumnScalar("x")).IsEqualTo("x");
    await Assert.That(PerspectivePhysicalValues.ToColumnScalar(null)).IsNull();
  }

  [Test]
  public async Task ToColumnScalar_WithADeclaredScalarType_ConvertsToItAsync() {
    await Assert.That(PerspectivePhysicalValues.ToColumnScalar(ByteKind.A, typeof(short))).IsEqualTo((short)7);
    await Assert.That(PerspectivePhysicalValues.ToColumnScalar(null, typeof(short))).IsNull();
    await Assert.That(PerspectivePhysicalValues.ToColumnScalar(5, typeof(int))).IsEqualTo(5)
      .Because("Only an enumeration is converted; a value already scalar is bound as it is.");
  }

  [Test]
  public async Task ColumnScalarType_MapsEachUnderlyingTypeAsync() {
    await Assert.That(PerspectivePhysicalValues.ColumnScalarType(typeof(byte))).IsEqualTo(typeof(short));
    await Assert.That(PerspectivePhysicalValues.ColumnScalarType(typeof(sbyte))).IsEqualTo(typeof(short));
    await Assert.That(PerspectivePhysicalValues.ColumnScalarType(typeof(short))).IsEqualTo(typeof(short));
    await Assert.That(PerspectivePhysicalValues.ColumnScalarType(typeof(ushort))).IsEqualTo(typeof(int));
    await Assert.That(PerspectivePhysicalValues.ColumnScalarType(typeof(int))).IsEqualTo(typeof(int));
    await Assert.That(PerspectivePhysicalValues.ColumnScalarType(typeof(uint))).IsEqualTo(typeof(long));
    await Assert.That(PerspectivePhysicalValues.ColumnScalarType(typeof(long))).IsEqualTo(typeof(long));
    await Assert.That(PerspectivePhysicalValues.ColumnScalarType(typeof(ulong))).IsEqualTo(typeof(decimal));
  }

  [Test]
  public async Task ColumnScalarType_NotAnIntegralType_ThrowsAsync() {
    await Assert.That(() => PerspectivePhysicalValues.ColumnScalarType(typeof(string))).Throws<ArgumentException>();
  }
}
