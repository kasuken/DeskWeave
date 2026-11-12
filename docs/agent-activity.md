# Agent activity

The opt-in **Agent activity** panel answers "which agents need me?" using explicit lifecycle metadata, not process names, CPU activity, terminal scraping or conversation databases. Waiting-for-approval/input rows come first, followed by failures/blocked tasks, running tasks and recently finished turns. At most five rows are shown; counts include overflow.

## Set up Claude Code on Windows

Claude Code is the first adapter. Copilot, Codex and other tools are **not** implicitly compatible. Use a current Claude Code version supporting the documented hooks and exec-form hook handlers; the supplied settings use `onFailure: "continue"` (Claude Code 2.1.295 or later). Windows PowerShell 5.1 is used by the adapter. Respect your organization's script-execution and hook policies; DeskWeave does not change them.

1. Run the new DeskWeave build. In its tray menu, select **Agent activity > Enable Claude Code hooks**. The integration starts disconnected until new hook metadata arrives.
2. From the repository root or extracted portable folder, generate a separate, local Claude settings file. Choose a non-sensitive workspace alias:

   ```powershell
   $settings = Join-Path $env:LOCALAPPDATA 'DeskWeave\claude-hooks-example.json'
   $json = .\integrations\claude-code\Get-HookSettings.ps1 -WorkspaceAlias Example
   [IO.File]::WriteAllText($settings, ($json -join [Environment]::NewLine), [Text.UTF8Encoding]::new($false))
   ```

3. In the workspace you want to monitor, explicitly start Claude with that file:

   ```powershell
   claude --settings $settings
   ```

   Alternatively, review and merge the generated `hooks` entries into your own Claude settings. Do not overwrite existing hooks. Managed policies can disable custom hooks. The generated handler paths are absolute; regenerate settings if you move DeskWeave.
4. Submit a prompt. A new task appears within the shared two-second polling interval. Two terminals in the same workspace have separate session IDs and do not overwrite each other.

DeskWeave never installs hooks automatically, starts agents, grants permissions or sends input. Disabling the integration stops ingestion and clears its rows, but does not edit Claude settings or stop a running tool. Remove the hook settings separately to stop producing metadata.

### Optional workspace navigation

Choose **Edit integration settings** in the tray. The file is `%LOCALAPPDATA%\DeskWeave\agent-activity-settings.json`. Its default is disabled with no folder mappings:

```json
{
  "EnabledIntegrations": [],
  "Workspaces": {}
}
```

Add `"claude-code"` to `EnabledIntegrations` to enable it, or use the tray toggle. Add explicit alias-to-folder mappings if you want **Open workspace folder**:

```json
{
  "EnabledIntegrations": ["claude-code"],
  "Workspaces": {
    "Example": "C:\\Projects\\Example"
  }
}
```

Select **Reload integration settings** after editing. Up to 16 integrations and 16 folder mappings are supported. Only existing, fully qualified local folders are supported, not commands, URLs, UNC/device paths, redirected folders or folder names containing commas. Folder navigation uses Explorer only after a tray click; paths are never taken from activity snapshots. A folder mapping is optional: the adapter's configured alias can be displayed without recording a workspace path.

## Capability matrix

The mapping follows Claude's [official hooks reference](https://code.claude.com/docs/en/hooks) and [CLI reference](https://code.claude.com/docs/en/cli-reference), reviewed on 2026-11-12.

| Signal | DeskWeave meaning | Limitations |
| --- | --- | --- |
| `SessionStart` | Source connected, no working-task claim | Starting a session alone is not work. |
| `UserPromptSubmit` | New coding turn running | A new prompt ID starts a new task; repeated IDs do not reset its start time. |
| `PreToolUse` / `PostToolUse` | Current turn running | `AskUserQuestion` is shown as needing input before its tool call. |
| `PermissionRequest` | Needs approval | A hook signal, not proof that a human dialog remains open: another hook/host can decide first. |
| `Notification: permission_prompt` | Needs approval | Delayed notification also covers supported sandbox network prompts. |
| `Elicitation` / supported input notifications | Needs input | No question text or form fields are captured. |
| `ElicitationResult` / subsequent tool completion | Running again | No reply or tool result is captured. |
| `PostToolUseFailure` | Turn still active, not failed | A tool failure can be recoverable; it does not establish a globally blocked/failed task. |
| `Stop` | Turn finished | Not proof of project/task success. Stops reporting background tasks or scheduled wakeups do not mark a turn finished. |
| `StopFailure` | Turn failed | API-level failures only; error text is ignored. |
| `SessionEnd` | Disconnected | An unfinished turn becomes unknown, not completed. |
| No event for two minutes | Unknown/disconnected | There is no heartbeat or process-liveness heuristic. Long model/tool operations and long waits can therefore become unknown too. |
| Generic blocked task | Supported by the contract, **not reported by this adapter** | Claude's selected hooks do not reliably establish that semantic state. |
| Subagents, remote sessions, other tools | Unsupported by this adapter | Subagent callbacks are ignored, not conflated with the parent turn. |

`Stop` is a response boundary, not a session-end or project-success event. Other Stop hooks may continue a response; a terminal task is deliberately not reopened by later tool callbacks. A new submitted prompt can start a new task. Unreported interrupts/crashes expire to unknown rather than being guessed as failures or completion.

## Local snapshot contract (v1)

Transport is a per-user local directory, `%LOCALAPPDATA%\DeskWeave\agent-activity`, with one UTF-8 JSON snapshot per session. There is no network listener or elevated service. A file must be named `<adapterId>-<sessionId>.json`; the Claude adapter hashes the tool session identifier with SHA-256, producing a 64-character lowercase hexadecimal ID.

```json
{
  "schemaVersion": 1,
  "adapterId": "claude-code",
  "sessionId": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
  "sequence": 2,
  "observedAt": "2026-11-12T10:00:00Z",
  "connected": true,
  "task": {
    "id": "turn-1",
    "label": "Coding turn",
    "workspaceAlias": "Example",
    "state": "needsApproval",
    "startedAt": "2026-11-12T09:59:00Z",
    "updatedAt": "2026-11-12T10:00:00Z"
  }
}
```

`task` may be `null` for a connected idle source. Task states are `running`, `needsApproval`, `needsInput`, `blocked`, `completed` and `failed`; freshness is separate. A task's start time is immutable. New turns need a new task ID. A session's sequence increases with every update, including connection changes; duplicate/older sequences cannot overwrite newer state or reopen a terminal task. Producers must serialize writes for a session, write a temporary file in the same directory, close it and atomically replace the final filename. Temporary files are ignored.

Snapshots are limited to **16 KiB**, directory reads to **128 session files**, and JSON is schema-validated. Event timestamps must be UTC and satisfy `startedAt <= updatedAt <= observedAt <= now`. Unknown fields/versions, invalid timestamps/states, oversized files and redirected inputs are rejected with source warnings rather than exposing their contents. More than 128 snapshot files rejects the batch. An unsupported configured adapter is explicitly unsupported, not connected.

Reads run in one shared loop with a two-second polling interval and a 15-second cooperative scan deadline. Under load, scanning resumes after the last attempted file instead of starving later sessions. Cancellation and the deadline cannot preempt synchronous filesystem checks; a slow scan can delay the next refresh.

Live claims expire after **two minutes** without a new observation. Application startup or re-enabling requires a newer observation before claiming a cached task is live. Re-reading a file is not a heartbeat. Completed and failed turns expire after **10 minutes**; disconnected history expires after **30 minutes**. At most 128 rows are retained; older terminal rows may be evicted sooner at that limit. Terminal task IDs remain protected while their session is retained: after 4,096 terminal IDs per session, new tasks fail closed with a warning until that session resets. Disabling ingestion or 30 minutes of inactivity resets session state.

The supplied writer serializes callbacks with a two-second local file-lock deadline, limits hook input to 256 Ki characters, caps stored session snapshots at 128 and prunes snapshots older than 30 minutes on subsequent writes. Atomic replacement prevents half-written snapshots being mistaken for completion. If a callback/write fails, it emits a generic error, never a permission decision. The generated hooks have a five-second timeout and continue the underlying action on failure.

## Privacy

The hook subprocess necessarily receives Claude's callback JSON on stdin. It projects only allowlisted identifiers/event kinds into the snapshot and never reads transcripts. Prompts, answers, source content, tool arguments/results, error details, credentials, full command lines and transcript paths are neither forwarded to DeskWeave nor stored/logged by the adapter. Task labels are fixed to "Coding turn"; aliases are explicitly chosen by the user. Local metadata and optional folder mappings remain on the machine. Do not use secrets in aliases.

## Reproducible Windows smoke test

### No-provider fixture

Use the adapter itself with safe synthetic hook payloads. This does not start Claude or make provider requests. Enable the integration before generating events:

```powershell
$output = Join-Path $env:LOCALAPPDATA 'DeskWeave\agent-activity'
.\integrations\claude-code\Invoke-ActivityFixture.ps1 -OutputDirectory $output -Event UserPromptSubmit -Session a
.\integrations\claude-code\Invoke-ActivityFixture.ps1 -OutputDirectory $output -Event UserPromptSubmit -Session b
.\integrations\claude-code\Invoke-ActivityFixture.ps1 -OutputDirectory $output -Event PermissionRequest -Session a
```

Expect two independent rows under the same `Example` alias; session A sorts first and the waiting count is one. Then:

```powershell
.\integrations\claude-code\Invoke-ActivityFixture.ps1 -OutputDirectory $output -Event PostToolUse -Session a
.\integrations\claude-code\Invoke-ActivityFixture.ps1 -OutputDirectory $output -Event Stop -Session a
.\integrations\claude-code\Invoke-ActivityFixture.ps1 -OutputDirectory $output -Event StopFailure -Session b
.\integrations\claude-code\Invoke-ActivityFixture.ps1 -OutputDirectory $output -Event SessionEnd -Session b
```

Expect a finished turn and a failed turn, with no waiting count. Generate a new `UserPromptSubmit -Turn next` for a session, stop producing events and wait over two minutes: that live row must become unknown. Restart DeskWeave before another event: it must not resurrect the cached row as running. Disable the integration: its rows disappear.

### Real Claude Code

1. Check `claude --version`, then follow setup above in a disposable workspace. Keep existing permission prompts enabled; do not bypass approvals.
2. Open two Claude terminals with the generated settings and submit small tasks. Confirm two independent rows.
3. Request an operation that Claude asks you to approve, or a supported question/MCP input. Confirm the corresponding attention row, then answer in Claude and confirm activity resumes.
4. Let the response finish: expect **Turn finished**, not "project succeeded". An actual API failure should produce **Failed** if `StopFailure` fires; do not modify credentials to fabricate one.
5. Exit a terminal normally: its source disconnects when no other fresh session remains. If it is forcibly interrupted or no hook fires, wait two minutes and confirm unknown/disconnected.

Automated tests invoke the actual PowerShell adapter with fixture callbacks. A real Claude session still requires the installed tool, your sign-in and explicit opt-in; fixture tests are not a claim that a real authenticated session was exercised.
