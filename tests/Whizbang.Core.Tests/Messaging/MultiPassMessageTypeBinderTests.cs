using System;
using System.Threading.Tasks;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

#pragma warning disable CA1707
#pragma warning disable IDE1006

public class MultiPassMessageTypeBinderTests {

  [Test]
  public async Task Bind_FullStrongName_HitsPass1Async() {
    // Use a built-in BCL type whose full strong name we know exists in the loaded runtime.
    var binder = new MultiPassMessageTypeBinder();
    var name = typeof(string).AssemblyQualifiedName!;

    var (type, pass) = binder.BindWithDiagnostics(name);

    await Assert.That(type).IsEqualTo(typeof(string));
    await Assert.That(pass).IsEqualTo(MessageTypeBinderPass.ExactStrongName);
  }

  [Test]
  public async Task Bind_VersionMetadataPresent_StillResolvesAsync() {
    // Runtime note: Type.GetType is permissive about version mismatches for unsigned assemblies,
    // so pass 1 typically still hits even when the version metadata is wrong. Pass 2 is the
    // fallback for strong-named assemblies whose strict-version resolution fails. The
    // important contract is that the binder resolves SOMETHING when the type exists locally —
    // the specific pass that wins is informative for telemetry but not behaviorally critical.
    var binder = new MultiPassMessageTypeBinder();
    var name = "System.String, System.Private.CoreLib, Version=99.99.99.99, Culture=neutral, PublicKeyToken=7cec85d7bea7798e";

    var (type, pass) = binder.BindWithDiagnostics(name);

    await Assert.That(type).IsEqualTo(typeof(string));
    await Assert.That(pass).IsNotEqualTo(MessageTypeBinderPass.Miss);
  }

  [Test]
  public async Task Bind_AssemblyNameWrong_HitsPass3Async() {
    // Wrong assembly name forces passes 1+2 to miss; pass 3 scans loaded assemblies for
    // a type whose FullName matches and finds it.
    var binder = new MultiPassMessageTypeBinder();
    var name = "System.String, NonExistentAssemblyName";

    var (type, pass) = binder.BindWithDiagnostics(name);

    await Assert.That(type).IsEqualTo(typeof(string));
    await Assert.That(pass).IsEqualTo(MessageTypeBinderPass.TypeFullNameAcrossAssemblies);
  }

  [Test]
  public async Task Bind_NoMatch_ReturnsNullMissAsync() {
    var binder = new MultiPassMessageTypeBinder();

    var (type, pass) = binder.BindWithDiagnostics("Definitely.Not.A.Real.Type, NopeAssembly");

    await Assert.That(type).IsNull();
    await Assert.That(pass).IsEqualTo(MessageTypeBinderPass.Miss);
  }

  [Test]
  public async Task Bind_Cached_ReturnsSameInstanceOnSecondCallAsync() {
    var binder = new MultiPassMessageTypeBinder();
    var name = typeof(string).AssemblyQualifiedName!;

    var first = binder.Bind(name);
    var second = binder.Bind(name);

    await Assert.That(first).IsEqualTo(second);
    await Assert.That(first).IsEqualTo(typeof(string));
  }

  [Test]
  public async Task Bind_EmptyOrNullInput_ReturnsMissAsync() {
    var binder = new MultiPassMessageTypeBinder();

    await Assert.That(binder.Bind("")).IsNull();
    await Assert.That(binder.Bind(null!)).IsNull();
  }

  [Test]
  public async Task BindWithDiagnostics_GenericType_StillResolvesAsync() {
    // Sanity: generic-type names with [[...]] inner-args don't break the FullName extraction.
    var binder = new MultiPassMessageTypeBinder();
    var name = typeof(System.Collections.Generic.List<int>).AssemblyQualifiedName!;

    var (type, _) = binder.BindWithDiagnostics(name);

    await Assert.That(type).IsEqualTo(typeof(System.Collections.Generic.List<int>));
  }
}
