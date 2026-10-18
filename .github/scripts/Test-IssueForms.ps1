#!/usr/bin/env pwsh
#Requires -Version 7.0

<#
.SYNOPSIS
    Fails when an issue form would let a request in without what triage needs.

.DESCRIPTION
    Every issue is opened from a form in .github/ISSUE_TEMPLATE (CONTRIBUTING.md, "Issues: types and
    triage"). Each form must have a name and a description, add `needs-triage` and at least one other
    label, and require at least one field. Every label a form adds must be listed in .github/labels.txt:
    GitHub silently drops a label the repository does not define, so a typo would quietly leave issues
    untyped. config.yml must turn blank issues off and route vulnerabilities to the private advisory form.

    The forms are read line by line (top-level keys, the labels line, `required: true`) rather than with
    a YAML parser, which the pipeline-script runner does not carry; the forms are flat enough for that.

    Run by the "Test · Pipeline scripts" job through its Pester test. Exit 0 on pass, 1 on fail.

.PARAMETER Root
    The repository root. Defaults to two levels above this script.
#>
[CmdletBinding()]
param(
  [string]$Root = (Join-Path $PSScriptRoot '..' '..')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-IssueFormProblem {
  <# .SYNOPSIS One message per problem in one issue form's text. #>
  param(
    [Parameter(Mandatory)][AllowEmptyString()][string]$Text,
    [Parameter(Mandatory)][string]$Path,
    [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$KnownLabels
  )
  if ($Text -notmatch '(?m)^name:\s*\S') { "$Path has no name." }
  if ($Text -notmatch '(?m)^description:\s*\S') { "$Path has no description." }
  $labels = if ($Text -match '(?m)^labels:\s*\[(?<list>[^\]]*)\]') {
    @([regex]::Matches($Matches['list'], '"([^"]+)"') | ForEach-Object { $_.Groups[1].Value })
  } else { @() }
  if ($labels -notcontains 'needs-triage') { "$Path does not add the needs-triage label." }
  if (@($labels | Where-Object { $_ -ne 'needs-triage' }).Count -eq 0) { "$Path adds no type label." }
  foreach ($label in $labels | Where-Object { $_ -notin $KnownLabels }) {
    "$Path adds the label '$label', which .github/labels.txt does not define, so GitHub would drop it."
  }
  if ($Text -notmatch '(?m)^\s+required:\s*true\s*$') { "$Path has no required field." }
}

function Get-IssueConfigProblem {
  <# .SYNOPSIS One message per problem in .github/ISSUE_TEMPLATE/config.yml. #>
  param([Parameter(Mandatory)][AllowEmptyString()][string]$Text)
  if ($Text -notmatch '(?m)^blank_issues_enabled:\s*false\s*$') {
    'config.yml leaves blank issues on, so an issue can skip every form.'
  }
  if ($Text -notmatch '/security/advisories/new') {
    'config.yml does not route vulnerabilities to the private advisory form.'
  }
}

function Get-RepositoryIssueFormProblem {
  <# .SYNOPSIS Every problem in the repository's issue forms and config. #>
  param([Parameter(Mandatory)][string]$Root)
  $dir = Join-Path $Root '.github' 'ISSUE_TEMPLATE'
  $labelsFile = Join-Path $Root '.github' 'labels.txt'
  $known = @(Get-Content -LiteralPath $labelsFile | ForEach-Object { ($_ -split '\|', 2)[0].Trim() } |
      Where-Object { $_ -and -not $_.StartsWith('#') })
  foreach ($form in Get-ChildItem -LiteralPath $dir -Filter '*.yml' | Where-Object Name -NE 'config.yml') {
    Get-IssueFormProblem -Text (Get-Content -Raw -LiteralPath $form.FullName) -Path ".github/ISSUE_TEMPLATE/$($form.Name)" -KnownLabels $known
  }
  Get-IssueConfigProblem -Text (Get-Content -Raw -LiteralPath (Join-Path $dir 'config.yml'))
}

# Dot-sourcing for tests loads the functions without running the check.
if ($MyInvocation.InvocationName -eq '.') { return }

$problems = @(Get-RepositoryIssueFormProblem -Root $Root)
if ($problems.Count -eq 0) {
  Write-Output 'Issue forms: every form adds a type label and needs-triage, and requires its key fields.'
  exit 0
}
$problems | ForEach-Object { Write-Output "::error::$_" }
exit 1
