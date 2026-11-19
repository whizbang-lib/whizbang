# Copyright (c) whizbang-lib contributors.
# SPDX-License-Identifier: MIT

#Requires -Modules Pester

# A stable release's version is the line's base plus, for each prerelease label published since the last
# stable, that label's latest number, added to the target component (#2136). The cut computes the release
# branch's number from GitVersion's alpha; the stable publish adds the betas and rcs published from the
# branch. Every row of the issue's table is a test here, through the math and through both run modes.

BeforeAll {
  $script:ScriptPath = Join-Path -Path $PSScriptRoot -ChildPath '..' -AdditionalChildPath 'Get-ReleaseVersion.ps1'
  . $ScriptPath

  function New-Prerelease([hashtable]$counts) {
    $d = [ordered]@{}
    foreach ($k in $counts.Keys) { $d[$k] = $counts[$k] }
    return $d
  }

  # Every tag a line has published for one label: v<line>-<label>.1 .. v<line>-<label>.<n>.
  function New-LineTags([string]$line, [string]$label, [int]$n) {
    1..$n | ForEach-Object { "v$line-$label.$_" }
  }

  # Runs the script as the workflows do, in a child pwsh, and returns its exit code, stdout and outputs.
  function Invoke-Script([string[]]$arguments, [hashtable]$environment = @{}) {
    $out = Join-Path $TestDrive "output-$([guid]::NewGuid()).txt"
    $summary = Join-Path $TestDrive "summary-$([guid]::NewGuid()).md"
    $saved = @{}
    $environment['GITHUB_OUTPUT'] = $out
    $environment['GITHUB_STEP_SUMMARY'] = $summary
    foreach ($k in $environment.Keys) { $saved[$k] = [Environment]::GetEnvironmentVariable($k); [Environment]::SetEnvironmentVariable($k, $environment[$k]) }
    try {
      $stdout = & pwsh -NoProfile -File $ScriptPath @arguments 2>&1 | Out-String
      $code = $LASTEXITCODE
    } finally {
      foreach ($k in $saved.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k]) }
    }
    $outputs = @{}
    if (Test-Path $out) {
      foreach ($l in Get-Content $out) { $name, $value = $l -split '=', 2; $outputs[$name] = $value }
    }
    $summaryText = if (Test-Path $summary) { Get-Content -Raw $summary } else { '' }
    return [pscustomobject]@{ Code = $code; Stdout = $stdout; Outputs = $outputs; Summary = $summaryText }
  }
}

Describe 'Get-ReleaseRollup: the issue''s rows' {
  It '0.2627.0-alpha.674 releases as 0.3301.0' {
    $r = Get-ReleaseRollup -Base '0.2627.0' -Prerelease (New-Prerelease @{ alpha = 674 })
    $r.Version | Should -Be '0.3301.0'
    $r.Sum | Should -Be 674
    $r.Summary | Should -Be 'alpha 674 = 674 -> 0.3301.0'
  }

  It 'alpha.674 then beta.5 releases as 0.3306.0' {
    $r = Get-ReleaseRollup -Base '0.2627.0' -Prerelease (New-Prerelease @{ alpha = 674; beta = 5 })
    $r.Version | Should -Be '0.3306.0'
    $r.Summary | Should -Be 'alpha 674 + beta 5 = 679 -> 0.3306.0'
  }

  It 'alpha.674, beta.5 and rc.3 release as 0.3309.0, and the summary shows the inputs and the sum' {
    $r = Get-ReleaseRollup -Base '0.2627.0' -Prerelease (New-Prerelease @{ rc = 3; alpha = 674; beta = 5 })
    $r.Version | Should -Be '0.3309.0'
    $r.Sum | Should -Be 682
    $r.Summary | Should -Be 'alpha 674 + beta 5 + rc 3 = 682 -> 0.3309.0'
  }

  It 'alpha.2, beta.5 and rc.3 release as 0.2637.0' {
    $r = Get-ReleaseRollup -Base '0.2627.0' -Prerelease (New-Prerelease @{ alpha = 2; beta = 5; rc = 3 })
    $r.Version | Should -Be '0.2637.0'
    $r.Summary | Should -Be 'alpha 2 + beta 5 + rc 3 = 10 -> 0.2637.0'
  }
}

Describe 'Get-ReleaseRollup: the rule' {
  It 'counts a label it has never heard of, in SemVer label order' {
    $r = Get-ReleaseRollup -Base '0.2627.0' -Prerelease (New-Prerelease @{ rc = 3; preview = 4; alpha = 2 })
    $r.Version | Should -Be '0.2636.0'
    $r.Summary | Should -Be 'alpha 2 + preview 4 + rc 3 = 9 -> 0.2636.0'
  }

  It 'releases the base itself when the line published no prereleases' {
    $r = Get-ReleaseRollup -Base '0.2627.0'
    $r.Version | Should -Be '0.2627.0'
    $r.Sum | Should -Be 0
    $r.Summary | Should -Be 'no prereleases -> 0.2627.0'
  }

  It 'resets the patch and drops any suffix when the target is the minor' {
    (Get-ReleaseRollup -Base '1.4.7-alpha.9' -Prerelease (New-Prerelease @{ alpha = 9 })).Version | Should -Be '1.13.0'
  }

  It 'adds to the patch when the target is the patch' {
    $r = Get-ReleaseRollup -Base '1.4.0' -Prerelease (New-Prerelease @{ alpha = 674; beta = 5; rc = 3 }) -Target patch
    $r.Version | Should -Be '1.4.682'
    $r.Summary | Should -Be 'alpha 674 + beta 5 + rc 3 = 682 -> 1.4.682'
  }

  It 'refuses a base that is not a version' {
    { Get-ReleaseRollup -Base 'latest' } | Should -Throw '*not a version*'
  }

  It 'refuses a negative prerelease number' {
    { Get-ReleaseRollup -Base '0.1.0' -Prerelease (New-Prerelease @{ beta = -1 }) } | Should -Throw '*beta*'
  }
}

Describe 'Get-LinePrerelease' {
  It 'discovers each label published on the line and keeps its latest number' {
    $tags = @('v0.3301.0-beta.1', 'refs/tags/v0.3301.0-beta.5', 'v0.3301.0-beta.3', 'v0.3301.0-rc.2', 'v0.3301.0-preview.1')
    $p = Get-LinePrerelease -Line '0.3301.0' -Tags $tags
    @($p.Keys) | Should -Be @('beta', 'preview', 'rc')
    $p['beta'] | Should -Be 5
    $p['rc'] | Should -Be 2
    $p['preview'] | Should -Be 1
  }

  It 'ignores other lines, the stable tag, numeric labels and longer prerelease tags' {
    $tags = @('v0.3301.0', 'v0.33010.0-beta.4', 'v0.3302.0-beta.1', 'v0.3301.0-1.2', 'v0.3301.0-beta.1.2', 'v0.3301.0-beta', 'x0.3301.0-beta.1')
    (Get-LinePrerelease -Line '0.3301.0' -Tags $tags).Count | Should -Be 0
  }

  It 'finds nothing on a line with no tags' {
    (Get-LinePrerelease -Line '0.3301.0' -Tags @()).Count | Should -Be 0
  }
}

Describe 'The rollup record' {
  It 'round-trips the base, the target and each label' {
    $marker = ConvertTo-RollupMarker -Base '0.2627.0' -Target minor -Prerelease (New-Prerelease @{ alpha = 674 })
    $marker | Should -Be '<!-- release-rollup: base=0.2627.0 target=minor alpha=674 -->'
    $r = ConvertFrom-RollupMarker -Text "## Release`n`n$marker`n`nmore text"
    $r.Base | Should -Be '0.2627.0'
    $r.Target | Should -Be 'minor'
    $r.Prerelease['alpha'] | Should -Be 674
  }

  It 'records a pinned line with no labels' {
    $marker = ConvertTo-RollupMarker -Base '0.3301.0' -Target minor
    $marker | Should -Be '<!-- release-rollup: base=0.3301.0 target=minor -->'
    (ConvertFrom-RollupMarker -Text $marker).Prerelease.Count | Should -Be 0
  }

  It 'is absent from a body without one' {
    ConvertFrom-RollupMarker -Text 'chore(release) body' | Should -BeNullOrEmpty
    ConvertFrom-RollupMarker -Text '' | Should -BeNullOrEmpty
  }

  It 'refuses a malformed record rather than guessing: <Case>' -ForEach @(
    @{ Case = 'no base'; Text = '<!-- release-rollup: target=minor alpha=1 -->' }
    @{ Case = 'bad base'; Text = '<!-- release-rollup: base=x target=minor -->' }
    @{ Case = 'bad target'; Text = '<!-- release-rollup: base=0.1.0 target=major -->' }
    @{ Case = 'bad number'; Text = '<!-- release-rollup: base=0.1.0 target=minor alpha=many -->' }
    @{ Case = 'bad token'; Text = '<!-- release-rollup: base=0.1.0 target=minor alpha -->' }
    @{ Case = 'numeric label'; Text = '<!-- release-rollup: base=0.1.0 target=minor 7=1 -->' }
  ) {
    { ConvertFrom-RollupMarker -Text $Text } | Should -Throw '*rollup record*'
  }
}

Describe 'Get-RollupTarget' {
  It 'reads the target from the configuration file' {
    $f = Join-Path $TestDrive 'patch.json'
    '{ "rollupTarget": "patch" }' | Set-Content $f
    Get-RollupTarget -ConfigPath $f | Should -Be 'patch'
  }

  It 'defaults to the minor when the file sets nothing' {
    $f = Join-Path $TestDrive 'empty.json'
    '{ }' | Set-Content $f
    Get-RollupTarget -ConfigPath $f | Should -Be 'minor'
  }

  It 'defaults to the minor when there is no file' {
    Get-RollupTarget -ConfigPath (Join-Path $TestDrive 'missing.json') | Should -Be 'minor'
  }

  It 'refuses a target it cannot apply' {
    $f = Join-Path $TestDrive 'major.json'
    '{ "rollupTarget": "major" }' | Set-Content $f
    { Get-RollupTarget -ConfigPath $f } | Should -Throw '*major*'
  }

  It 'is minor in the repository''s own configuration' {
    $repoConfig = Join-Path $PSScriptRoot '..' '..' 'release-version.json'
    Get-RollupTarget -ConfigPath $repoConfig | Should -Be 'minor'
  }
}

Describe 'Resolve-CutVersion' {
  It 'names the release branch from GitVersion''s base and prerelease number, and records them' {
    $r = Resolve-CutVersion -Base '0.2627.0' -PreReleaseLabel 'alpha' -PreReleaseNumber '674' -Target minor
    $r.Version | Should -Be '0.3301.0'
    $r.Summary | Should -Be 'alpha 674 = 674 -> 0.3301.0'
    $r.Marker | Should -Be '<!-- release-rollup: base=0.2627.0 target=minor alpha=674 -->'
  }

  It 'keeps a pinned line''s number when there is no prerelease number to add' {
    $r = Resolve-CutVersion -Base '0.3301.0' -Target minor
    $r.Version | Should -Be '0.3301.0'
    $r.Marker | Should -Be '<!-- release-rollup: base=0.3301.0 target=minor -->'
  }

  It 'refuses a prerelease number without its label' {
    { Resolve-CutVersion -Base '0.2627.0' -PreReleaseNumber '5' -Target minor } | Should -Throw '*label*'
  }

  It 'refuses a prerelease number that is not a number' {
    { Resolve-CutVersion -Base '0.2627.0' -PreReleaseLabel alpha -PreReleaseNumber 'x' -Target minor } | Should -Throw '*not a number*'
  }
}

Describe 'Resolve-StableVersion' {
  BeforeAll {
    $script:Body = "## Release v0.3301.0`n`n<!-- release-rollup: base=0.2627.0 target=minor alpha=674 -->`n"
  }

  It 'ships the line''s own number when nothing was published from the branch' {
    $r = Resolve-StableVersion -Line '0.3301.0' -Body $Body -Tags @('v0.2626.0')
    $r.Version | Should -Be '0.3301.0'
    $r.Line | Should -Be '0.3301.0'
    $r.Rolled | Should -BeTrue
    $r.Summary | Should -Be 'alpha 674 = 674 -> 0.3301.0'
  }

  It 'adds the betas: alpha.674 then beta.5 ships 0.3306.0' {
    $r = Resolve-StableVersion -Line '0.3301.0' -Body $Body -Tags (New-LineTags '0.3301.0' 'beta' 5)
    $r.Version | Should -Be '0.3306.0'
    $r.Summary | Should -Be 'alpha 674 + beta 5 = 679 -> 0.3306.0'
  }

  It 'adds the betas and rcs: alpha 674 + beta 5 + rc 3 ships 0.3309.0' {
    $tags = @(New-LineTags '0.3301.0' 'beta' 5) + @(New-LineTags '0.3301.0' 'rc' 3) + 'v0.2626.0'
    $r = Resolve-StableVersion -Line '0.3301.0' -Body $Body -Tags $tags
    $r.Version | Should -Be '0.3309.0'
    $r.Summary | Should -Be 'alpha 674 + beta 5 + rc 3 = 682 -> 0.3309.0'
  }

  It 'adds a 2-alpha line''s betas and rcs: 0.2637.0' {
    $body = '<!-- release-rollup: base=0.2627.0 target=minor alpha=2 -->'
    $tags = @(New-LineTags '0.2629.0' 'beta' 5) + @(New-LineTags '0.2629.0' 'rc' 3)
    (Resolve-StableVersion -Line '0.2629.0' -Body $body -Tags $tags).Version | Should -Be '0.2637.0'
  }

  It 'keeps the higher number when a label is both recorded and tagged' {
    $tags = @('v0.3301.0-alpha.3', 'v0.3301.0-beta.1')
    (Resolve-StableVersion -Line '0.3301.0' -Body $Body -Tags $tags).Summary | Should -Be 'alpha 674 + beta 1 = 675 -> 0.3302.0'
  }

  It 'applies the target the cut recorded, not today''s' {
    $body = '<!-- release-rollup: base=1.4.0 target=patch alpha=10 -->'
    (Resolve-StableVersion -Line '1.4.10' -Body $body -Tags @('v1.4.10-rc.2')).Version | Should -Be '1.4.12'
  }

  It 'rolls up a pinned line from its own number' {
    $body = '<!-- release-rollup: base=0.3301.0 target=minor -->'
    $r = Resolve-StableVersion -Line '0.3301.0' -Body $body -Tags (New-LineTags '0.3301.0' 'beta' 3)
    $r.Version | Should -Be '0.3304.0'
    $r.Summary | Should -Be 'beta 3 = 3 -> 0.3304.0'
  }

  It 'ships the branch''s own number when the release PR carries no record (an override or a hotfix)' {
    $r = Resolve-StableVersion -Line '0.3309.1' -Body 'Hotfix' -Tags @('v0.3309.1-beta.2')
    $r.Version | Should -Be '0.3309.1'
    $r.Rolled | Should -BeFalse
    $r.Summary | Should -BeLike '*no rollup record*0.3309.1*'
  }

  It 'refuses a record that does not compute the branch''s number' {
    $body = '<!-- release-rollup: base=0.2627.0 target=minor alpha=600 -->'
    { Resolve-StableVersion -Line '0.3301.0' -Body $body -Tags @() } | Should -Throw '*0.3227.0*0.3301.0*'
  }
}

Describe 'The script, as the workflows run it' {
  It 'Cut writes the version, the record and the summary line' {
    $config = Join-Path $TestDrive 'minor.json'
    '{ "rollupTarget": "minor" }' | Set-Content $config
    $r = Invoke-Script @('-Mode', 'Cut', '-Base', '0.2627.0', '-PreReleaseLabel', 'alpha', '-PreReleaseNumber', '674', '-ConfigPath', $config)
    $r.Code | Should -Be 0
    $r.Outputs['version'] | Should -Be '0.3301.0'
    $r.Outputs['marker'] | Should -Be '<!-- release-rollup: base=0.2627.0 target=minor alpha=674 -->'
    $r.Outputs['summary'] | Should -Be 'alpha 674 = 674 -> 0.3301.0'
    $r.Summary | Should -Match ([regex]::Escape('alpha 674 = 674 -> 0.3301.0'))
    $r.Stdout | Should -Match ([regex]::Escape('alpha 674 = 674 -> 0.3301.0'))
  }

  It 'Cut takes the target from the configuration: patch' {
    $config = Join-Path $TestDrive 'patch-cli.json'
    '{ "rollupTarget": "patch" }' | Set-Content $config
    $r = Invoke-Script @('-Mode', 'Cut', '-Base', '1.4.0', '-PreReleaseLabel', 'alpha', '-PreReleaseNumber', '674', '-ConfigPath', $config)
    $r.Outputs['version'] | Should -Be '1.4.674'
    $r.Outputs['marker'] | Should -Be '<!-- release-rollup: base=1.4.0 target=patch alpha=674 -->'
  }

  It 'Stable reads the record from the environment and the tags from one newline-separated argument' {
    $tags = ((@(New-LineTags '0.3301.0' 'beta' 5) + @(New-LineTags '0.3301.0' 'rc' 3)) -join "`n")
    $r = Invoke-Script @('-Mode', 'Stable', '-Line', '0.3301.0', '-Tags', $tags) @{ RELEASE_PR_BODY = '<!-- release-rollup: base=0.2627.0 target=minor alpha=674 -->' }
    $r.Code | Should -Be 0
    $r.Outputs['version'] | Should -Be '0.3309.0'
    $r.Outputs['line'] | Should -Be '0.3301.0'
    $r.Outputs['rolled'] | Should -Be 'true'
    $r.Outputs['summary'] | Should -Be 'alpha 674 + beta 5 + rc 3 = 682 -> 0.3309.0'
    $r.Summary | Should -Match ([regex]::Escape('alpha 674 + beta 5 + rc 3 = 682 -> 0.3309.0'))
  }

  It 'Stable with no record ships the line and says rolled=false' {
    $r = Invoke-Script @('-Mode', 'Stable', '-Line', '0.3309.1') @{ RELEASE_PR_BODY = '' }
    $r.Code | Should -Be 0
    $r.Outputs['version'] | Should -Be '0.3309.1'
    $r.Outputs['rolled'] | Should -Be 'false'
  }

  It 'fails with an error annotation, and writes no version, when the record is inconsistent' {
    $r = Invoke-Script @('-Mode', 'Stable', '-Line', '0.3301.0') @{ RELEASE_PR_BODY = '<!-- release-rollup: base=0.2627.0 target=minor alpha=1 -->' }
    $r.Code | Should -Be 1
    $r.Stdout | Should -Match '::error::'
    $r.Outputs.ContainsKey('version') | Should -BeFalse
  }

  It 'refuses an unknown mode' {
    $r = Invoke-Script @('-Mode', 'Ship')
    $r.Code | Should -Be 1
    $r.Stdout | Should -Match '::error::.*Mode'
  }
}

# A function no workflow calls decides nothing. These lock the wiring: the cut names the branch with it,
# the stable publish recomputes with it, and a rolled-up stable repacks the tested build.
Describe 'The release workflows use it' {
  BeforeAll {
    $wf = Join-Path $PSScriptRoot '..' '..' 'workflows'
    $script:StartRelease = Get-Content -Raw (Join-Path $wf 'start-release.yml')
    $script:Release = Get-Content -Raw (Join-Path $wf 'release.yml')
    $script:Prerelease = Get-Content -Raw (Join-Path $wf 'release-prerelease.yml')
  }

  It 'Start Release computes the cut with it and records the rollup in the release PR' {
    $StartRelease | Should -Match 'Get-ReleaseVersion\.ps1 -Mode Cut'
    $StartRelease | Should -Match 'steps\.version\.outputs\.marker'
  }

  It 'the stable publish recomputes with it from the release PR body and the line''s tags' {
    $Release | Should -Match 'Get-ReleaseVersion\.ps1 -Mode Stable'
    $Release | Should -Match 'RELEASE_PR_BODY'
  }

  It 'a rolled-up stable repacks the tested build at its own number, and publish takes either package set' {
    $Release | Should -Match '(?s)repack-tested-build:.*?uses: \./\.github/workflows/reusable-pack\.yml'
    $Release | Should -Match "needs\.build-and-pack\.result == 'success' \|\| needs\.repack-tested-build\.result == 'success'"
  }

  It 'Release Prerelease previews the stable number' {
    $Prerelease | Should -Match 'Get-ReleaseVersion\.ps1 -Mode Stable'
  }
}
