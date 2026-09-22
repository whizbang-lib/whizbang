using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Serialization;

namespace Whizbang.Core.Tests.Serialization;

/// <summary>An event carrying an instant, served by this assembly's generated message context.</summary>
public sealed record StampedEvent([property: StreamId] Guid StampId, DateTime At) : IEvent;

/// <summary>
/// That the generated message context hands each set of options its own metadata, so the profile
/// that asked first does not decide how every later profile writes a date.
/// </summary>
/// <remarks>
/// <para>
/// The generated facade caches the metadata it creates, which self-referencing types need, and it
/// cached by type alone. A serializer options set is bound to the metadata it is given, and the
/// converter a property uses is chosen from the options the metadata was created for. So once the
/// wire profile had asked for a date, the persistence profile received a date bound to the wire's
/// options and wrote a rendering into a document whose index casts the value to a number. It failed
/// or not depending on which profile happened to ask first, which is what made it intermittent.
/// </para>
/// <para>
/// Each order is asserted, because the failure is order-dependent and a fix that only worked one
/// way round would pass a single-order test.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Generators/Templates/Snippets/JsonContextSnippets.cs</code-under-test>
[Category("Core")]
[Category("Serialization")]
public class FacadeTypeInfoPerOptionsTests {
  private static readonly DateTime _at = new(2026, 3, 4, 5, 6, 7, 890, DateTimeKind.Utc);

  private static JsonValueKind _kindOfAt(JsonSerializerOptions options) {
    var json = JsonSerializer.Serialize(new StampedEvent(Guid.CreateVersion7(), _at), options.GetTypeInfo(typeof(StampedEvent)));
    return JsonDocument.Parse(json).RootElement.GetProperty("At").ValueKind;
  }

  /// <summary>The wire profile asking first does not make the persistence profile write a rendering.</summary>
  [Test]
  public async Task ThePersistenceProfileWritesANumberAfterTheWireProfileAskedFirstAsync() {
    var wire = JsonContextRegistry.CreateCombinedOptions();
    var stored = JsonContextRegistry.CreateCombinedOptions(SerializationProfile.Persistence);

    var onTheWire = _kindOfAt(wire);
    var inTheDocument = _kindOfAt(stored);

    await Assert.That(onTheWire).IsEqualTo(JsonValueKind.String)
      .Because("the wire keeps the rendering other systems and older releases read");
    await Assert.That(inTheDocument).IsEqualTo(JsonValueKind.Number)
      .Because("a document's date is a number whatever profile asked for the type's metadata first; "
        + "bound to the wire's options it was written as a rendering the document's index could not cast");
  }

  /// <summary>The persistence profile asking first does not make the wire profile write a number.</summary>
  [Test]
  public async Task TheWireProfileWritesARenderingAfterThePersistenceProfileAskedFirstAsync() {
    var stored = JsonContextRegistry.CreateCombinedOptions(SerializationProfile.Persistence);
    var wire = JsonContextRegistry.CreateCombinedOptions();

    var inTheDocument = _kindOfAt(stored);
    var onTheWire = _kindOfAt(wire);

    await Assert.That(inTheDocument).IsEqualTo(JsonValueKind.Number);
    await Assert.That(onTheWire).IsEqualTo(JsonValueKind.String)
      .Because("a number on the wire would be a wire-format change nobody asked for");
  }
}
