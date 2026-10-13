# Revit Add-in Manager MCP server (prototype)

Lets AI coding agents (Claude Code, Cursor, VS Code Copilot, …) develop Revit add-ins like a developer would:
hot-load and run the add-in, see exactly what it changed in the model, undo it, poke the Revit API with C# snippets,
drive the add-in's own dialogs, and check the code against every Revit version from 2019 to 2027, all without
restarting Revit.

```
AI agent ──stdio/MCP──► RevitAddinManager.Mcp.exe ──named pipe──► Add-in Manager inside Revit ──ExternalEvent──► Revit API
                              │                                                  └──UI dispatcher / Win32──► add-in windows
                              ├─ Roslyn: compiles execute_csharp snippets against the target Revit's own assemblies
                              └─ RevitAPI.dll metadata (installed Revit or Nice3point.Revit.Api packages): compat + lookup
```

The MCP server runs outside Revit, so its dependencies (Roslyn, the MCP SDK) never conflict with Revit or other add-ins.
Inside Revit, `AddInManager/Bridge` only uses the Revit API, WPF and Newtonsoft.Json, which Revit already ships.

## Tools

### Run and observe
| Tool | What it does |
|---|---|
| `run_command` | Load the latest .dll from disk and execute an `IExternalCommand` |
| `execute_csharp` | Compile a C# snippet with Roslyn against the target Revit's API and run it, with `uiapp`, `uidoc`, `doc` in scope; the returned value comes back as JSON |
| `get_run_result` | Final result of a run that returned early (`waitingForUI` / `running`) |
| `undo_last_run` | Revert the last run as one Revit undo step |
| `capture_view` | Image of a Revit view |

`run_command` and `execute_csharp` share the same run options and report:
- **Model changes**: added / modified / deleted elements, the transactions the code committed, and parameter before→after diffs.
- **`dryRun: true`**: run, record everything, then roll it all back, giving a full parameter diff and an untouched model.
- **`screenshot: after | beforeAfter`**: images of the active view attached to the result.
- **Revit warnings and errors** raised while the code ran, the exception with stack trace, and Debug/Trace output.
- Each run is wrapped in a `TransactionGroup`. A failed run is rolled back like Revit does for commands. A successful run becomes one undo step named `AI: <command>`.
- If the code opens a window, typically a modal dialog, the call returns at once with `status: waitingForUI`, a `runId` and a screenshot of that window. The agent then drives the window with the UI tools and calls `get_run_result`.

### Add-in UI
These calls work while an add-in is blocking Revit with a modal dialog.

| Tool | What it does |
|---|---|
| `reload_pane` | Hot-reload a dockable pane into a Dev Pane slot (no Revit restart) and return a screenshot; the pane is then `windowId` -1..-3 for the tools below |
| `list_windows` | Open add-in windows: WPF, WinForms, Win32 dialogs, TaskDialogs |
| `capture_window` | Image of a window |
| `get_window_tree` | Controls of a window with text, state and a `ref` |
| `ui_action` | `click`, `setText`, `select`, `check`, `uncheck`, `focus`, `close`, targeting a control by ref, `x:Name`/AutomationId or visible text; TaskDialog buttons by `ok`/`cancel`/`yes`/`no`/`commandLink1..4` |

### Revit API knowledge (no Revit needed)
| Tool | What it does |
|---|---|
| `check_api_compat` | Every Revit API type/member a compiled .dll uses that is removed, changed or obsolete in each Revit version 2019–2027, with the obsolete message (usually naming the replacement) and where it is used (`Command.cs:21` when a portable PDB is present) |
| `revit_api_lookup` | Exact overloads, signatures, obsolete status and XML docs of a type or member; `compareVersions` shows availability across versions; a removed member reports the last version that had it and what to use instead |

Versions that are not installed are read from the [Nice3point.Revit.Api](https://www.nuget.org/packages/Nice3point.Revit.Api.RevitAPI) reference packages: the local NuGet cache first, otherwise downloaded once (about 5 MB per version) to `%LOCALAPPDATA%\RevitAddinManager\api-cache`.

### Inspect
| Tool | What it does |
|---|---|
| `list_revit_instances` | Running Revit sessions with Add-in Manager loaded (pid, version) |
| `revit_status` | Revit version, active document and view |
| `list_commands` | `IExternalCommand` classes in a .dll |
| `get_trace_log` | Debug/Trace output, incremental via `sinceSeq` |
| `get_selection` | Elements currently selected in Revit |
| `get_element_params` | All parameters of elements (BuiltInParameter, value, unit, read-only, shared GUID) |

Every Revit tool takes an optional `pid` to pick a Revit instance; by default the most recently started one is used.

## Setup

1. Build and load Add-in Manager in Revit as usual (e.g. `Debug R25`). The bridge starts automatically when Revit starts.
2. Build the server:
   ```
   dotnet build AddInManager.Mcp -c Release
   ```
3. Register it with your agent, for example Claude Code:
   ```
   claude mcp add revit -- "<repo>\AddInManager.Mcp\bin\Release\net8.0\RevitAddinManager.Mcp.exe"
   ```
   or in `.mcp.json`:
   ```json
   {
     "mcpServers": {
       "revit": { "command": "C:\\path\\to\\RevitAddinManager.Mcp.exe" }
     }
   }
   ```

Typical loop for an agent: `revit_api_lookup` → write code → `dotnet build` → `run_command` with `dryRun` →
read changes / exception / trace / screenshot → fix → run for real → `undo_last_run` if needed →
`check_api_compat` before release.

## Security

- Each Revit writes `%LOCALAPPDATA%\RevitAddinManager\bridge\<pid>.json` with a random token; requests without the token are rejected.
- The named pipe only accepts connections from the current Windows user.
- `run_command` and `execute_csharp` execute arbitrary code and can modify the open model. Use `dryRun`, work on a test model, or rely on `undo_last_run`.
- To disable the bridge, set the environment variable `REVIT_ADDIN_MANAGER_BRIDGE=0` before starting Revit.

## Known limitations

- Revit only processes run requests when it is idle. While a run is waiting on a dialog, other Revit calls are refused with a clear message, but the UI tools keep working.
- `undo_last_run` refuses when the model changed after the run (user edits, other commands), so it never reverts the wrong step.
- Before/after parameter values in a committed run are only known for watched or selected elements; use `dryRun` for a full diff.
- `Debug.WriteLine` is compiled out of Release builds of your add-in; use `Trace.WriteLine` or build Debug. `execute_csharp` snippets always define `DEBUG`.
- Source line numbers in `check_api_compat` need a portable PDB (`<DebugType>portable</DebugType>`); Windows PDBs only give method names.
- Revit 2019–2024 (.NET Framework) cannot unload assemblies, so each run loads a new copy into the AppDomain, the same as the Add-in Manager UI does.
- TaskDialog text is not exposed as controls; read it with `capture_window`.
