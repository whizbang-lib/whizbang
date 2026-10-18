#Requires -Modules Pester

# The security settings drift check (#1468): the live rulesets, Actions settings, environments, access and
# organization settings are read into one normalized document and compared with the committed baseline,
# .github/settings-baseline.json. A difference opens (or updates) one issue; a return to the baseline closes it.

BeforeAll {
  $script:Root = (Resolve-Path (Join-Path $PSScriptRoot '..' '..' '..')).Path
  . (Join-Path $PSScriptRoot '..' 'Test-SettingsDrift.ps1') -Mode Check

  function ConvertTo-Live([string]$Json) { $Json | ConvertFrom-Json }

  # A minimal live API: each path returns what GitHub would, already parsed.
  $script:Api = @{
    'repos/o/r/rulesets'                                       = ConvertTo-Live '[{"id":2},{"id":1}]'
    'repos/o/r/rulesets/1'                                     = ConvertTo-Live '{"id":1,"name":"develop","target":"branch","enforcement":"active","updated_at":"x","conditions":{"ref_name":{"include":["refs/heads/develop"],"exclude":[]}},"rules":[{"type":"required_signatures"},{"type":"deletion"}],"bypass_actors":[{"actor_id":9,"actor_type":"Team","bypass_mode":"pull_request"},{"actor_id":5,"actor_type":"Integration","bypass_mode":"always"}]}'
    'repos/o/r/rulesets/2'                                     = ConvertTo-Live '{"id":2,"name":"main","target":"branch","enforcement":"active","conditions":null,"rules":[],"bypass_actors":[]}'
    'repos/o/r/actions/permissions'                            = ConvertTo-Live '{"enabled":true,"allowed_actions":"selected","sha_pinning_required":true,"selected_actions_url":"u"}'
    'repos/o/r/actions/permissions/selected-actions'           = ConvertTo-Live '{"github_owned_allowed":true,"verified_allowed":true,"patterns_allowed":["z/z@*","a/a@*"]}'
    'repos/o/r/actions/permissions/workflow'                   = ConvertTo-Live '{"default_workflow_permissions":"read","can_approve_pull_request_reviews":false}'
    'repos/o/r/actions/permissions/fork-pr-contributor-approval' = ConvertTo-Live '{"approval_policy":"first_time_contributors"}'
    'orgs/o/actions/permissions'                               = ConvertTo-Live '{"enabled_repositories":"all","allowed_actions":"all","sha_pinning_required":false}'
    'repos/o/r/environments'                                   = ConvertTo-Live '{"environments":[{"name":"cut","deployment_branch_policy":{"custom_branch_policies":true,"protected_branches":false},"protection_rules":[{"id":1,"type":"required_reviewers","prevent_self_review":false,"reviewers":[{"type":"Team","reviewer":{"slug":"admins","id":9}},{"type":"User","reviewer":{"login":"pat","id":3}}]},{"id":2,"type":"wait_timer","wait_timer":5},{"id":3,"type":"branch_policy"}]}]}'
    'repos/o/r/environments/cut/deployment-branch-policies'    = ConvertTo-Live '{"branch_policies":[{"id":1,"name":"main","type":"branch"},{"id":2,"name":"develop","type":"branch"}]}'
    'repos/o/r/teams'                                          = ConvertTo-Live '[{"slug":"writers","permission":"push"},{"slug":"admins","permission":"admin"}]'
    'repos/o/r/collaborators?affiliation=direct&per_page=100'  = ConvertTo-Live '[{"login":"kim","role_name":"write"}]'
    'repos/o/r/invitations'                                    = ConvertTo-Live '[{"invitee":{"login":"lee"},"permissions":"read"}]'
    'orgs/o'                                                   = ConvertTo-Live '{"login":"o","two_factor_requirement_enabled":true,"members_can_create_repositories":false,"members_can_create_public_repositories":false,"members_can_create_private_repositories":false,"default_repository_permission":"read","web_commit_signoff_required":false,"public_repos":12}'
  }
}

Describe 'Get-SecuritySettings' {
  BeforeEach {
    Mock Invoke-GhJson { if (-not $Api.ContainsKey($Path)) { throw "unexpected path $Path" }; $Api[$Path] }
    $script:s = Get-SecuritySettings -Repository 'o/r'
  }

  It 'keys each ruleset by name, without ids or timestamps, with rules and bypass actors in a stable order' {
    $s.rulesets.Keys | Should -Be @('develop', 'main')
    $d = $s.rulesets['develop']
    $d.Keys | Should -Be @('bypass_actors', 'conditions', 'enforcement', 'rules', 'target')
    $d.rules.type | Should -Be @('deletion', 'required_signatures')
    $d.bypass_actors[0].actor_type | Should -Be 'Integration'
    $d.bypass_actors[1].actor_id | Should -Be 9
  }

  It 'reads the Actions settings, the allowed patterns sorted, and the organization defaults' {
    $s.actions.repository.allowed_actions | Should -Be 'selected'
    $s.actions.repository.Keys | Should -Not -Contain 'selected_actions_url'
    $s.actions.selected.patterns_allowed | Should -Be @('a/a@*', 'z/z@*')
    $s.actions.workflow.can_approve_pull_request_reviews | Should -BeFalse
    $s.actions.fork_approval | Should -Be 'first_time_contributors'
    $s.actions.organization.allowed_actions | Should -Be 'all'
  }

  It 'reads no selected-actions list when the setting is not selected' {
    $Api['repos/o/r/actions/permissions'] = ConvertTo-Live '{"enabled":true,"allowed_actions":"all","sha_pinning_required":true}'
    try {
      $s2 = Get-SecuritySettings -Repository 'o/r'
      $s2.actions.selected | Should -BeNullOrEmpty
      Should -Invoke Invoke-GhJson -Times 0 -ParameterFilter { $Path -like '*selected-actions' } -Scope It
    } finally {
      $Api['repos/o/r/actions/permissions'] = ConvertTo-Live '{"enabled":true,"allowed_actions":"selected","sha_pinning_required":true,"selected_actions_url":"u"}'
    }
  }

  It 'reads each environment: reviewers by name, wait timer, and branch policies, sorted' {
    $e = $s.environments['cut']
    $e.reviewers | Should -Be @('Team:admins', 'User:pat')
    $e.prevent_self_review | Should -BeFalse
    $e.wait_timer | Should -Be 5
    $e.branch_policies | Should -Be @('branch:develop', 'branch:main')
    $e.deployment_branch_policy.custom_branch_policies | Should -BeTrue
  }

  It 'reads who has access: teams, direct collaborators and pending invitations, by role' {
    $s.access.teams.Keys | Should -Be @('admins', 'writers')
    $s.access.teams['admins'] | Should -Be 'admin'
    $s.access.collaborators['kim'] | Should -Be 'write'
    $s.access.invitations['lee'] | Should -Be 'read'
  }

  It 'reads only the security-relevant organization settings' {
    $s.organization.two_factor_requirement_enabled | Should -BeTrue
    $s.organization.Keys | Should -Not -Contain 'public_repos'
    $s.organization.Keys | Should -Not -Contain 'login'
  }

  It 'fails, rather than reporting a setting as missing, when a read fails' {
    Mock Invoke-GhJson { throw 'HTTP 403' } -ParameterFilter { $Path -eq 'orgs/o' }
    { Get-SecuritySettings -Repository 'o/r' } | Should -Throw '*403*'
  }
}

Describe 'Compare-SecuritySettings' {
  It 'finds nothing when the two are the same' {
    $a = '{"x":{"y":[1,2]},"z":"q"}' | ConvertFrom-Json -AsHashtable
    @(Compare-SecuritySettings -Baseline $a -Live $a).Count | Should -Be 0
  }

  It 'names each changed, added and removed setting by its path' {
    $baseline = '{"rulesets":{"develop":{"rules":[{"type":"a"},{"type":"b"}]}},"org":{"twofa":true,"gone":1}}' | ConvertFrom-Json -AsHashtable
    $live = '{"rulesets":{"develop":{"rules":[{"type":"a"}]}},"org":{"twofa":false,"new":2}}' | ConvertFrom-Json -AsHashtable
    $d = @(Compare-SecuritySettings -Baseline $baseline -Live $live)
    $d.Setting | Should -Be @('org.gone', 'org.new', 'org.twofa', 'rulesets.develop.rules[1].type')
    ($d | Where-Object Setting -EQ 'org.twofa').Baseline | Should -Be 'true'
    ($d | Where-Object Setting -EQ 'org.twofa').Live | Should -Be 'false'
    ($d | Where-Object Setting -EQ 'org.gone').Live | Should -Be '(absent)'
    ($d | Where-Object Setting -EQ 'org.new').Baseline | Should -Be '(absent)'
    ($d | Where-Object Setting -EQ 'rulesets.develop.rules[1].type').Live | Should -Be '(absent)'
  }

  It 'tells an empty list and an empty object from an absent setting' {
    $d = @(Compare-SecuritySettings -Baseline (@{ a = @(); b = @{} }) -Live (@{}))
    $d.Setting | Should -Be @('a', 'b')
    $d[0].Baseline | Should -Be '[]'
    $d[1].Baseline | Should -Be '{}'
  }
}

Describe 'Format-DriftIssueBody' {
  It 'lists each difference in a table, with the run and how to resolve it' {
    $body = Format-DriftIssueBody -Differences @([pscustomobject]@{ Setting = 'org.twofa'; Baseline = 'true'; Live = 'fal|se' }) -RunUrl 'https://run'
    $body | Should -Match '\| `org.twofa` \| `true` \| `fal\\\|se` \|'
    $body | Should -Match 'https://run'
    $body | Should -Match 'settings-baseline\.json'
  }
}

Describe 'Update-DriftIssue' {
  BeforeEach {
    $script:gh = [System.Collections.Generic.List[string]]::new()
    $script:open = @()
    Mock Invoke-Gh { $script:gh.Add(($args -join ' ')); if ($args[1] -eq 'list') { return ($script:open | ConvertTo-Json -AsArray) } }
    $script:diff = @([pscustomobject]@{ Setting = 'a'; Baseline = '1'; Live = '2' })
  }

  It 'opens an issue labeled security when there is drift and none is open' {
    Update-DriftIssue -Repository 'o/r' -Differences $diff -RunUrl 'u'
    @($gh | Where-Object { $_ -like 'issue create*' }).Count | Should -Be 1
    ($gh | Where-Object { $_ -like 'issue create*' }) | Should -Match '--label security'
  }

  It 'updates the open issue instead of opening another' {
    $script:open = @(@{ number = 7 })
    Update-DriftIssue -Repository 'o/r' -Differences $diff -RunUrl 'u'
    @($gh | Where-Object { $_ -like 'issue create*' }).Count | Should -Be 0
    @($gh | Where-Object { $_ -like 'issue edit 7*' }).Count | Should -Be 1
  }

  It 'closes the open issue when the settings are back to the baseline' {
    $script:open = @(@{ number = 7 })
    Update-DriftIssue -Repository 'o/r' -Differences @() -RunUrl 'u'
    @($gh | Where-Object { $_ -like 'issue close 7*' }).Count | Should -Be 1
  }

  It 'does nothing when there is no drift and no issue' {
    Update-DriftIssue -Repository 'o/r' -Differences @() -RunUrl 'u'
    @($gh | Where-Object { $_ -notlike 'issue list*' }).Count | Should -Be 0
  }
}

Describe 'The committed baseline' {
  BeforeAll {
    $script:Baseline = Get-Content -Raw (Join-Path $Root '.github' 'settings-baseline.json') | ConvertFrom-Json -AsHashtable
  }

  It 'covers every section' {
    $Baseline.Keys | Sort-Object | Should -Be @('access', 'actions', 'environments', 'organization', 'rulesets')
  }

  It 'allows exactly the actions .github/allowed-actions.txt lists, so the two change together' {
    . (Join-Path $Root '.github' 'scripts' 'Get-AllowedActions.ps1')
    $listed = @(Read-AllowedActionList -Path (Join-Path $Root '.github' 'allowed-actions.txt') | Sort-Object)
    $Baseline.actions.repository.allowed_actions | Should -Be 'selected'
    @($Baseline.actions.selected.patterns_allowed) | Should -Be $listed
  }

  It 'requires signed commits and code-owner review on develop and main' {
    foreach ($name in @('Require PR for protected branches', 'Require PR for main branch')) {
      $rules = $Baseline.rulesets[$name].rules
      $rules.type | Should -Contain 'required_signatures'
      (@($rules | Where-Object type -EQ 'pull_request')[0]).parameters.require_code_owner_review | Should -BeTrue
    }
  }
}
