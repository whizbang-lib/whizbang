# Copyright (c) whizbang-lib contributors.
# SPDX-License-Identifier: MIT

#Requires -Modules Pester

BeforeAll {
  . (Join-Path -Path $PSScriptRoot -ChildPath '..' -AdditionalChildPath 'Test-IssueForms.ps1')
  $script:Root = Join-Path $PSScriptRoot '..' '..' '..'

  $script:GoodForm = @'
name: Bug report
description: Something does not work as documented.
labels: ["bug", "needs-triage"]
body:
  - type: textarea
    id: repro
    attributes:
      label: Minimal reproduction
    validations:
      required: true
'@
}

Describe 'Get-IssueFormProblem' {
  It 'accepts a form with a name, a description, a type label, needs-triage and a required field' {
    @(Get-IssueFormProblem -Text $GoodForm -Path 'bug.yml' -KnownLabels @('bug', 'needs-triage')) | Should -BeNullOrEmpty
  }

  It 'reports a form that does not add needs-triage' {
    $text = $GoodForm.Replace('labels: ["bug", "needs-triage"]', 'labels: ["bug"]')
    $problems = @(Get-IssueFormProblem -Text $text -Path 'bug.yml' -KnownLabels @('bug', 'needs-triage'))
    $problems | Should -Match 'needs-triage'
  }

  It 'reports a label the repository does not define, which GitHub would silently drop' {
    $problems = @(Get-IssueFormProblem -Text $GoodForm -Path 'bug.yml' -KnownLabels @('needs-triage'))
    $problems | Should -Match "'bug'"
  }

  It 'reports a form with no required field' {
    $text = $GoodForm.Replace('required: true', 'required: false')
    @(Get-IssueFormProblem -Text $text -Path 'bug.yml' -KnownLabels @('bug', 'needs-triage')) | Should -Match 'required'
  }

  It 'reports a form with no name or description' {
    $text = ($GoodForm -split "`n" | Where-Object { $_ -notmatch '^(name|description):' }) -join "`n"
    $problems = @(Get-IssueFormProblem -Text $text -Path 'bug.yml' -KnownLabels @('bug', 'needs-triage'))
    ($problems -join "`n") | Should -Match 'no name'
    ($problems -join "`n") | Should -Match 'no description'
  }
}

Describe 'Get-IssueConfigProblem' {
  It 'accepts a config that turns blank issues off and routes vulnerabilities privately' {
    $text = @'
blank_issues_enabled: false
contact_links:
  - name: Report a security vulnerability
    url: https://github.com/o/r/security/advisories/new
    about: Privately.
'@
    @(Get-IssueConfigProblem -Text $text) | Should -BeNullOrEmpty
  }

  It 'reports blank issues left on and no private vulnerability route' {
    $problems = @(Get-IssueConfigProblem -Text "blank_issues_enabled: true`ncontact_links: []")
    ($problems -join "`n") | Should -Match 'blank'
    ($problems -join "`n") | Should -Match 'vulnerabilit'
  }
}

Describe 'The repository' {
  It 'has issue forms that each pass, and a config that routes questions and vulnerabilities away from issues' {
    $problems = @(Get-RepositoryIssueFormProblem -Root $Root)
    $problems | Should -BeNullOrEmpty
    # The independent fact: the bug, enhancement, feature, proposal and documentation forms all exist.
    $names = @(Get-ChildItem (Join-Path $Root '.github' 'ISSUE_TEMPLATE') -Filter '*.yml' | Where-Object Name -NE 'config.yml').BaseName
    $names | Should -Contain 'bug'
    $names | Should -Contain 'enhancement'
    $names | Should -Contain 'feature'
    $names | Should -Contain 'proposal'
    $names | Should -Contain 'documentation'
  }
}
