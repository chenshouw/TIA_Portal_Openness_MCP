---
name: add-mcp-host
description: Add a new AI client (MCP host) to the TIA Portal MCP one-click registrar, such as Trae CN, TraeCode, or another stdio MCP client. Use when the user asks to support/register a new AI host or IDE. Includes offline tests, build, doctor checks, docs, and commit. Do not use for MCP protocol or Openness business-logic changes.
---

# Add an AI host to the TIA MCP registrar

Add one new AI client ("host") that `TiaMcpServer.exe config` / `配置MCP.bat` can register
into, and that `doctor` can inspect. Scope is the registrar only — never MCP transport or
Openness logic. Work in the repo `TIA_Portal_Openness_MCP`.

## 0. Confirm scope first

- Confirm exactly which host and which edition/variant (e.g. Trae CN vs TraeCode are separate
  hosts). When variants exist, implement them as separate `Host` entries, one per task — do not
  bundle them silently.
- Confirm whether registration is stdio (`command/args/env`) or HTTP (`url/headers`). This playbook
  covers stdio, which all current hosts use.

## 1. Locate the real config path — never guess

For each target host determine its ACTUAL global config file:

1. Prefer the host's official docs.
2. On the machine, probe real paths. Windows examples:
   `Get-ChildItem "$env:APPDATA" -Directory | ? Name -match '<Host>'`, then recurse for
   `mcp.json` / `settings.json` / `*.toml`.
3. Open the real file and confirm the root key (`mcpServers` vs VS Code `servers` vs TOML
   `[mcp_servers.*]`) and any host-specific extra fields.
4. Record whether the file already exists and whether market-installed entries carry extra fields
   (e.g. Trae `fromGalleryId` / `disabled`) that must survive an upsert.

Do not infer a path from a directory name alone.

## 2. Code changes

File: `tools/tiaportal-mcp/src/TiaMcpServer/Cli/McpConfigInstaller.cs`

- `HostStyle` already covers `McpServers`, `VsCode`, `CodexToml`. Reuse one; add a new style only
  if the file format genuinely differs.
- Add the host in `KnownHosts()` via the four-arg constructor when it needs host-specific env:
  `new Host("Display Name", configPath, HostStyle.X, extraEnvDict)`. Use the three-arg constructor
  otherwise. Leave all existing host declarations untouched.
- To make a host the DEFAULT/preferred one-click target, set
  `McpConfigInstaller.DefaultHostName` to its display name. Then `CliCommands.Config()` already
  (a) sorts it first and tags it `[primary]`, (b) writes it even when not yet installed (Apply
  creates the dir), while other hosts still follow the "installed only" rule. Do not re-implement
  this per host.
- Host-specific env (e.g. cold-start timeouts) goes on `Host.ExtraEnv` only. `BuildServerEntry`
  merges it with the `--full` `TIA_MCP_PROFILE` env in one JsonObject — keys must coexist, never
  overwrite. Do not change existing method signatures; add tail optional parameters/overloads.
- Keep `Apply` upserting only the `tia-portal` key so other servers and their extra fields are
  preserved; `.bak` backup and atomic write already exist.
- Add XML comments on new/changed methods (params, returns, exceptions).

File: `tools/tiaportal-mcp/src/TiaMcpServer/Cli/CliCommands.cs`

- In `Config() --print`, print the host's snippet (pass `h.ExtraEnv`) and its real config path.
- In the apply loop pass `h.ExtraEnv` into `Apply`. Put host-specific warnings here (where both
  host and exe path are known), not in the generic entry builder — e.g. warn when command path
  contains spaces and the host forbids that.
- Add the selector token to `UsageText` (`--host <...>`). Verify `MatchesHost` normalization
  (removes spaces/hyphens, case-insensitive `Contains`) actually maps the token to the display name.
- `doctor` needs no loop change — it iterates `KnownHosts()`; just confirm `RegisteredCommand`
  understands the format.

Do NOT touch stdio/HTTP transport or any Siemens/Openness code.

## 3. Build

Build the engine project matching the machine's installed TIA version:

- V20: `dotnet build tools/tiaportal-mcp/src/TiaMcpServer/TiaMcpServer.V20.csproj -c Release
  -p:TiaPortalLocation="<real V20 install>"` (project default path may not exist).
- V21: `tools/tiaportal-mcp/src/TiaMcpServer/TiaMcpServer.csproj`.

A flood of `Siemens.Engineering` type-not-found errors means the matching TIA version is not
installed — an environment limitation, not a code error. Build the other/available target to
validate the change; state clearly which target could not be built locally.

Require 0 errors on a buildable target. Pre-existing nullable warnings unrelated to the change are
acceptable.

## 4. Offline tests — this project is NOT VSTest

The test project `tools/tiaportal-mcp/tests/TiaMcpServer.Tests` is a net8.0 console Exe driven by
`dotnet run` (NOT `dotnet test` — that runs zero cases and returns 0, a fake green). It links
zero-Siemens-dependency sources via `<Compile Include>`.

- Before linking a source, verify its dependency closure has no `Siemens.Engineering` references
  (grep its `using`s and types it calls). `McpConfigInstaller.cs` needs `EngineRouter.cs`, which is
  BCL-only — link both.
- Add a `<Compile Include>` for each needed source in the test csproj.
- Create `tests/.../<Host>HostConfigTests.cs` exposing `Run(Action<bool,string> check)` using temp
  directories only; never write real `%APPDATA%`. Cover:
  - host present in `KnownHosts()` with correct path suffix and style;
  - host env keys present; coexist with `TIA_MCP_PROFILE` when full; absent for other hosts;
  - `Apply` preserves a pre-existing market entry and its extra fields, is idempotent, writes `.bak`;
  - `RegisteredCommand` reads back the command;
  - a phased-scope sentinel: variants intentionally not implemented must be absent.
- Register the group in `tests/.../Program.cs Main`.
- Run: `dotnet run --project tools/tiaportal-mcp/tests/TiaMcpServer.Tests/TiaMcpServer.Tests.csproj
  -c Release` and require `0 failed`.

## 5. End-to-end on a machine with the host installed

Using the freshly built exe (pass matching `--tia-major-version`):

1. `config --host <token> --print` — verify path, args, host env.
2. Back up the real config yourself if needed, then `config --host <token>` — expect `[ok]`.
3. Open the real config: `tia-portal` present with correct absolute exe + host env; pre-existing
   servers and extra fields intact; `.bak` created.
4. `doctor` shows the host `tia-portal registered` (and ok when exe exists).
5. If feasible, restart the host and confirm the MCP server loads; validate cold-start timeout if
   the host kills slow-starting servers.
6. Regression: run config/doctor for an existing host (e.g. Cursor) to confirm shared changes.

## 6. Docs and scripts

Update, keeping hosts listed consistently:
`配置MCP.bat`, `配置MCP-v20.bat`, `README.zh-CN.md`, `README.md`,
`docs/CLI_quickstart.md`, `docs/mcp-ide-and-tool-visibility.md`.
Mention edition scope (e.g. "国内版") and note variants not yet supported.

## 7. Commit

Stage only the relevant code, test, and doc files (exclude unrelated local dirs). Include the plan
doc under `.trae/documents/` if the user wants it. Commit with a `feat(mcp):` message, then push to
the user's fork `origin` (the official repo is kept as `upstream`). Commit and push only when the
user asks.

## Reference paths

- Installer: `tools/tiaportal-mcp/src/TiaMcpServer/Cli/McpConfigInstaller.cs`
- CLI: `tools/tiaportal-mcp/src/TiaMcpServer/Cli/CliCommands.cs`
- Tests: `tools/tiaportal-mcp/tests/TiaMcpServer.Tests/`
- Worked example: the Trae CN change (commit "feat(mcp): 一键注册支持 Trae CN（国内版）")
