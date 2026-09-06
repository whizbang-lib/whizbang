using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Coverage-round-23 targeted tests for <see cref="MultiPassMessageTypeBinder"/>'s pass-2
/// (assembly-simple-name) recovery branch and the bracket-depth-aware full-name extraction
/// that pass 3's loaded-assembly scan depends on. The main suite
/// (MultiPassMessageTypeBinderTests) exercises passes 1 and 3 on clean input but never
/// forces pass 2 to actually win, never falls through pass 2 to pass 3, and never feeds
/// <c>_extractTypeFullName</c> a bracketed or comma-less name.
/// </summary>
/// <tests>src/Whizbang.Core/Messaging/MultiPassMessageTypeBinder.cs</tests>
public class MultiPassMessageTypeBinderCoverageTests {

  // If a publisher's envelope carries a type name whose version segment is corrupted
  // (stale build metadata, truncated in transport, etc.), pass 1's raw Type.GetType call
  // must fail closed instead of throwing, and pass 2 must still resolve the type after
  // stripping the unusable metadata - otherwise a recoverable envelope becomes a hard miss
  // and the message is ack+dropped instead of dispatched.
  [Test]
  public async Task BindWithDiagnostics_MalformedVersionSegment_RecoversViaPass2Async() {
    var binder = new MultiPassMessageTypeBinder();
    var name = "System.String, System.Private.CoreLib, Version=not-a-version";

    var (type, pass) = binder.BindWithDiagnostics(name);

    await Assert.That(type).IsEqualTo(typeof(string))
      .Because("pass 2 must resolve the type once the unparseable version metadata is stripped");
    await Assert.That(pass).IsEqualTo(MessageTypeBinderPass.AssemblySimpleName)
      .Because("the hit must come from the metadata-stripped retry, not the raw strong name");
  }

  // Pass 2 only helps when normalization changes the string; if the assembly name itself
  // is wrong, stripping the version doesn't fix that, and the binder must fall through the
  // pass-2 block to pass 3 rather than getting stuck between passes - otherwise an
  // assembly-rename scenario that also carries stale version metadata would never resolve.
  [Test]
  public async Task BindWithDiagnostics_VersionMetadataPresentButAssemblyMissing_FallsThroughPass2ToPass3Async() {
    var binder = new MultiPassMessageTypeBinder();
    var name = "System.String, NonExistentAssemblyName, Version=1.0.0.0";

    var (type, pass) = binder.BindWithDiagnostics(name);

    await Assert.That(type).IsEqualTo(typeof(string))
      .Because("pass 3's FullName scan across loaded assemblies must still find the type");
    await Assert.That(pass).IsEqualTo(MessageTypeBinderPass.TypeFullNameAcrossAssemblies)
      .Because("both pass 1 and pass 2 miss on the bogus assembly name, so pass 3 must be the one that wins");
  }

  // Pass 3's FullName extraction must treat a comma inside [[...]] generic-argument
  // brackets as part of the name, not as the assembly-name separator - otherwise a generic
  // message type's extracted FullName gets truncated mid-argument and pass 3 searches for
  // the wrong string, silently failing to resolve a type that IS loaded.
  [Test]
  public async Task BindWithDiagnostics_GenericLookingNameWithNoMatch_TracksBracketDepthAsync() {
    var binder = new MultiPassMessageTypeBinder();
    var name = "Coverage.Fake.Container`1[[System.String, System.Private.CoreLib]], NonExistentOuterAssembly";

    var (type, pass) = binder.BindWithDiagnostics(name);

    await Assert.That(type).IsNull()
      .Because("no loaded assembly defines a type with this fabricated generic FullName");
    await Assert.That(pass).IsEqualTo(MessageTypeBinderPass.Miss)
      .Because("all three passes must miss cleanly for a name that was never a real type");
  }

  // A bare type name with no assembly qualifier and no comma anywhere must still reach
  // pass 3's FullName scan by falling through to the whole-string return, instead of
  // throwing or leaving fullName null - otherwise a stripped-down or malformed envelope
  // type name would crash the binder instead of degrading to a reported miss.
  [Test]
  public async Task BindWithDiagnostics_NoAssemblyQualifierAndNoTopLevelComma_ReturnsMissAsync() {
    var binder = new MultiPassMessageTypeBinder();
    var name = "Coverage.Fake.NoAssemblyQualifierAtAll";

    var (type, pass) = binder.BindWithDiagnostics(name);

    await Assert.That(type).IsNull()
      .Because("this bare name matches no type in the executing assembly, System.Private.CoreLib, or any other loaded assembly");
    await Assert.That(pass).IsEqualTo(MessageTypeBinderPass.Miss)
      .Because("all three passes must miss cleanly rather than throwing on a comma-less name");
  }

  // Pass 3 hands each loaded assembly a FullName carved out of the same untrusted header, and
  // Assembly.GetType has the identical caveat Type.GetType does: throwOnError:false suppresses
  // the type not being found, not the name failing to parse. A generic whose NESTED argument
  // carries a malformed assembly segment survives passes 1 and 2 (both of which reject the whole
  // string) and reaches pass 3 with that garbage still embedded, so the very first assembly in
  // the scan throws. Without the guard, one unparseable name aborts the scan across every
  // remaining assembly -- turning a message that should be reported as a miss and dead-lettered
  // into an exception out of the deserializer, on every redelivery, forever.
  [Test]
  public async Task BindWithDiagnostics_MalformedAssemblySegmentInsideAGenericArgument_MissesInsteadOfThrowingAsync() {
    var binder = new MultiPassMessageTypeBinder();
    // The OUTER type must be unresolvable too. Pass 2 normalizes the whole string, which strips
    // the malformed segment -- so a real generic like List`1 would simply be recovered there and
    // pass 3 would never run. Only when passes 1 and 2 both miss does pass 3 get handed the
    // ORIGINAL name, malformed segment still attached.
    var name = "Coverage.Fake.MissingGeneric`1[[System.String, System.Private.CoreLib, Version=not-a-version]], Coverage.Fake.Assembly";

    var (type, pass) = binder.BindWithDiagnostics(name);

    await Assert.That(type).IsNull()
      .Because("no loaded assembly defines this fabricated generic, so no pass can produce a type");
    await Assert.That(pass).IsEqualTo(MessageTypeBinderPass.Miss)
      .Because("a malformed nested assembly name must degrade to a reported miss the caller can "
             + "dead-letter, not an exception thrown out of pass 3's assembly scan");
  }
}
