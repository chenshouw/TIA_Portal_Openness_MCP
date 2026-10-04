# 增加 Trae 客户端支持 — 可行性评估与设计方案

## 1. 需求理解

为 TIA Portal Openness MCP 升级一键注册能力，使 `TiaMcpServer.exe config` / `配置MCP.bat`
能自动注册进 Trae 客户端，并在 `doctor` 体检中识别其注册状态。

**分期策略（用户明确要求）**：国内版与国际版作为**两个独立宿主分别实现**，
**本期只实现国内版（Trae CN）**；国际版（TraeCode）仅预留命名与扩展位，不写代码、不写配置。

当前已支持 Claude Desktop / Claude Code / Cursor / VS Code / Codex / Gemini CLI /
Windsurf / Cline 共 8 个宿主。

## 2. 仓库调研结论

### 2.1 现有宿主注册架构（可直接复用）

- 所有宿主在 [McpConfigInstaller.cs](file:///d:/source/repos/TIA_Portal_Openness_MCP/tools/tiaportal-mcp/src/TiaMcpServer/Cli/McpConfigInstaller.cs)
  的 `KnownHosts()` 中以 `Host { Name, ConfigPath, HostStyle }` 声明，新增宿主只需加一行。
- 配置格式分三类（`HostStyle` 枚举）：
  - `McpServers`：根键 `mcpServers`，条目 `{command, args, env}` —— **Trae CN 正是此格式**，零新增解析逻辑。
  - `VsCode`：根键 `servers`，条目带 `type:"stdio"`。
  - `CodexToml`：TOML，已有独立的行级合并实现。
- 通用能力对所有宿主自动生效：
  - `Apply()` 原子写入、自动备份 `.bak`、保留其它 server（包括 Trae 市场安装项特有的
    `fromGalleryId` / `disabled` 等额外字段——这些字段挂在**别的** server 条目上，
    合并逻辑只整体替换 `tia-portal` 条目，天然不影响它们）；
  - `RegisteredCommand()` 供 `doctor` 读取已注册命令并校验 exe 是否存在；
  - `Snippet()` 供 `config --print` 输出手动粘贴片段。
- CLI 层 [CliCommands.cs](file:///d:/source/repos/TIA_Portal_Openness_MCP/tools/tiaportal-mcp/src/TiaMcpServer/Cli/CliCommands.cs#L177-L229)
  的 `Config()` 自动遍历 `KnownHosts()`，按"配置文件或其父目录存在即视为已安装"过滤；
  `MatchesHost()` 做去空格/连字符的包含匹配，`--host trae` / `traecn` 可命中。

### 2.2 本机实测的国内版配置事实（2026-10-04，开发机）

本机 `%APPDATA%` 下并存三个相关目录，已逐一核实：

| 目录 | 性质 | mcp.json |
|------|------|----------|
| `%APPDATA%\Trae CN\User\mcp.json` | **国内版（本期目标）** | **存在，标准 `mcpServers` 格式** |
| `%APPDATA%\Trae\User\` | 国际版 Trae/TraeCode | 有 User 目录但当前无 mcp.json（未配置过 MCP） |
| `%APPDATA%\TRAE SOLO\` | TRAE SOLO 客户端 | 未发现 mcp.json（本期不涉及） |

国内版现存 mcp.json 实例（市场安装的 server，含 `fromGalleryId`/`disabled` 扩展字段），
证实：路径确定为 `%APPDATA%\Trae CN\User\mcp.json`，JSON 结构与 Claude/Cursor 完全兼容。

### 2.3 Trae 配置规范（官方文档 docs.trae.ai 核对）

- 全局配置是 AI Agent 实际读取的配置；项目级 `.trae/mcp.json` 需在 设置 → MCP
  显式开启"启用项目级 MCP"，属实验特性，**不作为注册目标**。
- stdio 超时通过 env 配置：`START_MCP_TIMEOUT_MS`、`RUN_MCP_TIMEOUT_MS`。
  TIA headless 冷启动 10–28s（[CLI_quickstart.md](file:///d:/source/repos/TIA_Portal_Openness_MCP/docs/CLI_quickstart.md)），
  默认启动超时偏短，有冷启动被杀风险（Codex 已有同类先例）。
- 注意：官方文档称 command 中"不能包含空格"，交付包若位于含空格目录需给出警告。

## 3. 可行性结论

**国内版完全可行，改动小、风险低：**

1. 服务端是标准 MCP over stdio，国内版配置格式与 Claude/Cursor 相同，**协议/传输零改动**。
2. 宿主注册是数据驱动设计，国内版主要是 `KnownHosts()` 增加一个条目。
3. 路径已在开发机实测确认，非推测。
4. 国际版因目录虽存在但当前无 mcp.json、且与国内版产品分支不同，按用户要求**本期不实现**，
   仅在代码注释与文档中记录其候选路径（`%APPDATA%\Trae\User\mcp.json`）供下期复用。

## 4. 设计方案（本期：仅国内版 Trae CN）

### 4.1 变更文件与内容

> 评审修正（最小回归面原则）：不改动 `Apply/Snippet/RegisteredCommand` 的现有签名与全部
> 调用点。宿主专属 env 通过给 `Host` 加字段 + `BuildServerEntry` 增加**可选参数**实现，
> 现有 8 个宿主的声明与调用保持原样。

1. [McpConfigInstaller.cs](file:///d:/source/repos/TIA_Portal_Openness_MCP/tools/tiaportal-mcp/src/TiaMcpServer/Cli/McpConfigInstaller.cs)
   - `Host` 类增加**可选**成员 `IReadOnlyDictionary<string,string>? ExtraEnv`（默认 null）；
     **保留现有三参构造函数**（ExtraEnv=null），另加一个四参构造函数，现有 8 个宿主声明零改动。
   - `KnownHosts()` 新增**一个**条目（用四参构造）：
     - `Name = "Trae CN"`、`ConfigPath = %APPDATA%\Trae CN\User\mcp.json`、
       `Style = McpServers`、`ExtraEnv = { START_MCP_TIMEOUT_MS: "120000", RUN_MCP_TIMEOUT_MS: "600000" }`。
     - 注释中标注：国际版 TraeCode 候选路径 `%APPDATA%\Trae\User\mcp.json`，下期实现，勿并入本条目。
   - env 注入采用**最小侵入**方式：`BuildServerEntry(string exe, int ver, HostStyle style, bool full=false)`
     保留不动，新增内部重载
     `BuildServerEntry(string exe, int ver, HostStyle style, bool full, IReadOnlyDictionary<string,string>? extraEnv)`，
     原方法委托新方法传 null。真正需要宿主 env 的只有 `Apply`/`Snippet`——它们内部已持有
     `HostStyle`，但拿不到 `Host`。因此**仅给 `Apply` 和 `Snippet` 各增加一个可选参数
     `IReadOnlyDictionary<string,string>? extraEnv = null`**（尾部可选，不破坏现有调用），
     由 `Config()` 传 `h.ExtraEnv`。`ExtraEnv` 与 `--full` 的 `TIA_MCP_PROFILE` 在同一
     JsonObject env 上**合并**（先放 profile，再叠加 extraEnv，键不冲突）。
   - 空格警告放在 [CliCommands.cs](file:///d:/source/repos/TIA_Portal_Openness_MCP/tools/tiaportal-mcp/src/TiaMcpServer/Cli/CliCommands.cs)
     的 `Config()`（该处同时拿得到 host 与 exe 路径），仅当 host 是 Trae CN 且 exe 全路径含空格时
     打印警告，不阻断；不放进通用 `BuildServerEntry`，以免影响其它宿主。
   - 全部新增/改动方法补齐 XML 注释（参数、返回值、异常）。
2. [CliCommands.cs](file:///d:/source/repos/TIA_Portal_Openness_MCP/tools/tiaportal-mcp/src/TiaMcpServer/Cli/CliCommands.cs)
   - `Config()` 的 `--print` 分组说明加入 Trae CN 及其全局配置路径；
   - `UsageText` 中 `--host` 可选值追加 `trae-cn`（`MatchesHost` 规范化后可匹配）；
   - doctor 宿主循环自动覆盖，无需改逻辑。
3. 脚本与文档
   - [配置MCP.bat](file:///d:/source/repos/TIA_Portal_Openness_MCP/配置MCP.bat) 与
     `配置MCP-v20.bat`：注释中客户端列表加入 Trae CN（国内版）；
   - [README.zh-CN.md](file:///d:/source/repos/TIA_Portal_Openness_MCP/README.zh-CN.md)
     第 181 行 `--host` 可选值补 `trae-cn`；README.md 同步；
   - [docs/CLI_quickstart.md](file:///d:/source/repos/TIA_Portal_Openness_MCP/docs/CLI_quickstart.md)
     与 [docs/mcp-ide-and-tool-visibility.md](file:///d:/source/repos/TIA_Portal_Openness_MCP/docs/mcp-ide-and-tool-visibility.md)
     的宿主列表补充"Trae（国内版）"，并注明国际版待支持。

### 4.2 实施步骤（依赖顺序）

1. 改 `McpConfigInstaller`：Host 的 ExtraEnv + Trae CN 条目 + env 合并 + 空格警告，补 XML 注释。
2. 改 `CliCommands`：`--print` 文案、`--host` 用法。
3. 编译两个工程（V21 `TiaMcpServer.csproj`、V20 `TiaMcpServer.V20.csproj`），0 error。
4. 新增离线测试：链接源文件到测试工程、新增 `TraeHostConfigTests.cs` 并接入 `Program.cs`，
   用 `dotnet run`（**不是 dotnet test**）跑全量离线自检，确保新旧用例全绿。
5. 更新 .bat / README / docs。
6. 手动端到端验证（第 5 节）。
7. 提交版本控制（仅在用户确认提交时执行）。

### 4.3 单元测试要点（须遵循本测试工程的特殊机制）

> 评审修正：测试工程 [TiaMcpServer.Tests.csproj](file:///d:/source/repos/TIA_Portal_Openness_MCP/tools/tiaportal-mcp/tests/TiaMcpServer.Tests/TiaMcpServer.Tests.csproj)
> **不是 VSTest**，而是 net8.0 控制台 Exe，通过 `<Compile Include>` 仅链接**零 Siemens 依赖**
> 的源文件，由 `dotnet run` 驱动（csproj 明确警告：用 `dotnet test` 会"一个用例都不跑然后退 0"
> 的假绿灯）。因此**禁止写 `dotnet test`**，并需先确认被链接文件的依赖闭包可在 net8.0 编译。

依赖核查结论：`McpConfigInstaller.cs` 仅 using BCL + `System.Text.Json.Nodes`（net8.0 自带），
但它的 `ExeForVersion()` 引用了 `Siemens.EngineRouter`。经核查
[EngineRouter.cs](file:///d:/source/repos/TIA_Portal_Openness_MCP/tools/tiaportal-mcp/src/TiaMcpServer/Siemens/EngineRouter.cs)
 只 using BCL（System/IO/Diagnostics/Text/Regex），**无 Siemens.Engineering 依赖**，
故可把 `McpConfigInstaller.cs` 与 `EngineRouter.cs` 一并 `<Compile Include>` 进测试工程
（实现时若 EngineRouter 再牵出其它非零依赖文件，则改为：把待测得 JSON 合并/entry 构建逻辑
保持在 McpConfigInstaller 内不引入新依赖，并用同样方式链接；以实际编译结果为准，不新造测试框架）。

测试组织：新增 `TraeHostConfigTests.cs`，提供 `Run(Action<bool,string> check)`，
并在 [Program.cs](file:///d:/source/repos/TIA_Portal_Openness_MCP/tools/tiaportal-mcp/tests/TiaMcpServer.Tests/Program.cs)
 的 `Main` 中加一行调用，沿用现有 pass/fail 汇总与退出码。全部用临时目录/临时文件，不碰真实 `%APPDATA%`：

- entry 构建：Trae CN 的 env 含 `START_MCP_TIMEOUT_MS=120000` /
  `RUN_MCP_TIMEOUT_MS=600000`；叠加 `--full` 时 `TIA_MCP_PROFILE=full` 与二者共存；
  extraEnv=null（其它宿主）时不产生这两个键。
- `Apply`：在临时目录构造 Trae CN mcp.json（含一个带 `fromGalleryId`/`disabled` 的市场条目），
  验证写入 tia-portal 后该市场条目及其扩展字段**原样保留**、重复执行幂等、生成 `.bak`。
- `RegisteredCommand`：写入后能读回 command；command 指向不存在文件时语义上可判失效。
- `KnownHosts`：包含 "Trae CN"，其 Windows 路径以 `Trae CN\User\mcp.json` 结尾；
  且**不包含**国际版条目（分期哨兵）。

## 5. 验证计划

- 编译：两个 csproj 0 error；测试工程用 `dotnet run` 跑离线自检，新旧用例全绿。
- 手动端到端（本机国内版 Trae CN 已安装，具备验证条件）：
  1. `TiaMcpServer.exe config --host trae-cn --print` 片段格式/路径正确；
  2. `tia config --host trae-cn` 后检查 `%APPDATA%\Trae CN\User\mcp.json`：
     含 `tia-portal`、正确 exe 绝对路径、两个超时 env；原有"File Context Server"
     市场条目及其扩展字段完好；原文件备份为 `.bak`；
  3. 重启 Trae 国内版 → 设置 → MCP，tia-portal 状态正常，Agent 能枚举并调用一个只读工具；
  4. `tia doctor` 显示 Trae CN "tia-portal registered"；
  5. 冷启动验证 120s 超时生效（必要时先停止 prewarm）。
- 回归：对 Cursor 再跑一次 config/doctor，确认 env 合并等共享改动无副作用。

## 6. 风险与应对

| 风险 | 影响 | 应对 |
|------|------|------|
| command 路径含空格被国内版拒绝 | server 启动失败 | config 时检测 exe 路径空格并醒目警告，建议无空格目录 |
| TIA headless 冷启动超默认启动超时 | server 被当崩溃杀掉 | 注入 START_MCP_TIMEOUT_MS=120000；文档提示可先 prewarm |
| 误损市场安装的 MCP 条目（含扩展字段） | 用户其它 MCP 丢失 | 合并只替换 tia-portal 键；单测专测保留 fromGalleryId/disabled；写入前 .bak |
| 国内版路径版本漂移 | 高版本改路径 | 已本机实测；路径集中在 KnownHosts 单点，易调整；doctor 可暴露探测结果 |
| 国际版被顺手误实现 | 超出用户要求范围 | 本期只加 Trae CN 一个条目；国际版仅在注释/文档记录，单测断言不含国际版条目 |

## 7. 范围边界（不做的事）

- 本期**不实现国际版 TraeCode**（`%APPDATA%\Trae\User\mcp.json`），仅留注释/文档扩展位。
- 不涉及 TRAE SOLO 客户端。
- 不改 MCP 协议、stdio/HTTP 传输与任何 Openness 业务逻辑。
- 不自动生成仓库根 `.trae/mcp.json`（项目级开关默认关闭，避免改变克隆用户仓库状态）。
- 不向 Trae MCP 市场发布（属平台分发流程，另行评估）。
