param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9 _.-]{0,47}$')]
    [string] $WorkspaceAlias,
    [string] $OutputDirectory = (Join-Path $env:LOCALAPPDATA 'DeskWeave\agent-activity')
)

$ErrorActionPreference = 'Stop'
$lock = $null
$temporary = $null

function Get-SafeId([string] $Value) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($Value)))).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
    }
}

try {
    $eventTime = [DateTimeOffset]::UtcNow
    $buffer = New-Object char[] 4096
    $inputText = New-Object Text.StringBuilder
    while (($count = [Console]::In.Read($buffer, 0, $buffer.Length)) -gt 0) {
        if ($inputText.Length + $count -gt 262144) {
            throw 'Hook input exceeds the limit.'
        }
        [void] $inputText.Append($buffer, 0, $count)
    }
    $hook = $inputText.ToString() | ConvertFrom-Json
    $inputText.Clear() | Out-Null
    if (-not ($hook.session_id -is [string]) -or $hook.session_id.Length -gt 256 -or $hook.session_id.Length -eq 0) {
        throw 'Invalid session identifier.'
    }
    if ($hook.agent_id) {
        exit 0
    }

    $event = [string] $hook.hook_event_name
    $state = $null
    switch ($event) {
        'SessionStart' {}
        'UserPromptSubmit' { $state = 'running' }
        'PreToolUse' {
            if ($hook.tool_name -eq 'AskUserQuestion') { $state = 'needsInput' }
            else { $state = 'running' }
        }
        'PermissionRequest' { $state = 'needsApproval' }
        'PostToolUse' { $state = 'running' }
        'PostToolUseFailure' { $state = 'running' }
        'Elicitation' { $state = 'needsInput' }
        'ElicitationResult' { $state = 'running' }
        'Notification' {
            switch ([string] $hook.notification_type) {
                'permission_prompt' { $state = 'needsApproval' }
                'elicitation_dialog' { $state = 'needsInput' }
                'elicitation_url_dialog' { $state = 'needsInput' }
                'agent_needs_input' { $state = 'needsInput' }
                default { exit 0 }
            }
        }
        'Stop' {
            if (@($hook.background_tasks).Where({ $null -ne $_ }).Count -gt 0 -or
                @($hook.session_crons).Where({ $null -ne $_ }).Count -gt 0) {
                exit 0
            }
            $state = 'completed'
        }
        'StopFailure' { $state = 'failed' }
        'SessionEnd' {}
        default { exit 0 }
    }

    $directory = [IO.Path]::GetFullPath($OutputDirectory)
    if ($directory.StartsWith('\\') -or -not [IO.Path]::IsPathRooted($directory)) {
        throw 'A local output directory is required.'
    }
    [void] [IO.Directory]::CreateDirectory($directory)
    if (([IO.File]::GetAttributes($directory) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'Redirected output directories are not supported.'
    }
    $sessionId = Get-SafeId $hook.session_id
    $file = Join-Path $directory "claude-code-$sessionId.json"
    $lockFile = Join-Path $directory 'writer.lock'
    if ([IO.File]::Exists($lockFile) -and
        ([IO.File]::GetAttributes($lockFile) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'Redirected lock files are not supported.'
    }
    $wait = [Diagnostics.Stopwatch]::StartNew()
    while ($null -eq $lock) {
        try {
            $lock = [IO.File]::Open($lockFile, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        }
        catch [IO.IOException] {
            if ($wait.Elapsed.TotalSeconds -ge 2) { throw }
            Start-Sleep -Milliseconds 25
        }
    }

    $files = @([IO.Directory]::EnumerateFiles($directory, 'claude-code-*.json') |
        Where-Object { [IO.Path]::GetFileName($_) -cmatch '^claude-code-[0-9a-f]{64}\.json$' } |
        Select-Object -First 129)
    foreach ($candidate in $files) {
        $info = New-Object IO.FileInfo $candidate
        if (($info.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { continue }
        if ($info.LastWriteTimeUtc -lt $eventTime.UtcDateTime.AddMinutes(-30)) {
            [IO.File]::Delete($candidate)
        }
    }
    foreach ($stale in @([IO.Directory]::EnumerateFiles($directory, 'claude-code-*.tmp') |
        Where-Object { [IO.Path]::GetFileName($_) -cmatch '^claude-code-[0-9a-f]{32}\.tmp$' } |
        Select-Object -First 129)) {
        $info = New-Object IO.FileInfo $stale
        if (($info.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0 -and
            $info.LastWriteTimeUtc -lt $eventTime.UtcDateTime.AddMinutes(-30)) {
            [IO.File]::Delete($stale)
        }
    }
    if (-not [IO.File]::Exists($file) -and
        @([IO.Directory]::EnumerateFiles($directory, 'claude-code-*.json') |
            Where-Object { [IO.Path]::GetFileName($_) -cmatch '^claude-code-[0-9a-f]{64}\.json$' } |
            Select-Object -First 128).Count -ge 128) {
        throw 'Activity session limit reached.'
    }

    $previous = $null
    if ([IO.File]::Exists($file)) {
        $info = New-Object IO.FileInfo $file
        if ($info.Length -gt 16384 -or ($info.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'Invalid previous snapshot.'
        }
        $previous = [IO.File]::ReadAllText($file) | ConvertFrom-Json
        if ($previous.schemaVersion -ne 1 -or $previous.adapterId -ne 'claude-code' -or
            $previous.sessionId -ne $sessionId -or $previous.sequence -lt 1 -or
            $previous.sequence -ge [long]::MaxValue) {
            throw 'Invalid previous snapshot.'
        }
        if ([DateTimeOffset]::Parse($previous.observedAt) -ge $eventTime) {
            exit 0
        }
    }

    $task = $null
    if ($null -ne $previous.task) {
        $task = [ordered]@{
            id = $previous.task.id
            label = 'Coding turn'
            workspaceAlias = $WorkspaceAlias
            state = $previous.task.state
            startedAt = $previous.task.startedAt
            updatedAt = $previous.task.updatedAt
        }
    }
    $terminal = $null -ne $task -and $task.state -in @('completed', 'failed')
    if ($event -eq 'SessionStart') {
        $task = $null
    }
    elseif ($null -ne $state) {
        $newTurn = $event -eq 'UserPromptSubmit'
        $promptId = if ($hook.prompt_id -is [string] -and $hook.prompt_id.Length -in 1..256) {
            Get-SafeId $hook.prompt_id
        } else { $null }
        if ($newTurn -and $null -ne $task -and $null -ne $promptId -and $task.id -eq $promptId) {
            exit 0
        }
        if ($terminal -and -not $newTurn) {
            exit 0
        }
        if ($newTurn -or $null -eq $task -or $terminal) {
            if ($state -in @('completed', 'failed')) { exit 0 }
            $task = [ordered]@{
                id = if ($newTurn -and $null -ne $promptId) { $promptId } else { [Guid]::NewGuid().ToString('N') }
                label = 'Coding turn'
                workspaceAlias = $WorkspaceAlias
                state = $state
                startedAt = $eventTime.ToString('O')
                updatedAt = $eventTime.ToString('O')
            }
        }
        else {
            $task.state = $state
            $task.updatedAt = $eventTime.ToString('O')
        }
    }

    $snapshot = [ordered]@{
        schemaVersion = 1
        adapterId = 'claude-code'
        sessionId = $sessionId
        sequence = if ($null -eq $previous) { 1 } else { [long] $previous.sequence + 1 }
        observedAt = $eventTime.ToString('O')
        connected = $event -ne 'SessionEnd'
        task = $task
    }
    $json = $snapshot | ConvertTo-Json -Depth 5 -Compress
    $bytes = [Text.Encoding]::UTF8.GetBytes($json)
    if ($bytes.Length -gt 16384) { throw 'Snapshot exceeds the limit.' }
    $temporary = Join-Path $directory ('claude-code-' + [Guid]::NewGuid().ToString('N') + '.tmp')
    [IO.File]::WriteAllBytes($temporary, $bytes)
    if ([IO.File]::Exists($file)) {
        [IO.File]::Replace($temporary, $file, [System.Management.Automation.Language.NullString]::Value)
    }
    else {
        [IO.File]::Move($temporary, $file)
    }
}
catch {
    [Console]::Error.WriteLine('DeskWeave activity metadata could not be updated. Check the adapter setup and local directory permissions.')
    exit 1
}
finally {
    if ($null -ne $temporary -and [IO.File]::Exists($temporary)) {
        [IO.File]::Delete($temporary)
    }
    if ($null -ne $lock) { $lock.Dispose() }
}
