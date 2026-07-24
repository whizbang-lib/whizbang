using System.Threading.Tasks;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

#pragma warning disable CA1707
#pragma warning disable IDE1006

public class EnvelopeTypeNameHelperTests {

  [Test]
  public async Task ExtractInnerTypeName_StandardEnvelope_ReturnsInnerTypeAsync() {
    var name = "Whizbang.Core.Observability.MessageEnvelope`1[[MyApp.Events.Foo, MyApp.Contracts]], Whizbang.Core";
    var inner = EnvelopeTypeNameHelper.ExtractInnerTypeName(name);
    await Assert.That(inner).IsEqualTo("MyApp.Events.Foo, MyApp.Contracts");
  }

  [Test]
  public async Task ExtractInnerTypeName_WithVersionMetadata_PreservesItAsync() {
    var name = "MessageEnvelope`1[[MyApp.Foo, MyApp, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null]], Whizbang.Core";
    var inner = EnvelopeTypeNameHelper.ExtractInnerTypeName(name);
    await Assert.That(inner).IsEqualTo("MyApp.Foo, MyApp, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null");
  }

  [Test]
  public async Task ExtractInnerTypeName_NotGeneric_ReturnsNullAsync() {
    var inner = EnvelopeTypeNameHelper.ExtractInnerTypeName("MyApp.SomeType, MyApp");
    await Assert.That(inner).IsNull();
  }

  [Test]
  public async Task ExtractInnerTypeName_EmptyOrNull_ReturnsNullAsync() {
    await Assert.That(EnvelopeTypeNameHelper.ExtractInnerTypeName("")).IsNull();
    await Assert.That(EnvelopeTypeNameHelper.ExtractInnerTypeName(null!)).IsNull();
  }

  [Test]
  public async Task ExtractInnerTypeName_NestedGenerics_HandlesDepthAsync() {
    // List<MessageEnvelope<Foo>> — bracket depth must be tracked, not first ]].
    var name = "System.Collections.Generic.List`1[[Whizbang.Core.Observability.MessageEnvelope`1[[MyApp.Foo, MyApp]], Whizbang.Core]], System.Private.CoreLib";
    var inner = EnvelopeTypeNameHelper.ExtractInnerTypeName(name);
    await Assert.That(inner).IsEqualTo("Whizbang.Core.Observability.MessageEnvelope`1[[MyApp.Foo, MyApp]], Whizbang.Core");
  }
}
