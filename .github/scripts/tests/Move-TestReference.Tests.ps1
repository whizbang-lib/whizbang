#Requires -Modules Pester

BeforeAll {
  $script:ScriptPath = Join-Path -Path $PSScriptRoot -ChildPath '../../../scripts/Move-TestReference.ps1' -Resolve
  . $script:ScriptPath

  function New-Move([string]$OldPath, [string]$NewPath) {
    [pscustomobject]@{ From = $OldPath; To = $NewPath }
  }

  function Update-Text([string]$Text, [object[]]$MoveList) {
    Update-PathReference -Text $Text -Rewriter (New-PathRewriter $MoveList)
  }

  # Writes exact bytes: UTF-8, with a byte order mark only when asked.
  function Set-FixtureFile([string]$Root, [string]$Relative, [string]$Text, [switch]$Bom) {
    $path = Join-Path -Path $Root -ChildPath $Relative
    New-Item -ItemType Directory -Path (Split-Path -Path $path -Parent) -Force | Out-Null
    $encoding = [System.Text.UTF8Encoding]::new([bool]$Bom)
    [System.IO.File]::WriteAllBytes($path, [byte[]]($encoding.GetPreamble() + $encoding.GetBytes($Text)))
  }

  function Set-FixtureBytes([string]$Root, [string]$Relative, [byte[]]$Bytes) {
    $path = Join-Path -Path $Root -ChildPath $Relative
    New-Item -ItemType Directory -Path (Split-Path -Path $path -Parent) -Force | Out-Null
    [System.IO.File]::WriteAllBytes($path, $Bytes)
  }

  function Get-FixtureText([string]$Root, [string]$Relative) {
    [System.IO.File]::ReadAllText((Join-Path -Path $Root -ChildPath $Relative))
  }

  # A file's bytes as hex, so a comparison is one string: a byte array piped into Should arrives as
  # one nested item and never equals the expected array, however equal the bytes are.
  function Get-FixtureBytes([string]$Root, [string]$Relative) {
    [System.Convert]::ToHexString([System.IO.File]::ReadAllBytes((Join-Path -Path $Root -ChildPath $Relative)))
  }

  function ConvertTo-Hex([byte[]]$Bytes) {
    [System.Convert]::ToHexString($Bytes)
  }

  # One line per file: relative path and SHA-256, so a before/after comparison catches any write.
  function Get-TreeHash([string]$Root) {
    @(Get-ChildItem -LiteralPath $Root -Recurse -File -Force | Sort-Object -Property FullName | ForEach-Object {
        '{0} {1}' -f $_.FullName.Substring($Root.Length), (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
      })
  }

  $script:OldFoo = 'tests/A.Tests/Foo.cs'
  $script:NewFoo = 'tests/A.Component.Tests/Foo.cs'

  # A fake library (Base/whizbang) and a fake docs site next to it (Base/whizbang-lib.github.io).
  function New-Fixture([switch]$NoDocsSite) {
    $base = Join-Path -Path $TestDrive -ChildPath ([guid]::NewGuid().ToString('N'))
    $lib = Join-Path -Path $base -ChildPath 'whizbang'
    $docs = Join-Path -Path $base -ChildPath 'whizbang-lib.github.io'

    # Source: C# (BOM, CRLF), SQL (no trailing newline), a plain comment, binary and non-UTF-8 files.
    Set-FixtureFile $lib 'src/Core/Foo.cs' "namespace X;`r`n/// <tests>tests/A.Tests/Foo.cs:BarAsync</tests>`r`n/// <tests>tests/A.Tests/FooBar.cs</tests>`r`n/// <tests>tests/A.Tests.Extra/Foo.cs</tests>`r`npublic class Foo { }`r`n" -Bom
    Set-FixtureFile $lib 'src/Core/Untouched.cs' "/// <tests>tests/A.Tests/FooBar.cs</tests>`n/// <tests>No tests found</tests>`n"
    Set-FixtureFile $lib 'src/Data/Migrations/001_Init.sql' "-- <tests>tests/A.Tests/Foo.cs:BarAsync</tests>`n-- <tests>tests/A.Tests/Sub/Deep.cs</tests>`nSELECT 1;"
    Set-FixtureFile $lib 'src/Core/Notes.cs' "// Lock-in test: tests/A.Tests/Foo.cs`n"
    Set-FixtureBytes $lib 'src/Core/logo.bin' ([byte[]](@(0, 1, 2) + [System.Text.Encoding]::ASCII.GetBytes('tests/A.Tests/Foo.cs')))
    Set-FixtureBytes $lib 'src/Core/Latin.txt' ([byte[]](@(0xE9, 0x20) + [System.Text.Encoding]::ASCII.GetBytes('tests/A.Tests/Foo.cs')))
    Set-FixtureBytes $lib 'src/Core/LatinNoRef.txt' ([byte[]]@(0xE9, 0x20, 0x41))
    Set-FixtureFile $lib 'src/Core/bin/Debug/Foo.xml' '<tests>tests/A.Tests/Foo.cs</tests>'
    Set-FixtureFile $lib 'src/Core/obj/Gen.cs' '/// <tests>tests/A.Tests/Foo.cs</tests>'

    # Tests: two projects that share a name prefix, and the target project.
    Set-FixtureFile $lib 'tests/A.Tests/A.Tests.csproj' '<Project />'
    Set-FixtureFile $lib 'tests/A.Tests/Foo.cs' "public class FooTests { }`npublic sealed record class FooRecord;`n"
    Set-FixtureFile $lib 'tests/A.Tests/FooBar.cs' 'public class FooBarTests { }'
    Set-FixtureFile $lib 'tests/A.Tests/Sub/Deep.cs' 'public partial class DeepTests { }'
    Set-FixtureFile $lib 'tests/A.Tests/README.md' "Run tests/A.Tests/Foo.cs`n"
    Set-FixtureFile $lib 'tests/A.Tests.Extra/A.Tests.Extra.csproj' '<Project />'
    Set-FixtureFile $lib 'tests/A.Tests.Extra/Foo.cs' 'public class FooTests { }'
    Set-FixtureFile $lib 'tests/A.Component.Tests/A.Component.Tests.csproj' '<Project />'

    # Library docs in scope, and look-alikes out of scope.
    Set-FixtureFile $lib 'ai-docs/testing.md' "See ``tests/A.Tests/Foo.cs`` and tests/A.Tests/FooBar.cs.`n"
    Set-FixtureFile $lib 'ai-docs/data.json' '{"t":"tests/A.Tests/Foo.cs"}'
    Set-FixtureFile $lib 'README.md' "tests/A.Tests/Foo.cs`r`n"
    Set-FixtureFile $lib 'docs/guide.md' "[foo](../tests/A.Tests/Foo.cs)`n"
    Set-FixtureFile $lib 'docs/notes.txt' "tests/A.Tests/Foo.cs`n"
    Set-FixtureFile $lib 'plans/plan.md' "- tests/A.Tests/Foo.cs:BarAsync`n"
    Set-FixtureFile $lib 'plans/data.yml' "t: tests/A.Tests/Foo.cs`n"
    Set-FixtureFile $lib 'CLAUDE.md' "tests/A.Tests/Foo.cs`n"
    Set-FixtureFile $lib 'samples/App/CLAUDE.md' 'tests/A.Tests/Foo.cs'
    Set-FixtureFile $lib 'other/notes.md' 'tests/A.Tests/Foo.cs'
    Set-FixtureFile $lib 'scripts/Run.ps1' '# tests/A.Tests/Foo.cs'
    Set-FixtureFile $lib 'node_modules/pkg/README.md' 'tests/A.Tests/Foo.cs'
    Set-FixtureFile $lib '.claude/worktrees/w/README.md' 'tests/A.Tests/Foo.cs'

    if (-not $NoDocsSite) {
      Set-FixtureFile $docs 'src/assets/docs/v1.0.0/page.md' ("---`ntestReferences:`n  - tests/A.Tests/Foo.cs`n  - tests/A.Tests/FooBar.cs`n---`n" +
        "``````csharp{title=""x"" tests=[""FooTests.BarAsync"", ""OtherTests.XAsync""]}`n``````  `n" +
        "Text {verified: FooTests.BarAsync, FooTests.BazAsync}`n")
      Set-FixtureFile $docs 'src/assets/docs/v1.0.0/other.md' "{verified: FooBarTests.XAsync}`n"
      Set-FixtureFile $docs 'src/scripts/generate-code-maps.test.mjs' "const missing = '<tests>tests/A.Tests/Foo.cs</tests>';`n"
      Set-FixtureFile $docs 'ai-docs/CODE-TEST-LINKING.md' "`"testFile`": `"tests/A.Tests/Foo.cs`"`n" -Bom
      Set-FixtureFile $docs 'src/assets/code-tests-map.json' '{"testFile":"tests/A.Tests/Foo.cs"}'
      Set-FixtureFile $docs 'src/assets/data/test-status/A.Tests.json' '{"FooTests.BarAsync":{"o":"passed"}}'
      Set-FixtureFile $docs 'src/assets/data/test-status/index.json' '{}'
      Set-FixtureFile $docs 'src/static/docs.html' '<p>tests/A.Tests/Foo.cs</p>'
      Set-FixtureFile $docs 'audit-reports/stale.txt' 'Changed files: tests/A.Tests/Foo.cs'
      Set-FixtureFile $docs 'src/assets/docs-index.json' '{}'
      Set-FixtureBytes $docs 'src/assets/logo.png' ([byte[]]@(0x89, 0, 0x50))
      Set-FixtureBytes $docs 'src/assets/legacy.txt' ([byte[]](@(0xE9, 0x20) + [System.Text.Encoding]::ASCII.GetBytes('tests/A.Tests/Foo.cs')))
      Set-FixtureFile $docs 'node_modules/x/README.md' 'tests/A.Tests/Foo.cs'
      Set-FixtureFile $docs 'dist/page.md' 'tests/A.Tests/Foo.cs'
    }
    [pscustomobject]@{ Base = $base; Library = $lib; Docs = $docs }
  }

  # Resolves and applies moves the way the script does, quietly.
  function Invoke-Fixture([object]$Fixture, [object[]]$MoveList, [switch]$UpdateDocsSite, [switch]$DryRun) {
    $resolved = @(Resolve-TestMove -RepositoryRoot $Fixture.Library -From @($MoveList | ForEach-Object From) -To @($MoveList | ForEach-Object To))
    Invoke-TestReferenceMove -RepositoryRoot $Fixture.Library -Moves $resolved -DocsSiteRoot $Fixture.Docs -UpdateDocsSite:$UpdateDocsSite -DryRun:$DryRun -WarningAction SilentlyContinue
  }

  function Get-RegeneratePath([object]$Result) { @($Result.Regenerate | ForEach-Object { $_.Path }) }
  function Get-ChangePath([object]$Result, [string]$Repository) { @($Result.Changes | Where-Object { $_.Repository -eq $Repository } | ForEach-Object { $_.Path }) }
}

Describe 'ConvertTo-RepoRelativePath' {
  It 'turns backslashes into forward slashes and drops spaces, leading ./ and a trailing slash' {
    ConvertTo-RepoRelativePath '.\tests\A.Tests\Foo.cs' | Should -Be 'tests/A.Tests/Foo.cs'
    ConvertTo-RepoRelativePath '././tests/A.Tests/' | Should -Be 'tests/A.Tests'
    ConvertTo-RepoRelativePath '  tests/A.Tests  ' | Should -Be 'tests/A.Tests'
    ConvertTo-RepoRelativePath 'tests/A.Tests/Foo.cs' | Should -Be 'tests/A.Tests/Foo.cs'
  }
}

Describe 'Get-TestProjectName' {
  It 'names the folder right after the first tests folder' {
    Get-TestProjectName 'tests/A.Tests/Sub/Foo.cs' 'File' | Should -Be 'A.Tests'
    Get-TestProjectName 'samples/App/tests/App.Tests/Foo.cs' 'File' | Should -Be 'App.Tests'
    Get-TestProjectName 'tests/A.Tests' 'Directory' | Should -Be 'A.Tests'
    Get-TestProjectName 'tests/A.Tests/tests/Foo.cs' 'File' | Should -Be 'A.Tests'
  }

  It 'returns nothing outside a test project' {
    Get-TestProjectName 'tests/Foo.cs' 'File' | Should -BeNullOrEmpty
    Get-TestProjectName 'tests' 'Directory' | Should -BeNullOrEmpty
    Get-TestProjectName 'src/Core/Foo.cs' 'File' | Should -BeNullOrEmpty
  }
}

Describe 'Update-PathReference' {
  It 'rewrites a file path, keeping a :Method suffix and sentence punctuation' {
    $r = Update-Text 'see tests/A.Tests/Foo.cs:BarAsync and tests/A.Tests/Foo.cs.' @(New-Move $OldFoo $NewFoo)
    $r.Text | Should -Be 'see tests/A.Component.Tests/Foo.cs:BarAsync and tests/A.Component.Tests/Foo.cs.'
    $r.Count | Should -Be 2
  }

  It 'does not touch a longer file name that starts with the moved one' {
    $text = 'tests/A.Tests/FooBar.cs tests/A.Tests/Foo.csx tests/A.Tests/Foo.cs.bak tests/A.Tests/Foo.cs-old tests/A.Tests/Foo.cs_1'
    $r = Update-Text $text @(New-Move $OldFoo $NewFoo)
    $r.Text | Should -Be $text
    $r.Count | Should -Be 0
  }

  It 'does not touch a path that merely ends with the moved one' {
    $text = 'xtests/A.Tests/Foo.cs my.tests/A.Tests/Foo.cs my-tests/A.Tests/Foo.cs my_tests/A.Tests/Foo.cs'
    (Update-Text $text @(New-Move $OldFoo $NewFoo)).Text | Should -Be $text
  }

  It 'rewrites relative, quoted and URL forms' {
    $r = Update-Text '[x](../tests/A.Tests/Foo.cs) "tests/A.Tests/Foo.cs" https://example.test/blob/develop/tests/A.Tests/Foo.cs#L3' @(New-Move $OldFoo $NewFoo)
    $r.Text | Should -Be '[x](../tests/A.Component.Tests/Foo.cs) "tests/A.Component.Tests/Foo.cs" https://example.test/blob/develop/tests/A.Component.Tests/Foo.cs#L3'
    $r.Count | Should -Be 3
  }

  It 'rewrites a folder prefix and a bare folder mention, but not a sibling folder sharing the prefix' {
    $text = 'tests/A.Tests/Foo.cs `tests/A.Tests` tests/A.Tests.Extra/Foo.cs tests/A.TestsX/Foo.cs tests/A.Tests/Sub/Bar.cs'
    $r = Update-Text $text @(New-Move 'tests/A.Tests' 'tests/A.Component.Tests')
    $r.Text | Should -Be 'tests/A.Component.Tests/Foo.cs `tests/A.Component.Tests` tests/A.Tests.Extra/Foo.cs tests/A.TestsX/Foo.cs tests/A.Component.Tests/Sub/Bar.cs'
    $r.Count | Should -Be 3
  }

  It 'applies several moves in one pass, the most specific first, without chaining' {
    $moves = @(
      (New-Move 'tests/A.Tests' 'tests/B.Tests'),
      (New-Move 'tests/A.Tests/Keep.cs' 'tests/C.Tests/Keep.cs'),
      (New-Move 'tests/B.Tests/Old.cs' 'tests/A.Tests/Old.cs')
    )
    $r = Update-Text 'tests/A.Tests/X.cs tests/A.Tests/Keep.cs tests/B.Tests/Old.cs tests/A.Tests/Keep.csx' $moves
    $r.Text | Should -Be 'tests/B.Tests/X.cs tests/C.Tests/Keep.cs tests/A.Tests/Old.cs tests/B.Tests/Keep.csx'
    $r.Count | Should -Be 4
  }

  It 'is case-sensitive, like the repository paths' {
    $text = 'TESTS/A.Tests/Foo.cs tests/a.tests/foo.cs'
    (Update-Text $text @(New-Move $OldFoo $NewFoo)).Text | Should -Be $text
  }

  It 'treats regex characters in a path literally' {
    $r = Update-Text 'tests/A+B.Tests/F(1).cs tests/AxB.Tests/F(1).cs' @(New-Move 'tests/A+B.Tests/F(1).cs' 'tests/C.Tests/F(1).cs')
    $r.Text | Should -Be 'tests/C.Tests/F(1).cs tests/AxB.Tests/F(1).cs'
  }
}

Describe 'Update-SourceReference' {
  It 'counts tests tags and other references apart, in C# and SQL comments, keeping line endings' {
    $text = "/// <tests>tests/A.Tests/Foo.cs:BarAsync</tests>`r`n/// <tests>tests/A.Tests/FooBar.cs</tests>`n-- <tests>tests/A.Tests/Foo.cs</tests>`n// see tests/A.Tests/Foo.cs`n"
    $r = Update-SourceReference -Text $text -Rewriter (New-PathRewriter @(New-Move $OldFoo $NewFoo))
    $r.Text | Should -Be "/// <tests>tests/A.Component.Tests/Foo.cs:BarAsync</tests>`r`n/// <tests>tests/A.Tests/FooBar.cs</tests>`n-- <tests>tests/A.Component.Tests/Foo.cs</tests>`n// see tests/A.Component.Tests/Foo.cs`n"
    $r.Tags | Should -Be 2
    $r.Other | Should -Be 1
  }

  It 'counts a tag once however many moved paths it holds' {
    $r = Update-SourceReference -Text '<tests>tests/A.Tests/Foo.cs tests/A.Tests/Foo.cs</tests>' -Rewriter (New-PathRewriter @(New-Move $OldFoo $NewFoo))
    $r.Text | Should -Be '<tests>tests/A.Component.Tests/Foo.cs tests/A.Component.Tests/Foo.cs</tests>'
    $r.Tags | Should -Be 1
    $r.Other | Should -Be 0
  }

  It 'leaves an unclosed or unrelated tests-tag mention alone' {
    $text = 'a `<tests>` tag in prose; <tests>No tests found</tests>'
    $r = Update-SourceReference -Text $text -Rewriter (New-PathRewriter @(New-Move $OldFoo $NewFoo))
    $r.Text | Should -Be $text
    $r.Tags | Should -Be 0
    $r.Other | Should -Be 0
  }
}

Describe 'Read-TextFile and Write-TextFile' {
  It 'round-trips UTF-8 with and without a byte order mark, byte for byte' {
    $root = Join-Path -Path $TestDrive -ChildPath ([guid]::NewGuid().ToString('N'))
    Set-FixtureFile $root 'bom.txt' "caf$([char]0xE9)`r`nline" -Bom
    Set-FixtureFile $root 'plain.txt' "caf$([char]0xE9)`n"
    Set-FixtureFile $root 'empty.txt' ''
    foreach ($name in 'bom.txt', 'plain.txt', 'empty.txt') {
      $before = Get-FixtureBytes $root $name
      $file = Read-TextFile (Join-Path -Path $root -ChildPath $name)
      $file.Valid | Should -BeTrue
      Write-TextFile -Path (Join-Path -Path $root -ChildPath $name) -Text $file.Text -Bom $file.Bom
      Get-FixtureBytes $root $name | Should -Be $before
    }
    (Read-TextFile (Join-Path -Path $root -ChildPath 'bom.txt')).Bom | Should -BeTrue
    (Read-TextFile (Join-Path -Path $root -ChildPath 'bom.txt')).Text | Should -Be "caf$([char]0xE9)`r`nline"
    (Read-TextFile (Join-Path -Path $root -ChildPath 'plain.txt')).Bom | Should -BeFalse
  }

  It 'returns nothing for a binary file and an invalid reading for bytes that are not UTF-8' {
    $root = Join-Path -Path $TestDrive -ChildPath ([guid]::NewGuid().ToString('N'))
    Set-FixtureBytes $root 'bin.dat' ([byte[]]@(0x41, 0, 0x42))
    Set-FixtureBytes $root 'latin.txt' ([byte[]]@(0x63, 0xE9))
    Read-TextFile (Join-Path -Path $root -ChildPath 'bin.dat') | Should -BeNullOrEmpty
    $latin = Read-TextFile (Join-Path -Path $root -ChildPath 'latin.txt')
    $latin.Valid | Should -BeFalse
    $latin.Text | Should -Be "c$([char]0xE9)"
  }
}

Describe 'Get-LibraryFileCategory' {
  It 'puts src under Source and the doc locations under Doc, and nothing else' {
    Get-LibraryFileCategory 'src/Core/Foo.cs' | Should -Be 'Source'
    Get-LibraryFileCategory 'src/Core/README.md' | Should -Be 'Source'
    Get-LibraryFileCategory 'ai-docs/data.json' | Should -Be 'Doc'
    Get-LibraryFileCategory 'README.md' | Should -Be 'Doc'
    Get-LibraryFileCategory 'tests/A.Tests/README-local.md' | Should -Be 'Doc'
    Get-LibraryFileCategory 'samples/App/CLAUDE.md' | Should -Be 'Doc'
    Get-LibraryFileCategory 'plans/archive/p.md' | Should -Be 'Doc'
    Get-LibraryFileCategory 'docs/guide.md' | Should -Be 'Doc'
    Get-LibraryFileCategory 'docs/notes.txt' | Should -BeNullOrEmpty
    Get-LibraryFileCategory 'plans/data.yml' | Should -BeNullOrEmpty
    Get-LibraryFileCategory 'other/notes.md' | Should -BeNullOrEmpty
    Get-LibraryFileCategory 'tests/A.Tests/Foo.cs' | Should -BeNullOrEmpty
  }
}

Describe 'Get-DocsSiteGeneratedRule' {
  It 'knows the generated files and folders, and nothing hand-written' {
    (Get-DocsSiteGeneratedRule 'src/assets/code-tests-map.json').Generator | Should -BeLike '*generate-code-tests-map.mjs'
    (Get-DocsSiteGeneratedRule 'src/assets/vscode-feed.json').Generator | Should -BeLike '*generate-vscode-feed.mjs'
    (Get-DocsSiteGeneratedRule 'src/assets/data/test-status/A.Tests.json').Generator | Should -BeLike '*build-test-status.mjs*'
    (Get-DocsSiteGeneratedRule 'src/static/docs.html').Generator | Should -BeLike '*gen-static-docs.mjs'
    (Get-DocsSiteGeneratedRule 'audit-reports/x/y.json').Generator | Should -BeLike '*audit-baseline.mjs*'
    Get-DocsSiteGeneratedRule 'src/assets/docs/v1.0.0/page.md' | Should -BeNullOrEmpty
    Get-DocsSiteGeneratedRule 'src/assets/code-tests-map.json.bak' | Should -BeNullOrEmpty
  }
}

Describe 'Test-DocsSiteCodeFile' {
  It 'treats script code as code and docs as docs' {
    Test-DocsSiteCodeFile 'src/scripts/generate-code-maps.test.mjs' | Should -BeTrue
    Test-DocsSiteCodeFile 'src/app/x.ts' | Should -BeTrue
    Test-DocsSiteCodeFile 'src/assets/docs/v1.0.0/page.md' | Should -BeFalse
    Test-DocsSiteCodeFile '.github/workflows/ci.yml' | Should -BeFalse
  }
}

Describe 'Measure-ClassMarker' {
  It 'counts tests=[...] and {verified: ...} entries that name a moved class, and only those' {
    $classes = [System.Collections.Generic.HashSet[string]]::new([string[]]@('FooTests'), [System.StringComparer]::Ordinal)
    $text = 'x tests=["FooTests.BarAsync", "OtherTests.XAsync"] {verified: FooTests.A, NS.FooTests.B, FooTestsX.C} tests: FooTests.D FooTests.E'
    Measure-ClassMarker -Text $text -ClassName $classes | Should -Be 2
  }
}

Describe 'Get-MovedClassName' {
  It 'reads class and record names from a file, or from every .cs file under a folder' {
    $fixture = New-Fixture
    @(Get-MovedClassName -Path (Join-Path $fixture.Library $OldFoo) -Kind 'File') | Should -Be @('FooRecord', 'FooTests')
    @(Get-MovedClassName -Path (Join-Path $fixture.Library 'tests/A.Tests') -Kind 'Directory') | Should -Be @('DeepTests', 'FooBarTests', 'FooRecord', 'FooTests')
  }
}

Describe 'Get-RepositoryFile' {
  It 'lists files as forward-slash relative paths, skipping build output, dependencies and worktrees' {
    $fixture = New-Fixture
    $files = @(Get-RepositoryFile -Root $fixture.Library -ExcludeRelative '.claude/worktrees')
    $files | Should -Contain 'src/Core/Foo.cs'
    $files | Should -Contain 'tests/A.Tests/Sub/Deep.cs'
    @($files | Where-Object { $_ -match '(^|/)(bin|obj|node_modules)/|\.claude/worktrees' }).Count | Should -Be 0
  }
}

Describe 'Resolve-TestMove' {
  BeforeAll { $script:Fx = New-Fixture }

  It 'resolves a file move: kind, projects and declared classes' {
    $move = @(Resolve-TestMove -RepositoryRoot $Fx.Library -From '.\tests\A.Tests\Foo.cs' -To 'tests/A.Component.Tests/Foo.cs')
    $move.Count | Should -Be 1
    $move[0].From | Should -Be $OldFoo
    $move[0].Kind | Should -Be 'File'
    $move[0].OldProject | Should -Be 'A.Tests'
    $move[0].NewProject | Should -Be 'A.Component.Tests'
    $move[0].Classes | Should -Be @('FooRecord', 'FooTests')
  }

  It 'resolves a folder move' {
    $move = @(Resolve-TestMove -RepositoryRoot $Fx.Library -From 'tests/A.Tests/' -To 'tests/A.Unit.Tests')
    $move[0].Kind | Should -Be 'Directory'
    $move[0].NewProject | Should -Be 'A.Unit.Tests'
  }

  It 'accepts a move already made (only the new path exists)' {
    $fixture = New-Fixture
    Move-Item -LiteralPath (Join-Path $fixture.Library $OldFoo) -Destination (Join-Path $fixture.Library $NewFoo)
    $move = @(Resolve-TestMove -RepositoryRoot $fixture.Library -From $OldFoo -To $NewFoo)
    $move[0].Kind | Should -Be 'File'
    $move[0].Classes | Should -Be @('FooRecord', 'FooTests')
  }

  It 'combines -From/-To pairs with map-file rows' {
    $map = Join-Path $TestDrive 'moves.csv'
    Set-Content -LiteralPath $map -Value @('From,To', 'tests/A.Tests/FooBar.cs,tests/A.Component.Tests/FooBar.cs', 'tests/A.Tests/Sub,tests/A.Component.Tests/Sub')
    $moves = @(Resolve-TestMove -RepositoryRoot $Fx.Library -From $OldFoo -To $NewFoo -MapFile $map)
    @($moves | ForEach-Object From) | Should -Be @($OldFoo, 'tests/A.Tests/FooBar.cs', 'tests/A.Tests/Sub')
    $moves[2].Kind | Should -Be 'Directory'
  }

  It 'accepts a header-only map file alongside -From/-To' {
    $map = Join-Path $TestDrive 'empty.csv'
    Set-Content -LiteralPath $map -Value 'From,To'
    @(Resolve-TestMove -RepositoryRoot $Fx.Library -From $OldFoo -To $NewFoo -MapFile $map).Count | Should -Be 1
  }

  It 'stops on <Case>' -ForEach @(
    @{ Case = 'an unknown path'; From = @('tests/A.Tests/Fooo.cs'); To = @('tests/B.Tests/Fooo.cs'); Map = $null; Message = "Neither 'tests/A.Tests/Fooo.cs' nor 'tests/B.Tests/Fooo.cs' exists*" }
    @{ Case = 'unpaired paths'; From = @('tests/A.Tests/Foo.cs', 'tests/A.Tests/FooBar.cs'); To = @('tests/B.Tests/Foo.cs'); Map = $null; Message = '-From has 2 path(s) and -To has 1*' }
    @{ Case = 'no move'; From = @(); To = @(); Map = $null; Message = 'No move given*' }
    @{ Case = 'the same path on both sides'; From = @('tests/A.Tests/Foo.cs'); To = @('./tests/A.Tests/Foo.cs'); Map = $null; Message = "Move 'tests/A.Tests/Foo.cs' names the same path*" }
    @{ Case = 'a repeated move'; From = @('tests/A.Tests/Foo.cs', 'tests\A.Tests\Foo.cs'); To = @('tests/B.Tests/Foo.cs', 'tests/C.Tests/Foo.cs'); Map = $null; Message = "'tests/A.Tests/Foo.cs' is moved more than once*" }
    @{ Case = 'a missing map file'; From = @(); To = @(); Map = 'missing.csv'; Message = 'Map file not found*' }
    @{ Case = 'a map file without From and To columns'; From = @(); To = @(); Map = 'Old,New|a,b'; Message = '*needs From and To columns; it has: Old, New*' }
    @{ Case = 'a map row with an empty side'; From = @(); To = @(); Map = 'From,To|tests/A.Tests/Foo.cs,'; Message = 'A move has an empty side*' }
  ) {
    $mapPath = $null
    if ($Map) {
      $mapPath = Join-Path $TestDrive ([guid]::NewGuid().ToString('N') + '.csv')
      if ($Map -ne 'missing.csv') { Set-Content -LiteralPath $mapPath -Value ($Map -split '\|') }
    }
    { Resolve-TestMove -RepositoryRoot $Fx.Library -From $From -To $To -MapFile $mapPath } | Should -Throw $Message
  }
}

Describe 'Resolve-DocsSiteRoot' {
  It 'uses the sibling docs-site checkout by default' {
    $fixture = New-Fixture
    Resolve-DocsSiteRoot -RepositoryRoot $fixture.Library | Should -Be $fixture.Docs
  }

  It 'uses a given docs-site checkout' {
    $fixture = New-Fixture
    $other = Join-Path $fixture.Base 'elsewhere'
    New-Item -ItemType Directory -Path $other | Out-Null
    Resolve-DocsSiteRoot -RepositoryRoot $fixture.Library -DocsSiteRoot $other | Should -Be $other
  }

  It 'warns and skips the docs site when the default checkout is missing' {
    $fixture = New-Fixture -NoDocsSite
    $output = @(Resolve-DocsSiteRoot -RepositoryRoot $fixture.Library 3>&1)
    @($output | Where-Object { $_ -is [System.Management.Automation.WarningRecord] } | ForEach-Object Message) | Should -BeLike '*Docs site not found*'
    @($output | Where-Object { $_ -isnot [System.Management.Automation.WarningRecord] -and $null -ne $_ }).Count | Should -Be 0
  }

  It 'stops when a given checkout is missing, or when -UpdateDocsSite has nothing to update' {
    $fixture = New-Fixture -NoDocsSite
    { Resolve-DocsSiteRoot -RepositoryRoot $fixture.Library -DocsSiteRoot (Join-Path $fixture.Base 'nope') } | Should -Throw '*Docs site not found*'
    { Resolve-DocsSiteRoot -RepositoryRoot $fixture.Library -UpdateDocsSite } | Should -Throw '*Docs site not found*'
  }
}

Describe 'Invoke-TestReferenceMove: a file move' {
  BeforeAll {
    $script:Fx = New-Fixture
    $script:DocsBefore = Get-TreeHash $Fx.Docs
    $script:Result = Invoke-Fixture $Fx @(New-Move $OldFoo $NewFoo)
  }

  It 'rewrites tests tags in C# and SQL, and other source references, counting them apart' {
    $Result.TagsRewritten | Should -Be 2
    $Result.TagFiles | Should -Be 2
    $Result.SourceReferencesRewritten | Should -Be 1
    $Result.SourceReferenceFiles | Should -Be 1
    Get-FixtureText $Fx.Library 'src/Data/Migrations/001_Init.sql' | Should -Be "-- <tests>tests/A.Component.Tests/Foo.cs:BarAsync</tests>`n-- <tests>tests/A.Tests/Sub/Deep.cs</tests>`nSELECT 1;"
    Get-FixtureText $Fx.Library 'src/Core/Notes.cs' | Should -Be "// Lock-in test: tests/A.Component.Tests/Foo.cs`n"
  }

  It 'keeps a rewritten file''s byte order mark, line endings and trailing newline exactly' {
    $expected = [System.Text.UTF8Encoding]::new($true)
    $bytes = [byte[]]($expected.GetPreamble() + $expected.GetBytes("namespace X;`r`n/// <tests>tests/A.Component.Tests/Foo.cs:BarAsync</tests>`r`n/// <tests>tests/A.Tests/FooBar.cs</tests>`r`n/// <tests>tests/A.Tests.Extra/Foo.cs</tests>`r`npublic class Foo { }`r`n"))
    Get-FixtureBytes $Fx.Library 'src/Core/Foo.cs' | Should -Be (ConvertTo-Hex $bytes)
    Get-FixtureBytes $Fx.Library 'README.md' | Should -Be (ConvertTo-Hex ([System.Text.Encoding]::UTF8.GetBytes("tests/A.Component.Tests/Foo.cs`r`n")))
    Get-FixtureBytes $Fx.Library 'samples/App/CLAUDE.md' | Should -Be (ConvertTo-Hex ([System.Text.Encoding]::UTF8.GetBytes('tests/A.Component.Tests/Foo.cs')))
  }

  It 'rewrites ai-docs, READMEs, plans, docs and CLAUDE.md files' {
    $Result.LibraryDocReferencesRewritten | Should -Be 8
    $Result.LibraryDocFiles | Should -Be 8
    Get-FixtureText $Fx.Library 'ai-docs/testing.md' | Should -Be "See ``tests/A.Component.Tests/Foo.cs`` and tests/A.Tests/FooBar.cs.`n"
    Get-FixtureText $Fx.Library 'ai-docs/data.json' | Should -Be '{"t":"tests/A.Component.Tests/Foo.cs"}'
    Get-FixtureText $Fx.Library 'docs/guide.md' | Should -Be "[foo](../tests/A.Component.Tests/Foo.cs)`n"
    Get-FixtureText $Fx.Library 'plans/plan.md' | Should -Be "- tests/A.Component.Tests/Foo.cs:BarAsync`n"
    Get-FixtureText $Fx.Library 'CLAUDE.md' | Should -Be "tests/A.Component.Tests/Foo.cs`n"
    Get-FixtureText $Fx.Library 'tests/A.Tests/README.md' | Should -Be "Run tests/A.Component.Tests/Foo.cs`n"
  }

  It 'leaves files out of scope, without a reference, binary, or not UTF-8 untouched' {
    foreach ($relative in 'docs/notes.txt', 'plans/data.yml', 'other/notes.md', 'scripts/Run.ps1', 'src/Core/bin/Debug/Foo.xml', 'src/Core/obj/Gen.cs', 'node_modules/pkg/README.md', '.claude/worktrees/w/README.md') {
      Get-FixtureText $Fx.Library $relative | Should -BeLike '*tests/A.Tests/Foo.cs*' -Because "$relative is out of scope"
    }
    Get-FixtureText $Fx.Library 'src/Core/Untouched.cs' | Should -Be "/// <tests>tests/A.Tests/FooBar.cs</tests>`n/// <tests>No tests found</tests>`n"
    Get-FixtureBytes $Fx.Library 'src/Core/Latin.txt' | Should -Be (ConvertTo-Hex ([byte[]](@(0xE9, 0x20) + [System.Text.Encoding]::ASCII.GetBytes('tests/A.Tests/Foo.cs'))))
    Get-FixtureBytes $Fx.Library 'src/Core/logo.bin' | Should -Be (ConvertTo-Hex ([byte[]](@(0, 1, 2) + [System.Text.Encoding]::ASCII.GetBytes('tests/A.Tests/Foo.cs'))))
    @(Get-ChangePath $Result 'library') | Should -Not -Contain 'src/Core/Untouched.cs'
    @($Result.Changes | Where-Object { -not $_.Written -and $_.Repository -eq 'library' }).Count | Should -Be 0
  }

  It 'lists a non-UTF-8 file holding a reference as skipped, in either repository, and not one without' {
    @($Result.Skipped | ForEach-Object { "$($_.Repository) $($_.Path)" }) | Should -Be @('library src/Core/Latin.txt', 'docs-site src/assets/legacy.txt')
  }

  It 'reports the docs site''s hand-written references without rewriting anything there' {
    $Result.DocsSiteRoot | Should -Be $Fx.Docs
    $Result.DocsSiteUpdated | Should -BeFalse
    $Result.DocsSiteReferencesFound | Should -Be 2
    $Result.DocsSiteFiles | Should -Be 2
    $Result.DocsSiteReferencesRewritten | Should -Be 0
    Get-ChangePath $Result 'docs-site' | Should -Be @('ai-docs/CODE-TEST-LINKING.md', 'src/assets/docs/v1.0.0/page.md', 'src/scripts/generate-code-maps.test.mjs')
    Get-TreeHash $Fx.Docs | Should -Be $DocsBefore
  }

  It 'reports docs-site script code citing a moved path for review, apart from the docs' {
    $Result.DocsSiteCodeReferences | Should -Be 1
    $Result.DocsSiteCodeFiles | Should -Be 1
    ($Result.Changes | Where-Object Path -EQ 'src/scripts/generate-code-maps.test.mjs').Category | Should -Be 'DocsSiteCode'
  }

  It 'counts the class-based markers naming a moved class, which a path move leaves valid' {
    $Result.DocsSiteClassMarkers | Should -Be 3
  }

  It 'lists generated artifacts to regenerate: path-citing files, the feed, and both projects'' status shards' {
    Get-RegeneratePath $Result | Should -Be @(
      'audit-reports/stale.txt',
      'src/assets/code-tests-map.json',
      'src/assets/data/test-status/A.Component.Tests.json',
      'src/assets/data/test-status/A.Tests.json',
      'src/assets/data/test-status/index.json',
      'src/assets/vscode-feed.json',
      'src/static/docs.html')
    ($Result.Regenerate | Where-Object Path -EQ 'src/assets/data/test-status/A.Component.Tests.json').Reason | Should -BeLike '*no status shard yet*'
    ($Result.Regenerate | Where-Object Path -EQ 'src/assets/data/test-status/A.Tests.json').Reason | Should -Be 'tests move from A.Tests to A.Component.Tests'
    ($Result.Regenerate | Where-Object Path -EQ 'src/assets/code-tests-map.json').Reason | Should -Be 'cites 1 moved test path(s)'
    ($Result.Regenerate | Where-Object Path -EQ 'src/assets/vscode-feed.json').Reason | Should -Be 'built from code-tests-map.json'
  }
}

Describe 'Invoke-TestReferenceMove: a project folder move' {
  BeforeAll {
    $script:Fx = New-Fixture
    $script:Result = Invoke-Fixture $Fx @(New-Move 'tests/A.Tests' 'tests/A.Unit.Tests')
  }

  It 'rewrites every path under the folder and leaves the sibling project sharing its prefix alone' {
    $Result.TagsRewritten | Should -Be 5
    $Result.TagFiles | Should -Be 3
    Get-FixtureText $Fx.Library 'src/Core/Foo.cs' | Should -BeLike '*<tests>tests/A.Unit.Tests/FooBar.cs</tests>*<tests>tests/A.Tests.Extra/Foo.cs</tests>*'
    Get-FixtureText $Fx.Library 'ai-docs/testing.md' | Should -Be "See ``tests/A.Unit.Tests/Foo.cs`` and tests/A.Unit.Tests/FooBar.cs.`n"
  }

  It 'counts markers for every class in the folder and lists both projects'' status shards' {
    $Result.DocsSiteClassMarkers | Should -Be 4
    Get-RegeneratePath $Result | Should -Contain 'src/assets/data/test-status/A.Unit.Tests.json'
    Get-RegeneratePath $Result | Should -Contain 'src/assets/data/test-status/A.Tests.json'
  }
}

Describe 'Invoke-TestReferenceMove: several moves' {
  It 'lists each status shard once when several moves share projects' {
    $fixture = New-Fixture
    $result = Invoke-Fixture $fixture @((New-Move $OldFoo $NewFoo), (New-Move 'tests/A.Tests/FooBar.cs' 'tests/A.Component.Tests/FooBar.cs'))
    $paths = Get-RegeneratePath $result
    @($paths | Where-Object { $_ -like 'src/assets/data/test-status/*' }) | Should -Be @(
      'src/assets/data/test-status/A.Component.Tests.json',
      'src/assets/data/test-status/A.Tests.json',
      'src/assets/data/test-status/index.json')
    $result.TagsRewritten | Should -Be 4
    $result.DocsSiteReferencesFound | Should -Be 3
  }

  It 'keeps the feed''s own reason when the feed itself cites a moved path' {
    $fixture = New-Fixture
    Set-FixtureFile $fixture.Docs 'src/assets/vscode-feed.json' '{"testFile":"tests/A.Tests/Foo.cs"}'
    $result = Invoke-Fixture $fixture @(New-Move $OldFoo $NewFoo)
    ($result.Regenerate | Where-Object Path -EQ 'src/assets/vscode-feed.json').Reason | Should -Be 'cites 1 moved test path(s)'
    Get-FixtureText $fixture.Docs 'src/assets/vscode-feed.json' | Should -Be '{"testFile":"tests/A.Tests/Foo.cs"}'
  }

  It 'lists no status shard for a move inside one project or outside any, and no feed when the map is untouched' {
    $fixture = New-Fixture
    New-Item -ItemType Directory -Path (Join-Path $fixture.Library 'tests/A.Tests/Moved') | Out-Null
    $result = Invoke-Fixture $fixture @((New-Move 'tests/A.Tests/FooBar.cs' 'tests/A.Tests/Moved/FooBar.cs'), (New-Move 'src/Core/Notes.cs' 'src/Core/Notes2.cs'))
    Get-RegeneratePath $result | Should -BeNullOrEmpty
    $result.TagsRewritten | Should -Be 2
  }
}

Describe 'Invoke-TestReferenceMove: updating the docs site' {
  It 'rewrites hand-written docs-site files, keeping their encoding, and never a generated one or script code' {
    $fixture = New-Fixture
    $generatedPaths = @('src/assets/code-tests-map.json', 'src/static/docs.html', 'audit-reports/stale.txt', 'src/assets/data/test-status/A.Tests.json')
    $generated = @($generatedPaths | ForEach-Object { Get-FixtureBytes $fixture.Docs $_ })
    $result = Invoke-Fixture $fixture @(New-Move $OldFoo $NewFoo) -UpdateDocsSite
    $result.DocsSiteReferencesRewritten | Should -Be 2
    Get-FixtureText $fixture.Docs 'src/assets/docs/v1.0.0/page.md' | Should -BeLike "*  - tests/A.Component.Tests/Foo.cs`n  - tests/A.Tests/FooBar.cs*"
    Get-FixtureBytes $fixture.Docs 'ai-docs/CODE-TEST-LINKING.md' |
      Should -Be (ConvertTo-Hex ([byte[]]([System.Text.UTF8Encoding]::new($true).GetPreamble() + [System.Text.Encoding]::UTF8.GetBytes("`"testFile`": `"tests/A.Component.Tests/Foo.cs`"`n"))))
    for ($i = 0; $i -lt $generatedPaths.Count; $i++) {
      Get-FixtureBytes $fixture.Docs $generatedPaths[$i] | Should -Be $generated[$i] -Because "$($generatedPaths[$i]) is generated"
    }
    Get-FixtureText $fixture.Docs 'src/scripts/generate-code-maps.test.mjs' | Should -Be "const missing = '<tests>tests/A.Tests/Foo.cs</tests>';`n" -Because 'script code is only reported'
    ($result.Changes | Where-Object Path -EQ 'src/scripts/generate-code-maps.test.mjs').Written | Should -BeFalse
    Get-FixtureText $fixture.Docs 'node_modules/x/README.md' | Should -Be 'tests/A.Tests/Foo.cs'
    Get-FixtureText $fixture.Docs 'dist/page.md' | Should -Be 'tests/A.Tests/Foo.cs'
  }
}

Describe 'Invoke-TestReferenceMove: a dry run' {
  It 'changes no file in either repository and reports what a real run would' {
    $fixture = New-Fixture
    $libraryBefore = Get-TreeHash $fixture.Library
    $docsBefore = Get-TreeHash $fixture.Docs
    $dry = Invoke-Fixture $fixture @(New-Move $OldFoo $NewFoo) -UpdateDocsSite -DryRun
    Get-TreeHash $fixture.Library | Should -Be $libraryBefore
    Get-TreeHash $fixture.Docs | Should -Be $docsBefore
    $dry.DryRun | Should -BeTrue
    @($dry.Changes | Where-Object Written).Count | Should -Be 0
    $real = Invoke-Fixture (New-Fixture) @(New-Move $OldFoo $NewFoo) -UpdateDocsSite
    $real.DryRun | Should -BeFalse
    foreach ($name in 'TagsRewritten', 'TagFiles', 'SourceReferencesRewritten', 'LibraryDocReferencesRewritten', 'DocsSiteReferencesFound', 'DocsSiteReferencesRewritten', 'DocsSiteCodeReferences', 'DocsSiteClassMarkers') {
      $dry.$name | Should -Be $real.$name -Because $name
    }
    @(Get-RegeneratePath $dry) | Should -Be @(Get-RegeneratePath $real)
  }

  It 'changes nothing under -WhatIf either' {
    $fixture = New-Fixture
    $libraryBefore = Get-TreeHash $fixture.Library
    $docsBefore = Get-TreeHash $fixture.Docs
    $resolved = @(Resolve-TestMove -RepositoryRoot $fixture.Library -From $OldFoo -To $NewFoo)
    $result = Invoke-TestReferenceMove -RepositoryRoot $fixture.Library -Moves $resolved -DocsSiteRoot $fixture.Docs -UpdateDocsSite -WhatIf -WarningAction SilentlyContinue
    Get-TreeHash $fixture.Library | Should -Be $libraryBefore
    Get-TreeHash $fixture.Docs | Should -Be $docsBefore
    $result.DryRun | Should -BeTrue
    $result.TagsRewritten | Should -Be 2
  }
}

Describe 'Invoke-TestReferenceMove: without a docs site' {
  It 'rewrites the library and reports nothing for the docs site' {
    $fixture = New-Fixture -NoDocsSite
    $resolved = @(Resolve-TestMove -RepositoryRoot $fixture.Library -From $OldFoo -To $NewFoo)
    $result = Invoke-TestReferenceMove -RepositoryRoot $fixture.Library -Moves $resolved -WarningAction SilentlyContinue
    $result.DocsSiteRoot | Should -BeNullOrEmpty
    $result.TagsRewritten | Should -Be 2
    $result.DocsSiteReferencesFound | Should -Be 0
    @($result.Regenerate).Count | Should -Be 0
  }
}

Describe 'the script' {
  BeforeAll {
    function Get-HostLine([object[]]$Output) {
      @($Output | Where-Object { $_ -is [System.Management.Automation.InformationRecord] } | ForEach-Object { $_.MessageData.ToString() })
    }
  }

  It 'relinks a move and returns the result object' {
    $fixture = New-Fixture
    $result = & $ScriptPath -RepositoryRoot $fixture.Library -From $OldFoo -To $NewFoo -WarningAction SilentlyContinue 6>$null
    $result.TagsRewritten | Should -Be 2
    $result.DocsSiteRoot | Should -Be $fixture.Docs
    Get-FixtureText $fixture.Library 'CLAUDE.md' | Should -Be "tests/A.Component.Tests/Foo.cs`n"
  }

  It 'prints the PR counts in a dry run, report-only for the docs site, and writes nothing' {
    $fixture = New-Fixture
    $before = Get-TreeHash $fixture.Base
    $output = & $ScriptPath -RepositoryRoot $fixture.Library -DocsSiteRoot $fixture.Docs -From $OldFoo -To $NewFoo -DryRun -WarningAction SilentlyContinue 6>&1
    Get-TreeHash $fixture.Base | Should -Be $before
    $lines = Get-HostLine $output
    $lines | Should -Contain 'Test reference relink (dry run: nothing written)'
    $lines | Should -Contain '    [File] tests/A.Tests/Foo.cs -> tests/A.Component.Tests/Foo.cs'
    $lines | Should -Contain '  <tests> tags would be rewritten: 2 in 2 file(s)'
    $lines | Should -Contain '  Other source references would be rewritten: 1 in 1 file(s)'
    $lines | Should -Contain '  Library doc references would be rewritten: 8 in 8 file(s)'
    $lines | Should -Contain '  Skipped (not valid UTF-8, fix by hand): 2'
    $lines | Should -Contain '    library: src/Core/Latin.txt'
    $lines | Should -Contain "  Docs site ($($fixture.Docs)): 2 reference(s) found in 2 file(s); 0 rewritten (report only: pass -UpdateDocsSite to rewrite them)"
    $lines | Should -Contain '  Docs site script code citing a moved path (review by hand, never rewritten): 1 in 1 file(s)'
    $lines | Should -Contain '  Docs site class-based markers naming a moved class: 3 (tests=[...] and {verified: ...} name Class.Method, not a path, so a move leaves them valid)'
    $lines | Should -Contain '  Docs site generated artifacts to regenerate (never hand-edited): 7'
    $lines | Should -Contain '    src/assets/data/test-status/A.Tests.json: tests move from A.Tests to A.Component.Tests [src/scripts/build-test-status.mjs (docs-site CI, from the library test results)]'
    $lines | Should -Contain '    library src/Core/Foo.cs: 1 tag(s), 0 other reference(s)'
    @($output | Where-Object { $_ -isnot [System.Management.Automation.InformationRecord] }).Count | Should -Be 1
  }

  It 'says what it wrote when it updates the docs site, and reads moves from a map file' {
    $fixture = New-Fixture
    $map = Join-Path $fixture.Base 'moves.csv'
    Set-Content -LiteralPath $map -Value @('From,To', "$OldFoo,$NewFoo")
    $output = & $ScriptPath -RepositoryRoot $fixture.Library -MapFile $map -UpdateDocsSite -WarningAction SilentlyContinue 6>&1
    $lines = Get-HostLine $output
    $lines | Should -Contain 'Test reference relink'
    $lines | Should -Contain "  Docs site ($($fixture.Docs)): 2 reference(s) found in 2 file(s); 2 rewritten"
    $lines | Should -Contain '    docs-site src/assets/docs/v1.0.0/page.md: 0 tag(s), 1 other reference(s), written'
    $lines | Should -Contain '    docs-site src/scripts/generate-code-maps.test.mjs: 0 tag(s), 1 other reference(s)'
    Get-FixtureText $fixture.Docs 'src/assets/docs/v1.0.0/page.md' | Should -BeLike '*tests/A.Component.Tests/Foo.cs*'
  }

  It 'warns and says the docs site was not scanned when the default checkout is missing' {
    $fixture = New-Fixture -NoDocsSite
    $output = & $ScriptPath -RepositoryRoot $fixture.Library -From $OldFoo -To $NewFoo 3>&1 6>&1
    Get-HostLine $output | Should -Contain '  Docs site: not scanned'
    $warnings = @($output | Where-Object { $_ -is [System.Management.Automation.WarningRecord] } | ForEach-Object Message)
    @($warnings | Where-Object { $_ -like 'Docs site not found*' }).Count | Should -Be 1
    @($warnings | Where-Object { $_ -like 'library src/Core/Latin.txt: not valid UTF-8*' }).Count | Should -Be 1
  }

  It 'changes nothing under -WhatIf' {
    $fixture = New-Fixture
    $before = Get-TreeHash $fixture.Base
    $result = & $ScriptPath -RepositoryRoot $fixture.Library -From $OldFoo -To $NewFoo -UpdateDocsSite -WhatIf -WarningAction SilentlyContinue 6>$null
    Get-TreeHash $fixture.Base | Should -Be $before
    $result.DryRun | Should -BeTrue
  }

  It 'defaults to the repository above the script and stops on an unknown path before writing' {
    $repoRoot = Join-Path -Path $PSScriptRoot -ChildPath '../../..' -Resolve
    { & $ScriptPath -From 'tests/No.Such.Tests/Nope.cs' -To 'tests/Other.Tests/Nope.cs' 6>$null } |
      Should -Throw "Neither 'tests/No.Such.Tests/Nope.cs' nor 'tests/Other.Tests/Nope.cs' exists under $repoRoot*"
  }
}
