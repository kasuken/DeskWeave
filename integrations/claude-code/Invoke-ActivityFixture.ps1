param(
    [Parameter(Mandatory = $true)]
    [string] $OutputDirectory,
    [ValidateSet('SessionStart', 'UserPromptSubmit', 'PreToolUse', 'PermissionRequest', 'Elicitation',
        'PostToolUse', 'Stop', 'StopFailure', 'SessionEnd')]
    [string] $Event = 'UserPromptSubmit',
    [string] $Session = 'example-a',
    [string] $Turn = 'turn-a',
    [string] $WorkspaceAlias = 'Example'
)

$payload = @{
    hook_event_name = $Event
    session_id = "deskweave-fixture-$Session"
    prompt_id = "deskweave-fixture-$Session-$Turn"
    tool_name = 'Bash'
} | ConvertTo-Json -Compress
$payload | & powershell.exe -NoProfile -File (Join-Path $PSScriptRoot 'Publish-DeskWeaveActivity.ps1') `
    -WorkspaceAlias $WorkspaceAlias -OutputDirectory $OutputDirectory
exit $LASTEXITCODE
