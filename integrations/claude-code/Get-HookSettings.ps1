param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9 _.-]{0,47}$')]
    [string] $WorkspaceAlias,
    [string] $OutputDirectory = (Join-Path $env:LOCALAPPDATA 'DeskWeave\agent-activity')
)

$ErrorActionPreference = 'Stop'
$handler = @{
    type = 'command'
    command = (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe')
    args = @('-NoProfile', '-NonInteractive', '-File',
        (Join-Path $PSScriptRoot 'Publish-DeskWeaveActivity.ps1'),
        '-WorkspaceAlias', $WorkspaceAlias, '-OutputDirectory', [IO.Path]::GetFullPath($OutputDirectory))
    timeout = 5
    onFailure = 'continue'
}
$hooks = [ordered]@{}
foreach ($event in @('SessionStart', 'UserPromptSubmit', 'PreToolUse', 'PermissionRequest',
    'PostToolUse', 'PostToolUseFailure', 'Elicitation', 'ElicitationResult', 'Stop', 'StopFailure', 'SessionEnd')) {
    $hooks[$event] = @(@{ hooks = @($handler) })
}
$hooks['Notification'] = @(@{
    matcher = 'permission_prompt|elicitation_dialog|elicitation_url_dialog|agent_needs_input'
    hooks = @($handler)
})
@{ hooks = $hooks } | ConvertTo-Json -Depth 8
