#Requires -Modules Pester

BeforeAll {
  $script:ScriptPath = Join-Path -Path $PSScriptRoot -ChildPath '../Test-TestsTagLink.ps1'
  . $script:ScriptPath
  $script:RepoRoot = Join-Path -Path $PSScriptRoot -ChildPath '../../..' -Resolve

  # A fake repository in $TestDrive: keys are repository-relative paths, values are file contents.
  function New-FakeRepo([hashtable]$Files) {
    $repo = Join-Path -Path $TestDrive -ChildPath ([guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path (Join-Path -Path $repo -ChildPath 'src') -Force | Out-Null
    foreach ($relative in $Files.Keys) {
      $path = Join-Path -Path $repo -ChildPath $relative
      New-Item -ItemType Directory -Path (Split-Path -Path $path -Parent) -Force | Out-Null
      Set-Content -Path $path -Value $Files[$relative] -NoNewline
    }
    return $repo
  }

  # Resolves one tag text against a fake repository, as the scan does.
  function Resolve-Text([string]$Repo, [string]$Text) {
    $tag = @(Get-TestsTagInText -Text "/// <tests>$Text</tests>" -Source 'src/A.cs')[0]
    return Resolve-TestsTag -Tag $tag -Root $Repo -Cache (New-TestsTagCache)
  }

  $script:TestFile = @'
namespace Fake;

// RemovedInCommentAsync() would be a declaration if comments counted.
/// <summary>Also RemovedInDocCommentAsync() in a doc comment.</summary>
/* BlockCommentAsync() */
 * StarLineAsync() inside a block comment
public class WidgetTests {
  public WidgetTests() { }
  [Test]
  public async Task Plain_DoesThingAsync() {
    await CalledOnlyAsync();
    var x = AssignedOnly();
    return Returned();
    Func<int> f = () => LambdaBodyOnly();
    var y = flag ? TernaryOnly() : 0;
    var z = new ConstructedOnly();
  }
  public Task<int> Generic_TypedAsync<T>() => Task.FromResult(0);
  public int[] ArrayReturn() => [];
  public int? NullableReturn() => null;
  public (int, int) TupleReturn() => (0, 0);
  private static void SpacedName  ( ) { }
}
public record WidgetRecord(int Value);
internal sealed record struct WidgetValue(int Value);
public interface IWidget { }
public enum WidgetKind { A }
public struct WidgetStruct { }
'@
}

Describe 'Get-TestsTagInText' {
  It 'finds a C# doc tag with its line number and trims the text' {
    $tags = @(Get-TestsTagInText -Text "line one`n/// <tests>  tests/A/B.cs:M  </tests>`nline three" -Source 'src/X.cs')
    $tags.Count | Should -Be 1
    $tags[0].Source | Should -Be 'src/X.cs'
    $tags[0].Line | Should -Be 2
    $tags[0].Text | Should -Be 'tests/A/B.cs:M'
    $tags[0].Closed | Should -BeTrue
    $tags[0].Reason | Should -BeNullOrEmpty
  }

  It 'finds a SQL comment tag and counts lines across CRLF endings' {
    $tags = @(Get-TestsTagInText -Text "-- a`r`n-- b`r`n-- <tests>tests/A/B.cs</tests>" -Source 'src/M.sql')
    $tags[0].Line | Should -Be 3
    $tags[0].Text | Should -Be 'tests/A/B.cs'
  }

  It 'finds two tags on one line and tags on later lines, each with its own line' {
    $tags = @(Get-TestsTagInText -Text "/// <tests>a.cs</tests> <tests>b.cs</tests>`n`n/// <tests>c.cs</tests>" -Source 'src/X.cs')
    @($tags | ForEach-Object { "$($_.Line):$($_.Text)" }) | Should -Be @('1:a.cs', '1:b.cs', '3:c.cs')
  }

  It 'reports a tag that does not close on its own line as unterminated' {
    $tags = @(Get-TestsTagInText -Text "/// <tests>tests/A/B.cs`n/// </tests>" -Source 'src/X.cs')
    $tags.Count | Should -Be 1
    $tags[0].Closed | Should -BeFalse
    $tags[0].Text | Should -Be 'tests/A/B.cs'
  }

  It 'finds nothing in text without tags' {
    @(Get-TestsTagInText -Text "/// <summary>x</summary>`n// tests/A.cs" -Source 'src/X.cs').Count | Should -Be 0
  }
}

Describe 'Test-TestsTagMember' {
  It 'accepts a method declared after a return type: <Name>' -ForEach @(
    @{ Name = 'Plain_DoesThingAsync' }, @{ Name = 'Generic_TypedAsync' }, @{ Name = 'ArrayReturn' },
    @{ Name = 'NullableReturn' }, @{ Name = 'TupleReturn' }, @{ Name = 'SpacedName' }
  ) {
    Test-TestsTagMember -Content $script:TestFile -Name $Name | Should -BeTrue
  }

  It 'accepts a type or constructor name: <Name>' -ForEach @(
    @{ Name = 'WidgetTests' }, @{ Name = 'WidgetRecord' }, @{ Name = 'WidgetValue' },
    @{ Name = 'IWidget' }, @{ Name = 'WidgetKind' }, @{ Name = 'WidgetStruct' }
  ) {
    Test-TestsTagMember -Content $script:TestFile -Name $Name | Should -BeTrue
  }

  It 'rejects a name that is only called, constructed or mentioned: <Name>' -ForEach @(
    @{ Name = 'CalledOnlyAsync' }, @{ Name = 'AssignedOnly' }, @{ Name = 'Returned' },
    @{ Name = 'LambdaBodyOnly' }, @{ Name = 'TernaryOnly' }, @{ Name = 'ConstructedOnly' },
    @{ Name = 'RemovedInCommentAsync' }, @{ Name = 'RemovedInDocCommentAsync' },
    @{ Name = 'BlockCommentAsync' }, @{ Name = 'StarLineAsync' }, @{ Name = 'NotThereAtAll' }
  ) {
    Test-TestsTagMember -Content $script:TestFile -Name $Name | Should -BeFalse
  }

  It 'does not accept a longer name that merely starts with the tagged one' {
    Test-TestsTagMember -Content $script:TestFile -Name 'Plain_DoesThing' | Should -BeFalse
    Test-TestsTagMember -Content $script:TestFile -Name 'Widget' | Should -BeFalse
  }
}

Describe 'Test-TestsTagPath' {
  BeforeAll {
    $script:PathRepo = New-FakeRepo @{ 'tests/P.Tests/Sub/A.cs' = 'x' }
  }

  It 'accepts an existing repository-relative file and answers again from the cache' {
    $cache = New-TestsTagCache
    Test-TestsTagPath -Root $PathRepo -Path 'tests/P.Tests/Sub/A.cs' -Cache $cache.Entries | Should -BeTrue
    Remove-Item -Path (Join-Path -Path $PathRepo -ChildPath 'tests/P.Tests/Sub/A.cs')
    Test-TestsTagPath -Root $PathRepo -Path 'tests/P.Tests/Sub/A.cs' -Cache $cache.Entries | Should -BeFalse -Because 'the listing is cached, but the final file check is not'
    Set-Content -Path (Join-Path -Path $PathRepo -ChildPath 'tests/P.Tests/Sub/A.cs') -Value 'x'
  }

  It 'rejects <Why>' -ForEach @(
    @{ Path = 'tests/P.Tests/Sub/Missing.cs'; Why = 'a missing file' },
    @{ Path = 'tests/p.tests/Sub/A.cs'; Why = 'a wrong-case directory, which Linux CI would not find' },
    @{ Path = 'tests/P.Tests/Sub/a.cs'; Why = 'a wrong-case file name' },
    @{ Path = 'tests/P.Tests/Sub'; Why = 'a directory' },
    @{ Path = 'tests/P.Tests/Sub/A.cs/Inner.cs'; Why = 'a path through a file' },
    @{ Path = 'tests/P.Tests/../P.Tests/Sub/A.cs'; Why = 'a parent segment' },
    @{ Path = './tests/P.Tests/Sub/A.cs'; Why = 'a current-directory segment' },
    @{ Path = '/tests/P.Tests/Sub/A.cs'; Why = 'a rooted path' },
    @{ Path = 'tests//P.Tests/Sub/A.cs'; Why = 'an empty segment' },
    @{ Path = 'tests\P.Tests\Sub\A.cs'; Why = 'backslashes' }
  ) {
    Test-TestsTagPath -Root $PathRepo -Path $Path -Cache (New-TestsTagCache).Entries | Should -BeFalse
  }
}

Describe 'Resolve-TestsTag' {
  BeforeAll {
    $script:ResolveRepo = New-FakeRepo @{ 'tests/W.Tests/WidgetTests.cs' = $script:TestFile; 'src/Whizbang.Testing/Contracts/C.cs' = 'public class C { public void M() { } }' }
  }

  It 'resolves <Text>' -ForEach @(
    @{ Text = 'tests/W.Tests/WidgetTests.cs' },
    @{ Text = 'tests/W.Tests/WidgetTests.cs:Plain_DoesThingAsync' },
    @{ Text = 'tests/W.Tests/WidgetTests.cs:WidgetTests' },
    @{ Text = 'src/Whizbang.Testing/Contracts/C.cs:M' },
    @{ Text = 'No tests found' }
  ) {
    Resolve-Text -Repo $ResolveRepo -Text $Text | Should -BeNullOrEmpty
  }

  It 'rejects <Text> as malformed' -ForEach @(
    @{ Text = '' },
    @{ Text = 'tests/W.Tests/WidgetTests.cs:WidgetTests.Plain_DoesThingAsync' },
    @{ Text = 'tests/W.Tests/WidgetTests.cs, tests/W.Tests/Other.cs' },
    @{ Text = 'tests/W.Tests/WidgetTests.cs:' },
    @{ Text = 'tests/W.Tests/WidgetTests.cs:9Bad' },
    @{ Text = 'no tests found' }
  ) {
    Resolve-Text -Repo $ResolveRepo -Text $Text | Should -Match '^malformed'
  }

  It 'reports a missing file for <Text>' -ForEach @(
    @{ Text = 'W.Tests/WidgetTests.cs' },
    @{ Text = 'tests/W.Tests/Gone.cs:Plain_DoesThingAsync' }
  ) {
    Resolve-Text -Repo $ResolveRepo -Text $Text | Should -Match '^missing file'
  }

  It 'reports a missing method when the file declares no such member, reading the file once' {
    $cache = New-TestsTagCache
    $tag = @(Get-TestsTagInText -Text '<tests>tests/W.Tests/WidgetTests.cs:CalledOnlyAsync</tests>' -Source 'src/A.cs')[0]
    Resolve-TestsTag -Tag $tag -Root $ResolveRepo -Cache $cache | Should -Match '^missing method'
    $cache.Content['tests/W.Tests/WidgetTests.cs'] = 'public void CalledOnlyAsync() { }'
    Resolve-TestsTag -Tag $tag -Root $ResolveRepo -Cache $cache | Should -BeNullOrEmpty -Because 'the second lookup must come from the cache'
  }

  It 'reports an unterminated tag' {
    $tag = @(Get-TestsTagInText -Text '/// <tests>tests/W.Tests/WidgetTests.cs' -Source 'src/A.cs')[0]
    Resolve-TestsTag -Tag $tag -Root $ResolveRepo -Cache (New-TestsTagCache) | Should -Match '^unterminated'
  }
}

Describe 'Find-TestsTag' {
  It 'scans .cs and .sql under src with repository-relative forward-slash paths, skipping bin, obj and other files' {
    $repo = New-FakeRepo @{
      'src/P/A.cs'          = '/// <tests>a.cs</tests>'
      'src/P/Migrations/1.sql' = "--`n-- <tests>b.cs</tests>"
      'src/P/README.md'     = '<tests>tests/.../Example.cs</tests>'
      'src/P/bin/Gen.cs'    = '/// <tests>bin.cs</tests>'
      'src/P/obj/Gen.cs'    = '/// <tests>obj.cs</tests>'
      'tests/P.Tests/T.cs'  = '/// <tests>outside.cs</tests>'
    }
    $tags = @(Find-TestsTag -Root $repo | Sort-Object Source)
    @($tags | ForEach-Object { "$($_.Source):$($_.Line):$($_.Text)" }) | Should -Be @('src/P/A.cs:1:a.cs', 'src/P/Migrations/1.sql:2:b.cs')
  }
}

Describe 'Read-TestsTagBaseline' {
  It 'reads entries, skipping comments and blank lines, keeping a reason that holds a separator' {
    $path = Join-Path -Path $TestDrive -ChildPath 'baseline-ok.txt'
    Set-Content -Path $path -Value @('# header', '', '  src/A.cs | tests/X.cs:M | gone | really gone  ', 'src/B.cs|tests/Y.cs|moved')
    $baseline = Read-TestsTagBaseline -Path $path
    $baseline.Count | Should -Be 2
    $baseline['src/A.cs|tests/X.cs:M'].Reason | Should -Be 'gone | really gone'
    $baseline['src/A.cs|tests/X.cs:M'].Line | Should -Be 3
    $baseline['src/B.cs|tests/Y.cs'].Text | Should -Be 'tests/Y.cs'
  }

  It 'treats a missing baseline file as empty' {
    (Read-TestsTagBaseline -Path (Join-Path -Path $TestDrive -ChildPath 'no-such-baseline.txt')).Count | Should -Be 0
  }

  It 'refuses <Why>' -ForEach @(
    @{ Lines = @('src/A.cs | tests/X.cs'); Why = 'an entry without a reason'; Message = '*source | tag | reason*' },
    @{ Lines = @('src/A.cs | tests/X.cs | '); Why = 'an empty reason'; Message = '*source | tag | reason*' },
    @{ Lines = @(' | tests/X.cs | gone'); Why = 'an empty source'; Message = '*source | tag | reason*' },
    @{ Lines = @('src/A.cs | tests/X.cs | gone', 'src/A.cs | tests/X.cs | again'); Why = 'a duplicate entry'; Message = '*duplicates line 1*' }
  ) {
    $path = Join-Path -Path $TestDrive -ChildPath "baseline-$([guid]::NewGuid().ToString('N')).txt"
    Set-Content -Path $path -Value $Lines
    { Read-TestsTagBaseline -Path $path } | Should -Throw -ExpectedMessage $Message
  }
}

Describe 'Compare-TestsTagBaseline' {
  It 'separates unbaselined breaks from baselined ones and explains each stale entry' {
    $tags = @(
      [pscustomobject]@{ Source = 'src/A.cs'; Line = 1; Text = 'tests/New.cs'; Closed = $true; Reason = 'missing file' },
      [pscustomobject]@{ Source = 'src/A.cs'; Line = 2; Text = 'tests/Known.cs'; Closed = $true; Reason = 'missing file' },
      [pscustomobject]@{ Source = 'src/A.cs'; Line = 3; Text = 'tests/Fixed.cs'; Closed = $true; Reason = $null }
    )
    $path = Join-Path -Path $TestDrive -ChildPath 'baseline-compare.txt'
    Set-Content -Path $path -Value @('src/A.cs | tests/Known.cs | known', 'src/A.cs | tests/Fixed.cs | was broken', 'src/Gone.cs | tests/Old.cs | file removed')
    $comparison = Compare-TestsTagBaseline -Tags $tags -Baseline (Read-TestsTagBaseline -Path $path)
    @($comparison.Unbaselined | ForEach-Object Text) | Should -Be @('tests/New.cs')
    @($comparison.Stale | ForEach-Object { "$($_.Entry.Text): $($_.Why)" }) | Should -Be @(
      'tests/Fixed.cs: the tag now resolves; delete the entry',
      'tests/Old.cs: no such tag in that file any more; delete the entry'
    )
  }

  It 'reports nothing for a clean scan with an empty baseline' {
    $comparison = Compare-TestsTagBaseline -Tags @() -Baseline (Read-TestsTagBaseline -Path (Join-Path -Path $TestDrive -ChildPath 'none.txt'))
    $comparison.Unbaselined.Count | Should -Be 0
    $comparison.Stale.Count | Should -Be 0
  }
}

Describe 'running the guard' {
  It 'passes a repository whose tags all resolve, counting placeholders' {
    $repo = New-FakeRepo @{
      'src/P/A.cs'               = "/// <tests>tests/T/WidgetTests.cs:Plain_DoesThingAsync</tests>`n/// <tests>No tests found</tests>"
      'tests/T/WidgetTests.cs'   = $script:TestFile
    }
    $output = & $script:ScriptPath -Root $repo -BaselinePath (Join-Path -Path $repo -ChildPath 'none.txt')
    $LASTEXITCODE | Should -Be 0
    ($output -join "`n") | Should -Match 'Checked 2 <tests> tags \(1 "No tests found"\): 0 broken, 0 baselined, 0 stale baseline entries\.'
  }

  It 'fails and lists every unbaselined break with its file and line, ignoring baselined ones' {
    $repo = New-FakeRepo @{
      'src/P/A.cs'             = "/// <tests>tests/T/Gone.cs</tests>`n/// <tests>tests/T/WidgetTests.cs:NotThereAtAll</tests>`n/// <tests>tests/T/Known.cs</tests>"
      'tests/T/WidgetTests.cs' = $script:TestFile
    }
    $baseline = Join-Path -Path $repo -ChildPath 'baseline.txt'
    Set-Content -Path $baseline -Value 'src/P/A.cs | tests/T/Known.cs | tracked elsewhere'
    $output = & $script:ScriptPath -Root $repo -BaselinePath $baseline
    $LASTEXITCODE | Should -Be 1
    $text = $output -join "`n"
    $text | Should -Match 'src/P/A\.cs:1: <tests>tests/T/Gone\.cs</tests>: missing file'
    $text | Should -Match 'src/P/A\.cs:2: <tests>tests/T/WidgetTests\.cs:NotThereAtAll</tests>: missing method'
    $text | Should -Not -Match 'Known\.cs</tests>:'
    $text | Should -Match '3 broken, 1 baselined, 0 stale'
  }

  It 'fails on a stale baseline entry even when nothing is broken' {
    $repo = New-FakeRepo @{ 'src/P/A.cs' = '/// <tests>tests/T/WidgetTests.cs</tests>'; 'tests/T/WidgetTests.cs' = $script:TestFile }
    $baseline = Join-Path -Path $repo -ChildPath 'baseline.txt'
    Set-Content -Path $baseline -Value @('# known breaks', 'src/P/A.cs | tests/T/WidgetTests.cs | was missing')
    $output = & $script:ScriptPath -Root $repo -BaselinePath $baseline
    $LASTEXITCODE | Should -Be 1
    ($output -join "`n") | Should -Match 'baseline\.txt:2: stale entry ''src/P/A\.cs \| tests/T/WidgetTests\.cs'': the tag now resolves'
  }
}

Describe 'the tests tags in this repository' {
  It 'all resolve or are baselined, and the baseline holds nothing stale' {
    $output = & $script:ScriptPath
    $LASTEXITCODE | Should -Be 0 -Because ($output -join "`n")
  }

  It 'are found in their thousands, in both C# and SQL, so a scan that finds nothing cannot pass' {
    $tags = @(Find-TestsTag -Root $script:RepoRoot)
    $tags.Count | Should -BeGreaterThan 5000
    @($tags | Where-Object { $_.Source.EndsWith('.sql') }).Count | Should -BeGreaterThan 10
    $cache = New-TestsTagCache
    $broken = @($tags | Where-Object { $null -ne (Resolve-TestsTag -Tag $_ -Root $script:RepoRoot -Cache $cache) })
    $broken.Count | Should -BeGreaterThan 0 -Because 'the baseline lists known breaks, so the resolver must still see them'
    $broken.Count | Should -BeLessThan 100
  }
}
