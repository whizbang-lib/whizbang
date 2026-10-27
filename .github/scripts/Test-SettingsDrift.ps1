#!/usr/bin/env pwsh
# Copyright (c) whizbang-lib contributors.
# SPDX-License-Identifier: MIT

#Requires -Version 7.0

<#
.SYNOPSIS
    Detects drift in the repository's security settings: reads them into one normalized document and
    compares it with the committed baseline, .github/settings-baseline.json.

.DESCRIPTION
    Rulesets, Actions settings, environments and access can be loosened by an admin, or by a stolen admin
    session, and nothing in the repository would notice (#1468). The settings-drift workflow runs this
    weekly: Capture reads the live settings with a read-only release app token, Check compares them with
    the baseline and opens (or updates) one issue labeled security that lists each difference, or closes
    that issue once the settings match again.

    What is read, each without ids, URLs or timestamps, in a stable order:
      - rulesets, by name: target, enforcement, conditions, rules and bypass actors;
      - Actions: the repository and organization permissions, the allowed-actions list when the setting is
        "selected", the default token permissions, whether Actions can approve pull requests, and the
        approval policy for fork pull requests;
      - environments, by name: required reviewers, self-review, wait timer and deployment branch policies;
      - access: the teams, direct collaborators and pending invitations, each with its role;
      - organization: two-factor requirement, repository creation by members, default repository permission
        and web commit sign-off.

    A read that fails stops the run: a setting that could not be read is never reported as removed.
    A field GitHub leaves out for a read-only reader (a ruleset's bypass actors, shown only to someone
    who can edit the ruleset) is recorded as not visible: it is neither compared nor reported as a
    difference, and the report lists it as not checked. The baseline, captured by an admin, still
    records it, so a change to it is reviewed in the baseline pull request.

    Changing a setting on purpose means changing the baseline in a pull request, which CODEOWNERS gives to
    build-admins: run -Mode Capture -OutFile .github/settings-baseline.json with an admin's gh login.

.PARAMETER Mode
    Capture writes the live settings to -OutFile. Check compares -LivePath with -BaselinePath, updates the
    drift issue, and exits 1 when they differ.

.EXAMPLE
    pwsh .github/scripts/Test-SettingsDrift.ps1 -Mode Capture -OutFile .github/settings-baseline.json
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)][ValidateSet('Capture', 'Check')][string]$Mode,
  [string]$Repository = $env:GITHUB_REPOSITORY,
  [string]$OutFile,
  [string]$BaselinePath = (Join-Path $PSScriptRoot '..' 'settings-baseline.json'),
  [string]$LivePath,
  [string]$RunUrl
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:DriftIssueTitle = 'Security settings differ from the baseline'
$script:NotVisible = '(not visible to this reader)'
$script:OrganizationKeys = @(
  'default_repository_permission', 'members_can_create_private_repositories', 'members_can_create_public_repositories',
  'members_can_create_repositories', 'two_factor_requirement_enabled', 'web_commit_signoff_required')

function Invoke-GhJson {
  <# .SYNOPSIS One GET through gh api, parsed; throws when it fails. #>
  param([Parameter(Mandatory)][string]$Path)
  $out = & gh api $Path 2>&1
  if ($LASTEXITCODE -ne 0) { throw "gh api $Path failed (exit $LASTEXITCODE): $($out -join ' ')" }
  return ($out -join "`n") | ConvertFrom-Json
}

function Invoke-Gh {
  <# .SYNOPSIS Runs gh with the given arguments and returns its output; throws when it fails. #>
  $out = & gh @args 2>&1
  if ($LASTEXITCODE -ne 0) { throw "gh $($args[0]) $($args[1]) failed (exit $LASTEXITCODE): $($out -join ' ')" }
  return ($out -join "`n")
}

function ConvertTo-SortedValue {
  <# .SYNOPSIS A parsed JSON value as ordered dictionaries with sorted keys, so it serializes the same way every time. #>
  param([AllowNull()]$Value)
  if ($null -eq $Value) { return $null }
  if ($Value -is [System.Management.Automation.PSCustomObject]) {
    $sorted = [ordered]@{}
    foreach ($name in @($Value.PSObject.Properties.Name | Sort-Object)) { $sorted[$name] = ConvertTo-SortedValue $Value.$name }
    return $sorted
  }
  if ($Value -is [System.Collections.IDictionary]) {
    $sorted = [ordered]@{}
    foreach ($name in @($Value.Keys | Sort-Object)) { $sorted[$name] = ConvertTo-SortedValue $Value[$name] }
    return $sorted
  }
  if ($Value -is [System.Collections.IList]) { return , @($Value | ForEach-Object { ConvertTo-SortedValue $_ }) }
  return $Value
}

function Get-RulesetSetting {
  <# .SYNOPSIS Each ruleset by name: target, enforcement, conditions, rules (by type) and bypass actors. #>
  param([Parameter(Mandatory)][string]$Repository)
  $byName = [ordered]@{}
  $rulesets = @(Invoke-GhJson -Path "repos/$Repository/rulesets" | ForEach-Object { Invoke-GhJson -Path "repos/$Repository/rulesets/$($_.id)" })
  foreach ($r in @($rulesets | Sort-Object name)) {
    # GitHub returns bypass_actors only to a reader who can edit the ruleset.
    $bypass = if ($r.PSObject.Properties['bypass_actors']) {
      @(@($r.bypass_actors) | Where-Object { $_ } | Sort-Object actor_type, actor_id |
          ForEach-Object { [ordered]@{ actor_id = $_.actor_id; actor_type = $_.actor_type; bypass_mode = $_.bypass_mode } })
    } else { $NotVisible }
    $byName[$r.name] = [ordered]@{
      bypass_actors = $bypass
      conditions    = ConvertTo-SortedValue $r.conditions
      enforcement   = $r.enforcement
      rules         = @(@($r.rules) | Where-Object { $_ } | Sort-Object type | ForEach-Object { ConvertTo-SortedValue $_ })
      target        = $r.target
    }
  }
  return $byName
}

function Get-ActionsSetting {
  <# .SYNOPSIS The repository's and organization's Actions permissions, allowed actions, token defaults and fork approval. #>
  param([Parameter(Mandatory)][string]$Repository)
  $organization = $Repository.Split('/')[0]
  $permissions = Invoke-GhJson -Path "repos/$Repository/actions/permissions"
  $selected = $null
  if ($permissions.allowed_actions -eq 'selected') {
    $s = Invoke-GhJson -Path "repos/$Repository/actions/permissions/selected-actions"
    $selected = [ordered]@{
      github_owned_allowed = $s.github_owned_allowed
      patterns_allowed     = @(@($s.patterns_allowed) | Where-Object { $_ } | Sort-Object)
      verified_allowed     = $s.verified_allowed
    }
  }
  $workflow = Invoke-GhJson -Path "repos/$Repository/actions/permissions/workflow"
  $org = Invoke-GhJson -Path "orgs/$organization/actions/permissions"
  return [ordered]@{
    fork_approval = (Invoke-GhJson -Path "repos/$Repository/actions/permissions/fork-pr-contributor-approval").approval_policy
    organization  = [ordered]@{ allowed_actions = $org.allowed_actions; enabled_repositories = $org.enabled_repositories; sha_pinning_required = $org.sha_pinning_required }
    repository    = [ordered]@{ allowed_actions = $permissions.allowed_actions; enabled = $permissions.enabled; sha_pinning_required = $permissions.sha_pinning_required }
    selected      = $selected
    workflow      = [ordered]@{ can_approve_pull_request_reviews = $workflow.can_approve_pull_request_reviews; default_workflow_permissions = $workflow.default_workflow_permissions }
  }
}

function Get-EnvironmentSetting {
  <# .SYNOPSIS Each environment by name: reviewers, self-review, wait timer and deployment branch policies. #>
  param([Parameter(Mandatory)][string]$Repository)
  $byName = [ordered]@{}
  foreach ($e in @((Invoke-GhJson -Path "repos/$Repository/environments").environments | Where-Object { $_ } | Sort-Object name)) {
    $rules = @($e.protection_rules | Where-Object { $_ })
    $review = @($rules | Where-Object type -EQ 'required_reviewers') | Select-Object -First 1
    $timer = @($rules | Where-Object type -EQ 'wait_timer') | Select-Object -First 1
    $policies = @()
    if ($e.deployment_branch_policy -and $e.deployment_branch_policy.custom_branch_policies) {
      $policies = @((Invoke-GhJson -Path "repos/$Repository/environments/$($e.name)/deployment-branch-policies").branch_policies |
          Where-Object { $_ } | ForEach-Object { "$($_.type):$($_.name)" } | Sort-Object)
    }
    $byName[$e.name] = [ordered]@{
      branch_policies          = $policies
      deployment_branch_policy = ConvertTo-SortedValue $e.deployment_branch_policy
      prevent_self_review      = [bool]($review -and $review.prevent_self_review)
      reviewers                = @(if ($review) {
          $review.reviewers | ForEach-Object { if ($_.type -eq 'Team') { "Team:$($_.reviewer.slug)" } else { "$($_.type):$($_.reviewer.login)" } } | Sort-Object
        })
      wait_timer               = if ($timer) { $timer.wait_timer } else { 0 }
    }
  }
  return $byName
}

function Get-AccessSetting {
  <# .SYNOPSIS Who has access: the teams, direct collaborators and pending invitations, each with its role. #>
  param([Parameter(Mandatory)][string]$Repository)
  $teams = [ordered]@{}
  foreach ($t in @(Invoke-GhJson -Path "repos/$Repository/teams" | Sort-Object slug)) { $teams[$t.slug] = $t.permission }
  $collaborators = [ordered]@{}
  foreach ($c in @(Invoke-GhJson -Path "repos/$Repository/collaborators?affiliation=direct&per_page=100" | Sort-Object login)) { $collaborators[$c.login] = $c.role_name }
  $invitations = [ordered]@{}
  foreach ($i in @(Invoke-GhJson -Path "repos/$Repository/invitations" | Sort-Object { $_.invitee.login })) { $invitations[$i.invitee.login] = $i.permissions }
  return [ordered]@{ collaborators = $collaborators; invitations = $invitations; teams = $teams }
}

function Get-SecuritySettings {
  <# .SYNOPSIS The security-relevant settings of a repository and its organization, normalized. #>
  param([Parameter(Mandatory)][string]$Repository)
  $org = Invoke-GhJson -Path "orgs/$($Repository.Split('/')[0])"
  $organization = [ordered]@{}
  foreach ($key in $OrganizationKeys) { $organization[$key] = $org.$key }
  return [ordered]@{
    access       = Get-AccessSetting -Repository $Repository
    actions      = Get-ActionsSetting -Repository $Repository
    environments = Get-EnvironmentSetting -Repository $Repository
    organization = $organization
    rulesets     = Get-RulesetSetting -Repository $Repository
  }
}

function ConvertTo-FlatSetting {
  <# .SYNOPSIS Every leaf of a settings document by its path (a.b[0].c), as compact JSON. #>
  param([AllowNull()]$Value, [string]$Prefix = '', [Parameter(Mandatory)][hashtable]$Into)
  if (-not $Prefix -and $Value -is [System.Collections.IDictionary] -and $Value.Count -eq 0) { return }
  if ($Value -is [System.Collections.IDictionary] -and $Value.Count -gt 0) {
    foreach ($key in $Value.Keys) {
      $path = if ($Prefix) { "$Prefix.$key" } else { "$key" }
      ConvertTo-FlatSetting -Value $Value[$key] -Prefix $path -Into $Into
    }
    return
  }
  if ($Value -is [System.Collections.IList] -and $Value -isnot [string] -and $Value.Count -gt 0) {
    for ($i = 0; $i -lt $Value.Count; $i++) { ConvertTo-FlatSetting -Value $Value[$i] -Prefix "$Prefix[$i]" -Into $Into }
    return
  }
  $Into[$Prefix] = if ($Value -is [System.Collections.IDictionary]) { '{}' }
  elseif ($Value -is [System.Collections.IList] -and $Value -isnot [string]) { '[]' }
  else { ConvertTo-Json -InputObject $Value -Compress }
}

function Get-UncheckedSetting {
  <# .SYNOPSIS The path of each live setting the reader could not see, sorted. #>
  param([Parameter(Mandatory)]$Live)
  $l = @{}; ConvertTo-FlatSetting -Value $Live -Into $l
  $hidden = ConvertTo-Json -InputObject $NotVisible -Compress
  @($l.Keys | Where-Object { $l[$_] -ceq $hidden } | Sort-Object { $_ } -CaseSensitive)
}

function Compare-SecuritySettings {
  <#
  .SYNOPSIS
      Each setting that differs between the baseline and the live settings: Setting, Baseline, Live. A live
      setting the reader could not see is skipped, with everything the baseline has under it.
  #>
  param([Parameter(Mandatory)]$Baseline, [Parameter(Mandatory)]$Live)
  $b = @{}; ConvertTo-FlatSetting -Value $Baseline -Into $b
  $l = @{}; ConvertTo-FlatSetting -Value $Live -Into $l
  $unchecked = @(Get-UncheckedSetting -Live $Live)
  $paths = [System.Collections.Generic.SortedSet[string]]::new([StringComparer]::Ordinal)
  foreach ($p in @($b.Keys) + @($l.Keys)) { [void]$paths.Add($p) }
  foreach ($p in $paths) {
    if ($unchecked | Where-Object { $p -eq $_ -or $p.StartsWith("$_[") -or $p.StartsWith("$_.") }) { continue }
    $expected = if ($b.ContainsKey($p)) { $b[$p] } else { '(absent)' }
    $actual = if ($l.ContainsKey($p)) { $l[$p] } else { '(absent)' }
    if ($expected -cne $actual) { [pscustomobject]@{ Setting = $p; Baseline = $expected; Live = $actual } }
  }
}

function Format-DriftIssueBody {
  <# .SYNOPSIS The drift issue's body: what differs, where it was found, and how to resolve it. #>
  param([Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Differences, [string]$RunUrl, [string[]]$Unchecked = @())
  $cell = { param($s) '`' + ("$s" -replace '\|', '\|') + '`' }
  $notChecked = if ($Unchecked.Count -gt 0) {
    @('', "**Not checked** (the read-only reader cannot see them; the baseline records them, so a change is reviewed there): $(($Unchecked | ForEach-Object { & $cell $_ }) -join ', ').")
  } else { @() }
  $rows = $Differences | ForEach-Object { "| $(& $cell $_.Setting) | $(& $cell $_.Baseline) | $(& $cell $_.Live) |" }
  return @(
    "The repository's security settings no longer match [.github/settings-baseline.json](../blob/develop/.github/settings-baseline.json) ($($Differences.Count) difference(s), found by [the settings drift check]($RunUrl))."
    ''
    '| Setting | Baseline | Live |'
    '|---|---|---|'
    $rows
    ''
    $notChecked
    ''
    '**To resolve:** if the change was intended, update the baseline in a pull request (`pwsh .github/scripts/Test-SettingsDrift.ps1 -Mode Capture -OutFile .github/settings-baseline.json`), which a build admin approves. If it was not, put the setting back and find out who changed it in the organization audit log. This issue closes itself on the first run that finds no difference.'
  ) -join "`n"
}

function Update-DriftIssue {
  <# .SYNOPSIS Opens or updates the one drift issue when there are differences; closes it when there are none. #>
  param(
    [Parameter(Mandatory)][string]$Repository,
    [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Differences,
    [string]$RunUrl,
    [string[]]$Unchecked = @()
  )
  $listed = Invoke-Gh issue list -R $Repository --state open --label security --search "`"$DriftIssueTitle`" in:title" --json number
  $open = @(if ($listed) { @($listed | ConvertFrom-Json) | Where-Object { $_ } })
  $number = if ($open.Count -gt 0) { $open[0].number } else { $null }
  if ($Differences.Count -eq 0) {
    if ($number) { [void](Invoke-Gh issue close $number -R $Repository --comment "The settings match the baseline again ([run]($RunUrl)).") }
    return
  }
  $body = Format-DriftIssueBody -Differences $Differences -RunUrl $RunUrl -Unchecked $Unchecked
  if ($number) {
    [void](Invoke-Gh issue edit $number -R $Repository --body $body)
  } else {
    [void](Invoke-Gh issue create -R $Repository --title $DriftIssueTitle --label security --body $body)
  }
}

# Dot-sourcing for tests loads the functions without running anything.
if ($MyInvocation.InvocationName -eq '.') { return }

if (-not $Repository) { throw 'Set -Repository (owner/name) or GITHUB_REPOSITORY.' }
if ($Mode -eq 'Capture') {
  if (-not $OutFile) { throw 'Capture needs -OutFile.' }
  $settings = Get-SecuritySettings -Repository $Repository
  Set-Content -LiteralPath $OutFile -Value (ConvertTo-Json -InputObject $settings -Depth 30) -Encoding utf8NoBOM
  Write-Host "Wrote the security settings of $Repository to $OutFile."
  exit 0
}

if (-not $LivePath) { throw 'Check needs -LivePath (the output of -Mode Capture).' }
$baseline = Get-Content -Raw -LiteralPath $BaselinePath | ConvertFrom-Json -AsHashtable
$live = Get-Content -Raw -LiteralPath $LivePath | ConvertFrom-Json -AsHashtable
$differences = @(Compare-SecuritySettings -Baseline $baseline -Live $live)
$unchecked = @(Get-UncheckedSetting -Live $live)
Update-DriftIssue -Repository $Repository -Differences $differences -RunUrl $RunUrl -Unchecked $unchecked
if ($env:GITHUB_STEP_SUMMARY) { Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value (Format-DriftIssueBody -Differences $differences -RunUrl $RunUrl -Unchecked $unchecked) }
if ($unchecked.Count -gt 0) { Write-Host "Not checked (not visible to this reader): $($unchecked -join ', ')" }
if ($differences.Count -gt 0) {
  $differences | Format-Table -AutoSize | Out-String | Write-Host
  Write-Host "::error::$($differences.Count) security setting(s) differ from the baseline."
  exit 1
}
Write-Host 'The security settings match the baseline.'
exit 0
