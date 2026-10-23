# Copyright (c) whizbang-lib contributors.
# SPDX-License-Identifier: MIT

#Requires -Modules Pester

BeforeAll {
  Import-Module (Join-Path $PSScriptRoot '..' '..' '..' 'scripts' 'lib' 'ReviewFindings.psm1') -Force

  $script:Sha = 'abc1234def5678abc1234def5678abc1234def56'

  # A unified diff with one modified file, one new file and one modified migration.
  $script:Diff = @'
diff --git a/src/Whizbang.Core/Thing.cs b/src/Whizbang.Core/Thing.cs
index 1111111..2222222 100644
--- a/src/Whizbang.Core/Thing.cs
+++ b/src/Whizbang.Core/Thing.cs
@@ -10,3 +10,4 @@ public class Thing {
   int a;
-  int b;
+  int c; // the behaviour is recognised here
+  var id = Guid.NewGuid();
   int d;
diff --git a/tests/Whizbang.Core.Tests/ThingTests.cs b/tests/Whizbang.Core.Tests/ThingTests.cs
new file mode 100644
index 0000000..3333333
--- /dev/null
+++ b/tests/Whizbang.Core.Tests/ThingTests.cs
@@ -0,0 +1,3 @@
+await Task.Delay(100);
+var now = DateTime.UtcNow;
+var id = Guid.NewGuid();
diff --git a/src/Whizbang.Data.Postgres/Migrations/150_Old.sql b/src/Whizbang.Data.Postgres/Migrations/150_Old.sql
index 4444444..5555555 100644
--- a/src/Whizbang.Data.Postgres/Migrations/150_Old.sql
+++ b/src/Whizbang.Data.Postgres/Migrations/150_Old.sql
@@ -1,1 +1,1 @@
-SELECT 1;
+SELECT 2; -- a note — with a dash
'@
}

Describe 'the findings block' {
  It 'round-trips findings, source and commit' {
    $f = New-ReviewFinding -Id 'x:1' -Rule 'r' -Severity blocking -Body 'text' -Path 'src/a.cs' -Line 3
    $text = ConvertTo-ReviewBlock -Source 'review-checks' -Sha $Sha -Findings @($f)
    $blocks = @(ConvertFrom-ReviewBlock -Text "intro`n$text`noutro")
    $blocks.Count | Should -Be 1
    $blocks[0].Source | Should -Be 'review-checks'
    $blocks[0].Sha | Should -Be $Sha
    $blocks[0].Findings[0].Id | Should -Be 'x:1'
    $blocks[0].Findings[0].Path | Should -Be 'src/a.cs'
    $blocks[0].Findings[0].Line | Should -Be 3
  }

  It 'keeps the HTML comment closed when a body contains -->' {
    $f = New-ReviewFinding -Id 'x:1' -Rule 'r' -Severity warning -Body 'a --> b --!> c'
    $text = ConvertTo-ReviewBlock -Source 's' -Sha $Sha -Findings @($f)
    ([regex]::Matches($text, '-->')).Count | Should -Be 1
    $text | Should -Not -Match '--!>'
    (ConvertFrom-ReviewBlock -Text $text)[0].Findings[0].Body | Should -Be 'a --> b --!> c'
  }

  It 'reads every block in a text' {
    $a = ConvertTo-ReviewBlock -Source 'a' -Sha $Sha -Findings @()
    $b = ConvertTo-ReviewBlock -Source 'b' -Sha $Sha -Findings @()
    @(ConvertFrom-ReviewBlock -Text "$a`n$b").Source | Should -Be @('a', 'b')
  }

  It 'returns nothing for a text with no block' {
    @(ConvertFrom-ReviewBlock -Text 'just a comment').Count | Should -Be 0
  }

  It 'drops a block that is not valid JSON' {
    @(ConvertFrom-ReviewBlock -Text "<!-- whizbang-review:v1`n{not json`n-->" -WarningAction SilentlyContinue).Count | Should -Be 0
  }

  It 'drops a block whose commit is not a hex sha' {
    $text = "<!-- whizbang-review:v1`n{""source"":""s"",""sha"":""main"",""findings"":[]}`n-->"
    @(ConvertFrom-ReviewBlock -Text $text -WarningAction SilentlyContinue).Count | Should -Be 0
  }

  It 'drops findings that are malformed and keeps the rest' -TestCases @(
    @{ Name = 'no id'; Json = '{"rule":"r","severity":"warning","body":"b"}' }
    @{ Name = 'unknown severity'; Json = '{"id":"i","rule":"r","severity":"fatal","body":"b"}' }
    @{ Name = 'absolute path'; Json = '{"id":"i","rule":"r","severity":"warning","body":"b","path":"/etc/passwd","line":1}' }
    @{ Name = 'path escapes the repo'; Json = '{"id":"i","rule":"r","severity":"warning","body":"b","path":"src/../../x","line":1}' }
    @{ Name = 'line not positive'; Json = '{"id":"i","rule":"r","severity":"warning","body":"b","path":"a.cs","line":0}' }
    @{ Name = 'line without path'; Json = '{"id":"i","rule":"r","severity":"warning","body":"b","line":4}' }
  ) {
    $good = '{"id":"ok","rule":"r","severity":"warning","body":"b"}'
    $text = "<!-- whizbang-review:v1`n{""source"":""s"",""sha"":""$Sha"",""findings"":[$Json,$good]}`n-->"
    $findings = (ConvertFrom-ReviewBlock -Text $text -WarningAction SilentlyContinue)[0].Findings
    @($findings).Id | Should -Be @('ok')
  }

  It 'caps the block and records how many findings it left out' {
    $many = 1..400 | ForEach-Object { New-ReviewFinding -Id "x:$_" -Rule 'r' -Severity warning -Body ('b' * 300) }
    $text = ConvertTo-ReviewBlock -Source 's' -Sha $Sha -Findings $many -MaxLength 20000
    $text.Length | Should -BeLessOrEqual 20000
    $block = (ConvertFrom-ReviewBlock -Text $text)[0]
    $block.Omitted | Should -BeGreaterThan 0
    ($block.Findings.Count + $block.Omitted) | Should -Be 400
  }
}

Describe 'Get-GateFindings' {
  It 'reads the coverage and Sonar files the quality gate writes' {
    $dir = Join-Path $TestDrive 'gate'
    New-Item -ItemType Directory -Path $dir | Out-Null
    Set-Content (Join-Path $dir 'lines.txt') 'src/Gen/A.cs:215: return null;'
    Set-Content (Join-Path $dir 'branches.txt') 'src/Gen/A.cs:213: (2/4 conditions) if (x)'
    Set-Content (Join-Path $dir 'sonar.txt') @('src/Gen/A.cs:187: external_roslyn:RCS9004 Call ''Any'' instead of accessing ''Count''', 'src/Gen/B.cs:: S1135 Complete the task')

    $f = @(Get-GateFindings -UncoveredLinesFile (Join-Path $dir 'lines.txt') -UncoveredBranchesFile (Join-Path $dir 'branches.txt') -SonarFile (Join-Path $dir 'sonar.txt'))

    $f.Count | Should -Be 4
    ($f | Where-Object Rule -EQ 'coverage.new-line').Line | Should -Be 215
    ($f | Where-Object Rule -EQ 'coverage.new-line').Body | Should -BeLike '*No test runs this line*return null;*'
    ($f | Where-Object Rule -EQ 'coverage.new-branch').Body | Should -BeLike '*2/4 conditions*'
    $sonar = @($f | Where-Object Rule -Like 'sonar*')
    $sonar[0].Id | Should -Be 'sonar:external_roslyn:RCS9004:src/Gen/A.cs:187'
    $sonar[0].Line | Should -Be 187
    $sonar[1].Path | Should -Be 'src/Gen/B.cs'
    $sonar[1].Line | Should -BeNullOrEmpty
    $f | ForEach-Object { $_.Severity | Should -Be 'blocking' }
  }

  It 'treats a missing file as no findings' {
    @(Get-GateFindings -UncoveredLinesFile (Join-Path $TestDrive 'none.txt')).Count | Should -Be 0
  }
}

Describe 'reading a diff' {
  It 'lists the added lines with their new line numbers' {
    $added = @(Get-AddedLines -DiffText $Diff)
    ($added | Where-Object Path -EQ 'src/Whizbang.Core/Thing.cs').Line | Should -Be @(11, 12)
    ($added | Where-Object Path -EQ 'tests/Whizbang.Core.Tests/ThingTests.cs').Line | Should -Be @(1, 2, 3)
  }

  It 'lists every line a review comment can be placed on' {
    $lines = Get-CommentableLines -DiffText $Diff
    $lines.Contains('src/Whizbang.Core/Thing.cs:10') | Should -BeTrue  # context
    $lines.Contains('src/Whizbang.Core/Thing.cs:12') | Should -BeTrue  # added
    $lines.Contains('src/Whizbang.Core/Thing.cs:13') | Should -BeTrue  # context
    $lines.Contains('src/Whizbang.Core/Thing.cs:14') | Should -BeFalse # outside the hunk
  }

  It 'tells a modified file from a new one' {
    $files = Get-DiffFiles -DiffText $Diff
    ($files | Where-Object Path -EQ 'src/Whizbang.Data.Postgres/Migrations/150_Old.sql').IsNew | Should -BeFalse
    ($files | Where-Object Path -EQ 'tests/Whizbang.Core.Tests/ThingTests.cs').IsNew | Should -BeTrue
  }
}

Describe 'Find-DiffFindings' {
  BeforeAll { $script:Found = @(Find-DiffFindings -DiffText $Diff) }

  It 'flags en-GB spelling on the line' {
    $f = @($Found | Where-Object Rule -EQ 'style.en-us')
    $f.Count | Should -Be 1
    $f[0].Line | Should -Be 11
    $f[0].Body | Should -BeLike '*behaviour*behavior*'
    $f[0].Body | Should -BeLike '*recognised*recognized*'
  }

  It 'flags an em-dash' {
    $f = @($Found | Where-Object Rule -EQ 'style.em-dash')
    $f.Count | Should -Be 1
    $f[0].Path | Should -Be 'src/Whizbang.Data.Postgres/Migrations/150_Old.sql'
  }

  It 'flags real time in test code only' {
    $f = @($Found | Where-Object Rule -EQ 'tests.real-time')
    $f.Line | Should -Be @(1, 2)
    $f | ForEach-Object { $_.Path | Should -Be 'tests/Whizbang.Core.Tests/ThingTests.cs' }
  }

  It 'flags Guid.NewGuid in library code only' {
    $f = @($Found | Where-Object Rule -EQ 'code.tracked-guid')
    $f.Count | Should -Be 1
    $f[0].Path | Should -Be 'src/Whizbang.Core/Thing.cs'
  }

  It 'flags an edited migration once, on the file' {
    $f = @($Found | Where-Object Rule -EQ 'migrations.edited')
    $f.Count | Should -Be 1
    $f[0].Severity | Should -Be 'blocking'
    $f[0].Line | Should -Be 1
  }

  It 'does not flag a new migration' {
    $new = @'
diff --git a/src/Whizbang.Data.Postgres/Migrations/207_New.sql b/src/Whizbang.Data.Postgres/Migrations/207_New.sql
new file mode 100644
--- /dev/null
+++ b/src/Whizbang.Data.Postgres/Migrations/207_New.sql
@@ -0,0 +1,1 @@
+SELECT 1;
'@
    @(Find-DiffFindings -DiffText $new | Where-Object Rule -EQ 'migrations.edited').Count | Should -Be 0
  }

  It 'skips generated code' {
    $gen = @'
diff --git a/src/Whizbang.Core/Thing.g.cs b/src/Whizbang.Core/Thing.g.cs
--- a/src/Whizbang.Core/Thing.g.cs
+++ b/src/Whizbang.Core/Thing.g.cs
@@ -1,0 +1,1 @@
+var id = Guid.NewGuid(); // behaviour
'@
    @(Find-DiffFindings -DiffText $gen).Count | Should -Be 0
  }

  It 'skips a line marked review-checks:allow' {
    $d = @'
diff --git a/src/A.cs b/src/A.cs
--- a/src/A.cs
+++ b/src/A.cs
@@ -1,0 +1,2 @@
+// behaviour, quoted on purpose. review-checks:allow
+// behaviour
'@
    @(Find-DiffFindings -DiffText $d).Line | Should -Be @(2)
  }

  It 'skips a path that matches an ignore glob' {
    @(Find-DiffFindings -DiffText $Diff -IgnorePaths @('tests/*', 'src/Whizbang.Data.Postgres/*')).Path | Select-Object -Unique |
      Should -Be @('src/Whizbang.Core/Thing.cs')
  }

  It 'leaves a quoted cancelled status and cancelled() alone' {
    $d = @'
diff --git a/x.yml b/x.yml
--- a/x.yml
+++ b/x.yml
@@ -1,0 +1,3 @@
+if: conclusion == 'cancelled'
+if: cancelled()
+the run was cancelled
'@
    @(Find-DiffFindings -DiffText $d).Line | Should -Be @(3)
  }

  It 'gives every finding a stable id' {
    $again = @(Find-DiffFindings -DiffText $Diff)
    $again.Id | Should -Be $Found.Id
    ($Found.Id | Select-Object -Unique).Count | Should -Be $Found.Count
  }

  It 'does not flag a migration edit whose normalized SQL is unchanged' {
    # REUSE-IgnoreStart: a header written into a fixture, not this file's license
    $read = { param($OldPath, $NewPath) [pscustomobject]@{ Old = "SELECT 1;`n"; New = "-- SPDX-License-Identifier: MIT`r`nSELECT 1;   -- note`r`n" } }
    # REUSE-IgnoreEnd
    @(Find-DiffFindings -DiffText $Diff -ReadMigrationVersions $read | Where-Object Rule -EQ 'migrations.edited').Count | Should -Be 0
  }

  It 'flags a migration edit that changes executable SQL' {
    $read = { param($OldPath, $NewPath) [pscustomobject]@{ Old = 'SELECT 1;'; New = 'SELECT 2; -- a note' } }
    @(Find-DiffFindings -DiffText $Diff -ReadMigrationVersions $read | Where-Object Rule -EQ 'migrations.edited').Count | Should -Be 1
  }

  It 'flags a migration edit whose versions cannot be read' {
    $read = { param($OldPath, $NewPath) $null }
    @(Find-DiffFindings -DiffText $Diff -ReadMigrationVersions $read | Where-Object Rule -EQ 'migrations.edited').Count | Should -Be 1
  }

  It 'reads a renamed migration from its old path' {
    $renamed = @'
diff --git a/src/Whizbang.Data.Postgres/Migrations/150_Old.sql b/src/Whizbang.Data.Postgres/Migrations/150_New.sql
similarity index 90%
rename from src/Whizbang.Data.Postgres/Migrations/150_Old.sql
rename to src/Whizbang.Data.Postgres/Migrations/150_New.sql
--- a/src/Whizbang.Data.Postgres/Migrations/150_Old.sql
+++ b/src/Whizbang.Data.Postgres/Migrations/150_New.sql
@@ -1,1 +1,2 @@
+-- a note
 SELECT 1;
'@
    $script:seen = @()
    $read = { param($OldPath, $NewPath) $script:seen = @($OldPath, $NewPath); [pscustomobject]@{ Old = 'SELECT 1;'; New = "-- a note`nSELECT 1;" } }
    @(Find-DiffFindings -DiffText $renamed -ReadMigrationVersions $read | Where-Object Rule -EQ 'migrations.edited').Count | Should -Be 0
    $script:seen | Should -Be @('src/Whizbang.Data.Postgres/Migrations/150_Old.sql', 'src/Whizbang.Data.Postgres/Migrations/150_New.sql')
  }
}

Describe 'ConvertTo-NormalizedSql' {
  # The same cases as tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/MigrationSqlNormalizerTests.cs, which
  # holds the C# normalizer every driver hashes with; the two must agree.
  BeforeAll { $script:Base = "CREATE TABLE wh_a (id uuid);`nSELECT 1;" }

  It 'removes line comments in every position' {
    ConvertTo-NormalizedSql "-- header`nCREATE TABLE wh_a (id uuid); -- trailing`n  -- indented`nSELECT 1;`n-- last" | Should -BeExactly $Base
  }

  It 'removes block comments in every position' {
    ConvertTo-NormalizedSql "/* license`n   header */`nCREATE TABLE /* inline */ wh_a (id uuid);`n/* between`n*/`nSELECT 1; /* after */" | Should -BeExactly $Base
  }

  It 'reads nested block comments as PostgreSQL does' {
    ConvertTo-NormalizedSql "/* outer /* inner */ still a comment */`nCREATE TABLE wh_a (id uuid);`nSELECT 1;" | Should -BeExactly $Base
  }

  It 'runs an unterminated block comment to the end' {
    ConvertTo-NormalizedSql "SELECT 1; /* never closed`nSELECT 2;" | Should -BeExactly 'SELECT 1;'
  }

  It 'reads a block comment between tokens as one space' {
    ConvertTo-NormalizedSql 'SELECT/*x*/1;' | Should -BeExactly 'SELECT 1;'
    ConvertTo-NormalizedSql 'SELECT  /* x */  1;' | Should -BeExactly 'SELECT 1;'
  }

  It 'removes whitespace at line edges and blank lines' {
    ConvertTo-NormalizedSql "`n`n   `t CREATE TABLE wh_a (id uuid);   `n`n`t`n  SELECT 1;`t `n`n" | Should -BeExactly $Base
  }

  It 'collapses runs of whitespace between tokens' {
    ConvertTo-NormalizedSql "CREATE  TABLE`twh_a  (id `t uuid);`nSELECT    1;" | Should -BeExactly $Base
    ConvertTo-NormalizedSql "CREATE`fTABLE`vwh_a (id uuid);`nSELECT 1;" | Should -BeExactly $Base
  }

  It 'reads CRLF and a lone CR as LF' {
    ConvertTo-NormalizedSql "CREATE TABLE wh_a (id uuid);`r`nSELECT 1;" | Should -BeExactly $Base
    ConvertTo-NormalizedSql "CREATE TABLE wh_a (id uuid);`rSELECT 1;" | Should -BeExactly $Base
  }

  It 'keeps <Sql> verbatim' -TestCases @(
    @{ Sql = "SELECT 'a -- b  /* c */ ';" }
    @{ Sql = "SELECT 'it''s -- not a comment';" }
    @{ Sql = "SELECT E'a\' -- still inside';" }
    @{ Sql = "SELECT e'tab\t  /* inside */';" }
    @{ Sql = "SELECT E'doubled '' -- inside';" }
    @{ Sql = 'SELECT "col -- name" FROM "t /* x */";' }
    @{ Sql = 'SELECT "a""b -- c";' }
    @{ Sql = 'SELECT $$ -- body /* x */  $$;' }
    @{ Sql = 'SELECT $fn$ body $$ -- still inside $fn$;' }
    @{ Sql = 'SELECT $a_1$ x $b$ y $a_1$;' }
    @{ Sql = 'SELECT $_t$ -- x $_t$;' }
    @{ Sql = "SELECT 'never closed -- x  " }
    @{ Sql = "SELECT E'never closed \' -- x  " }
    @{ Sql = "SELECT E'ends on a backslash \" }
    @{ Sql = 'SELECT "never closed -- x  ' }
    @{ Sql = 'SELECT $q$ never closed -- x  ' }
    @{ Sql = '$$ -- x $$' }
    @{ Sql = "E'a\' -- inside'" }
  ) {
    ConvertTo-NormalizedSql $Sql | Should -BeExactly $Sql
  }

  It 'keeps a literal that spans lines as it is' {
    $body = "CREATE FUNCTION f() RETURNS int AS `$`$`n  BEGIN`n`n    -- inside the body`n    RETURN 1;   `n  END;`n`$`$ LANGUAGE plpgsql;"
    ConvertTo-NormalizedSql $body | Should -BeExactly $body
  }

  It 'resumes after a literal' {
    ConvertTo-NormalizedSql "SELECT 'x'   -- note`n`n  , `$`$y`$`$  /* z */ ;" | Should -BeExactly "SELECT 'x'`n, `$`$y`$`$ ;"
  }

  It 'reads <Sql> as ordinary text' -TestCases @(
    @{ Sql = 'SELECT $1; -- note'; Expected = 'SELECT $1;' }
    @{ Sql = 'SELECT a$b$ FROM t; -- note'; Expected = 'SELECT a$b$ FROM t;' }
    @{ Sql = 'SELECT $1x$ -- note'; Expected = 'SELECT $1x$' }
    @{ Sql = 'SELECT $a b$ -- note'; Expected = 'SELECT $a b$' }
    @{ Sql = 'SELECT 1 $'; Expected = 'SELECT 1 $' }
    @{ Sql = 'SELECT 1 $ab'; Expected = 'SELECT 1 $ab' }
    @{ Sql = 'SELECT $1$$ -- note'; Expected = 'SELECT $1$$' }
    @{ Sql = 'SELECT a_$$ -- note'; Expected = 'SELECT a_$$' }
    @{ Sql = "SELECT xE'a\' -- gone"; Expected = "SELECT xE'a\'" }
    @{ Sql = "'a\' -- gone"; Expected = "'a\'" }
    @{ Sql = 'SELECT 4 - 2 / 1;'; Expected = 'SELECT 4 - 2 / 1;' }
    @{ Sql = 'SELECT 1 -'; Expected = 'SELECT 1 -' }
    @{ Sql = 'SELECT 1 /'; Expected = 'SELECT 1 /' }
  ) {
    ConvertTo-NormalizedSql $Sql | Should -BeExactly $Expected
  }

  It 'normalizes only comments and whitespace to nothing' {
    ConvertTo-NormalizedSql " -- a`n/* b */`n`r`n`t" | Should -BeExactly ''
  }

  It 'tells a change inside a literal from a comment edit' {
    ConvertTo-NormalizedSql "SELECT 'a -- b';" | Should -Not -BeExactly (ConvertTo-NormalizedSql "SELECT 'a -- c';")
  }

  It 'normalizes every shipped migration to what the C# normalizer would hash' {
    # Spot-checks the port against the real files: none normalizes to nothing, and a comment-only edit of each is
    # invisible.
    $dir = Join-Path $PSScriptRoot '..' '..' '..' 'src' 'Whizbang.Data.Postgres' 'Migrations'
    foreach ($file in Get-ChildItem -LiteralPath $dir -Filter '*.sql') {
      $sql = [System.IO.File]::ReadAllText($file.FullName)
      $normalized = ConvertTo-NormalizedSql $sql
      $normalized | Should -Not -BeNullOrEmpty -Because $file.Name
      ConvertTo-NormalizedSql ("-- header`r`n" + $sql + "`n-- trailer`n") | Should -BeExactly $normalized -Because $file.Name
    }
  }
}

Describe 'Find-PullRequestFindings' {
  It 'blocks a pull request that closes no issue, since the gate requires one' {
    $f = @(Find-PullRequestFindings -Body 'a fix' -ClosingIssueCount 0 -BaseRef develop -HeadRef fix/x)
    $f.Rule | Should -Be @('pr.closing-keyword')
    $f[0].Severity | Should -Be 'blocking'
    $f[0].Body | Should -Match 'requires'
    $f[0].Path | Should -BeNullOrEmpty
  }

  It 'does not ask a bot pull request for an issue' {
    @(Find-PullRequestFindings -Body '' -ClosingIssueCount 0 -BaseRef develop -HeadRef dependabot/x -AuthorIsBot).Count | Should -Be 0
  }

  It 'does not ask the release pull request into main for an issue' {
    @(Find-PullRequestFindings -Body '' -ClosingIssueCount 0 -BaseRef main -HeadRef 'release/v1.0.0').Count | Should -Be 0
  }

  It 'still asks a hotfix into main for an issue' {
    (Find-PullRequestFindings -Body '' -ClosingIssueCount 0 -BaseRef main -HeadRef 'hotfix/v1.0.1').Rule | Should -Be @('pr.closing-keyword')
  }

  It 'explains a backticked keyword' {
    $f = @(Find-PullRequestFindings -Body 'This `Fixes #12`' -ClosingIssueCount 0 -BaseRef develop -HeadRef fix/x)
    $f[0].Body | Should -BeLike '*backticks*'
    $f[0].Severity | Should -Be 'blocking'
  }

  It 'is quiet when an issue is closed and the base is develop' {
    @(Find-PullRequestFindings -Body 'Fixes #12' -ClosingIssueCount 1 -BaseRef develop -HeadRef fix/x).Count | Should -Be 0
  }

  It 'flags a base other than develop' {
    (Find-PullRequestFindings -Body '' -ClosingIssueCount 1 -BaseRef main -HeadRef fix/x).Rule | Should -Be @('pr.base-branch')
  }

  It 'allows a release or hotfix branch into main' -TestCases @(@{ Head = 'release/v1.0.0' }, @{ Head = 'hotfix/v1.0.1' }) {
    @(Find-PullRequestFindings -Body '' -ClosingIssueCount 1 -BaseRef main -HeadRef $Head).Count | Should -Be 0
  }
}

Describe 'Find-PullRequestTypeFindings' {
  BeforeAll {
    # A docs-site pull request that links back, unless a test says otherwise.
    $script:GoodDocs = @([pscustomobject]@{ Number = 1206; State = 'OPEN'; ReferencesThis = $true })
    function Find-Type([string]$Title, [string[]]$Paths, [string[]]$Labels = @('bug'), [string]$Body = '', [switch]$Bot, [string]$Base = 'develop',
      [string]$Head = 'fix/x', [object[]]$Docs = $script:GoodDocs, [string[]]$PrLabels = @()) {
      @(Find-PullRequestTypeFindings -Title $Title -Body $Body -ChangedPaths $Paths -ClosingIssueLabels $Labels `
          -BaseRef $Base -HeadRef $Head -AuthorIsBot:$Bot -DocsPullRequests $Docs -Labels $PrLabels)
    }
  }

  It 'asks a fix to close an issue labeled bug' {
    $f = Find-Type -Title 'fix: x' -Paths @('src/A.cs', 'tests/ATests.cs', 'CHANGELOG.md') -Labels @('enhancement')
    $f.Rule | Should -Be @('pr.fix-bug-issue')
    $f[0].Severity | Should -Be 'blocking'
  }

  It 'asks a library fix for a test' {
    (Find-Type -Title 'fix: x' -Paths @('src/A.cs', 'CHANGELOG.md')).Rule | Should -Be @('pr.fix-test')
  }

  It 'asks a library feat, fix or perf for its docs-site pull request, saying how to link it' -TestCases @(
    @{ T = 'feat: x'; L = @('feature') }, @{ T = 'fix: x'; L = @('bug') }, @{ T = 'perf: x'; L = @() }
  ) {
    $f = @(Find-Type -Title $T -Paths @('src/A.cs', 'tests/ATests.cs', 'CHANGELOG.md') -Labels $L -Docs @())
    $f.Rule | Should -Be @('pr.docs-link')
    $f[0].Severity | Should -Be 'blocking'
    $f[0].Body | Should -Match 'Docs for whizbang-lib/whizbang#'
    $f[0].Body | Should -Match '/recheck'
    $f[0].Body | Should -Match '/docs-not-needed'
  }

  It 'does not count a docs-site pull request closed without merging' {
    $closed = @([pscustomobject]@{ Number = 9; State = 'CLOSED'; ReferencesThis = $true })
    (Find-Type -Title 'feat: x' -Paths @('src/A.cs', 'CHANGELOG.md') -Labels @('feature') -Docs $closed).Rule | Should -Be @('pr.docs-link')
  }

  It 'counts a merged docs-site pull request' {
    $merged = @([pscustomobject]@{ Number = 9; State = 'MERGED'; ReferencesThis = $true })
    @(Find-Type -Title 'feat: x' -Paths @('src/A.cs', 'CHANGELOG.md') -Labels @('feature') -Docs $merged).Count | Should -Be 0
  }

  It 'asks a docs-site pull request linked only from here to link back' {
    $oneWay = @([pscustomobject]@{ Number = 9; State = 'OPEN'; ReferencesThis = $false })
    $f = @(Find-Type -Title 'feat: x' -Paths @('src/A.cs', 'CHANGELOG.md') -Labels @('feature') -Docs $oneWay)
    $f.Rule | Should -Be @('pr.docs-backlink')
    $f[0].Body | Should -Match '#9'
  }

  It 'asks nothing about docs when a maintainer marked them not needed' {
    @(Find-Type -Title 'fix: x' -Paths @('src/A.cs', 'tests/ATests.cs', 'CHANGELOG.md') -Docs @() -PrLabels @('docs:not-needed')).Count | Should -Be 0
  }

  It 'asks no docs of a refactor, a test or a change outside the library' {
    @(Find-Type -Title 'refactor: x' -Paths @('src/A.cs', 'CHANGELOG.md') -Docs @()).Rule | Should -Not -Contain 'pr.docs-link'
    @(Find-Type -Title 'fix(ci): x' -Paths @('.github/workflows/ci.yml') -Docs @()).Rule | Should -Not -Contain 'pr.docs-link'
  }

  It 'asks a library feat or fix for a CHANGELOG.md entry' -TestCases @(@{ T = 'fix: x' }, @{ T = 'feat(core): x' }) {
    $body = 'whizbang-lib.github.io/pull/1'
    (Find-Type -Title $T -Paths @('src/A.cs', 'tests/ATests.cs') -Body $body).Rule | Should -Contain 'pr.changelog'
  }

  It 'is quiet for a complete fix' {
    @(Find-Type -Title 'fix(core): x' -Paths @('src/A.cs', 'tests/ATests.cs', 'CHANGELOG.md')).Count | Should -Be 0
  }

  It 'asks nothing of a change outside the library' {
    @(Find-Type -Title 'fix(ci): x' -Paths @('.github/workflows/ci.yml') -Labels @()).Rule | Should -Be @('pr.fix-bug-issue')
    @(Find-Type -Title 'feat(ci): x' -Paths @('.github/workflows/ci.yml') -Labels @()).Count | Should -Be 0
    @(Find-Type -Title 'docs: x' -Paths @('README.md') -Labels @()).Count | Should -Be 0
  }

  It 'asks nothing of a title that is not a Conventional Commit, which the gate reports' {
    @(Find-Type -Title 'Fix it' -Paths @('src/A.cs') -Labels @()).Count | Should -Be 0
  }

  It 'exempts bots and the release pull request into main' {
    @(Find-Type -Title 'fix: x' -Paths @('src/A.cs') -Labels @() -Bot).Count | Should -Be 0
    @(Find-Type -Title 'fix: x' -Paths @('src/A.cs') -Labels @() -Base main -Head 'release/v1.0.0').Count | Should -Be 0
  }
}

Describe 'ConvertTo-DcoFindings' {
  It 'turns each DCO problem into a finding for its commit' {
    $verdict = [pscustomobject]@{ Pass = $false; Problems = @('aaaaaaaaaaaa feat: x: no Signed-off-by line', 'bbbbbbbbbbbb fix: y: Signed-off-by a@b does not match the author, c@d') }
    $f = @(ConvertTo-DcoFindings -Verdict $verdict)
    $f.Id | Should -Be @('dco:aaaaaaaaaaaa', 'dco:bbbbbbbbbbbb')
    $f[0].Body | Should -BeLike '*no Signed-off-by*git rebase --signoff*'
    $f[0].Severity | Should -Be 'blocking'
  }

  It 'returns nothing for a passing verdict' {
    @(ConvertTo-DcoFindings -Verdict ([pscustomobject]@{ Pass = $true; Problems = @() })).Count | Should -Be 0
  }
}

Describe 'Format-ReviewChecksComment' {
  It 'starts with its marker, lists each finding and carries the block' {
    $f = New-ReviewFinding -Id 'x:1' -Rule 'style.en-us' -Severity warning -Body 'Use "behavior".' -Path 'src/a.cs' -Line 3
    $text = Format-ReviewChecksComment -Sha $Sha -RunUrl 'https://example/run' -Findings @($f)
    $text | Should -BeLike '<!-- pr-review-checks -->*'
    $text | Should -Match ([regex]::Escape('| warning | `src/a.cs:3` | style.en-us |'))
    $text | Should -BeLike '*abc1234de*'
    $text | Should -Match 'Gate · Review checks'
    $text | Should -Match 'review-checks:allow'
    @(ConvertFrom-ReviewBlock -Text $text)[0].Findings[0].Id | Should -Be 'x:1'
  }

  It 'says so when there is nothing to report' {
    Format-ReviewChecksComment -Sha $Sha -RunUrl 'u' -Findings @() | Should -BeLike '*No findings*'
  }

  It 'escapes a pipe so a finding cannot break the table' {
    $f = New-ReviewFinding -Id 'x:1' -Rule 'r' -Severity warning -Body 'a | b'
    Format-ReviewChecksComment -Sha $Sha -RunUrl 'u' -Findings @($f) | Should -BeLike '*a \| b*'
  }
}
