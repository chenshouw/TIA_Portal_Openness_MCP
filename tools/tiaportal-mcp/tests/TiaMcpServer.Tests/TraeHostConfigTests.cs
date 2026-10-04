using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using TiaMcpServer.Cli;

// ─────────────────────────────────────────────────────────────────────────────
//  AI 宿主一键注册 —— Trae CN（国内版）专项回归。
//
//  盯三类「写错了也不崩」的缺陷：
//   · 宿主专属 env（Trae 冷启动/调用超时）漏注入或被 --full 的 profile 覆盖；
//   · upsert 时把用户已有的、从市场安装的 MCP 条目（带 fromGalleryId/disabled 等
//     扩展字段）整体抹掉 —— 只该替换 tia-portal 这一个键；
//   · 分期越界：本期只允许出现 Trae CN，不许顺手把国际版 Trae 也注册进来。
//  全部离线，操作临时目录，不碰真实 %APPDATA%。
// ─────────────────────────────────────────────────────────────────────────────
internal static class TraeHostConfigTests
{
    public static void Run(Action<bool, string> check)
    {
        void Check(bool ok, string what) => check(ok, what);

        // ---- KnownHosts：Trae CN 路径与结构 ----
        var hosts = McpConfigInstaller.KnownHosts();
        var traeCn = hosts.Find(h => h.Name == "Trae CN");
        Check(traeCn != null, "trae-cn: KnownHosts 包含名为 'Trae CN' 的宿主");
        if (traeCn != null)
        {
            Check(traeCn.ConfigPath.Replace('/', '\\').EndsWith(
                      "Trae CN" + Path.DirectorySeparatorChar + "User" + Path.DirectorySeparatorChar + "mcp.json",
                      StringComparison.Ordinal),
                  "trae-cn: 配置路径以 Trae CN\\User\\mcp.json 结尾（实际: " + traeCn.ConfigPath + "）");
            Check(traeCn.Style == McpConfigInstaller.HostStyle.McpServers,
                  "trae-cn: 使用标准 mcpServers 结构");
            Check(traeCn.ExtraEnv != null
                  && traeCn.ExtraEnv.TryGetValue("START_MCP_TIMEOUT_MS", out var startMs) && startMs == "120000"
                  && traeCn.ExtraEnv.TryGetValue("RUN_MCP_TIMEOUT_MS", out var runMs) && runMs == "600000",
                  "trae-cn: 注入 120s 启动 / 600s 调用超时 env");
        }

        // ---- TRAE SOLO：默认/首选宿主，路径落到 TRAE SOLO\User\mcp.json ----
        var solo = hosts.Find(h => h.Name == "TRAE SOLO");
        Check(solo != null, "trae-solo: KnownHosts 包含名为 'TRAE SOLO' 的宿主");
        if (solo != null)
        {
            Check(solo.ConfigPath.Replace('/', '\\').EndsWith(
                      "TRAE SOLO" + Path.DirectorySeparatorChar + "User" + Path.DirectorySeparatorChar + "mcp.json",
                      StringComparison.Ordinal),
                  "trae-solo: 配置路径以 TRAE SOLO\\User\\mcp.json 结尾（实际: " + solo.ConfigPath + "）");
            Check(solo.Style == McpConfigInstaller.HostStyle.McpServers,
                  "trae-solo: 使用标准 mcpServers 结构");
            Check(solo.ExtraEnv != null
                  && solo.ExtraEnv["START_MCP_TIMEOUT_MS"] == "120000"
                  && solo.ExtraEnv["RUN_MCP_TIMEOUT_MS"] == "600000",
                  "trae-solo: 注入 120s 启动 / 600s 调用超时 env");
        }
        Check(McpConfigInstaller.DefaultHostName == "TRAE SOLO",
              "trae-solo: DefaultHostName 为 TRAE SOLO（一键注册的默认首选）");
        // 默认首选必须在宿主列表里真实存在，否则排序置顶会落空。
        Check(hosts.Exists(h => h.Name == McpConfigInstaller.DefaultHostName),
              "trae-solo: 默认首选宿主在 KnownHosts 中存在");
        // 分期哨兵：国际版 TraeCode 本期仍不注册。
        Check(hosts.Find(h => h.Name == "Trae" || h.Name == "TraeCode") == null,
              "trae: [分期哨兵] 本期不注册国际版 Trae/TraeCode");

        // ---- entry 构建：默认 lite + Trae 超时（以 SOLO 的 ExtraEnv 验证）----
        var entry = McpConfigInstaller.BuildServerEntry(
            @"C:\tia\TiaMcpServer.exe", 20, McpConfigInstaller.HostStyle.McpServers, false,
            solo?.ExtraEnv);
        var env0 = entry["env"]?.AsObject();
        Check(env0 != null
              && env0["START_MCP_TIMEOUT_MS"]?.GetValue<string>() == "120000"
              && env0["RUN_MCP_TIMEOUT_MS"]?.GetValue<string>() == "600000",
              "trae-solo: 条目 env 含两个超时键");
        Check(env0 != null && env0["TIA_MCP_PROFILE"] == null,
              "trae-solo: 默认 lite 不写 TIA_MCP_PROFILE");

        // ---- entry 构建：--full 与宿主超时必须共存（互不覆盖）----
        var entryFull = McpConfigInstaller.BuildServerEntry(
            @"C:\tia\TiaMcpServer.exe", 20, McpConfigInstaller.HostStyle.McpServers, true,
            solo?.ExtraEnv);
        var envF = entryFull["env"]?.AsObject();
        Check(envF != null
              && envF["TIA_MCP_PROFILE"]?.GetValue<string>() == "full"
              && envF["START_MCP_TIMEOUT_MS"]?.GetValue<string>() == "120000"
              && envF["RUN_MCP_TIMEOUT_MS"]?.GetValue<string>() == "600000",
              "trae-solo: --full 时 TIA_MCP_PROFILE=full 与两个超时 env 共存");

        // ---- 回归：无 extraEnv 的普通宿主不应凭空多出超时键 ----
        var plain = McpConfigInstaller.BuildServerEntry(
            @"C:\tia\TiaMcpServer.exe", 20, McpConfigInstaller.HostStyle.McpServers, false, null);
        Check(plain["env"] == null,
              "regression: extraEnv=null 且非 full 时条目不写 env 块");

        // ---- Apply：保留市场安装条目及其扩展字段 + 幂等 + 备份 ----
        var tmp = Path.Combine(Path.GetTempPath(), "trae_mcp_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        var cfg = Path.Combine(tmp, "mcp.json");
        try
        {
            // 模拟国内版现存配置：一个市场安装、被禁用的 server，带 Trae 扩展字段。
            File.WriteAllText(cfg,
                "{\n  \"mcpServers\": {\n" +
                "    \"File Context Server\": {\n" +
                "      \"command\": \"npx\",\n" +
                "      \"args\": [\"--yes\", \"file-context-server\"],\n" +
                "      \"env\": { \"CACHE_TTL\": \"3600000\" },\n" +
                "      \"fromGalleryId\": \"bsmi021.mcp-file-context-server\",\n" +
                "      \"disabled\": true\n" +
                "    }\n  }\n}",
                new System.Text.UTF8Encoding(false));

            var exe = @"C:\tia\TiaMcpServer.exe";
            var msg1 = McpConfigInstaller.Apply(cfg, exe, 20,
                McpConfigInstaller.HostStyle.McpServers, false, traeCn?.ExtraEnv);
            Check(msg1.StartsWith("wrote", StringComparison.Ordinal),
                  "trae-cn: 首次写入返回 wrote（" + msg1 + "）");

            var root = JsonNode.Parse(File.ReadAllText(cfg)) as JsonObject;
            var servers = root?["mcpServers"]?.AsObject();
            Check(servers != null && servers.ContainsKey(McpConfigInstaller.ServerKey),
                  "trae-cn: 写入后含 tia-portal 条目");
            var market = servers?["File Context Server"]?.AsObject();
            Check(market != null
                  && market["fromGalleryId"]?.GetValue<string>() == "bsmi021.mcp-file-context-server"
                  && market["disabled"]?.GetValue<bool>() == true
                  && market["command"]?.GetValue<string>() == "npx",
                  "trae-cn: 市场条目及其 fromGalleryId/disabled/command 扩展字段原样保留");
            var tiaEnv = servers?[McpConfigInstaller.ServerKey]?["env"]?.AsObject();
            Check(tiaEnv != null
                  && tiaEnv["START_MCP_TIMEOUT_MS"]?.GetValue<string>() == "120000",
                  "trae-cn: 落盘的 tia-portal 条目带启动超时 env");
            Check(File.Exists(cfg + ".bak"), "trae-cn: 写入前生成 .bak 备份");

            // 幂等：再写一次应是 updated，且市场条目仍在。
            var msg2 = McpConfigInstaller.Apply(cfg, exe, 20,
                McpConfigInstaller.HostStyle.McpServers, false, traeCn?.ExtraEnv);
            Check(msg2.StartsWith("updated", StringComparison.Ordinal),
                  "trae-cn: 再次写入返回 updated（" + msg2 + "）");
            var root2 = JsonNode.Parse(File.ReadAllText(cfg)) as JsonObject;
            var servers2 = root2?["mcpServers"]?.AsObject();
            Check(servers2 != null
                  && servers2.ContainsKey(McpConfigInstaller.ServerKey)
                  && servers2.ContainsKey("File Context Server"),
                  "trae-cn: 重复执行后 tia-portal 与市场条目并存（幂等不丢配置）");

            // ---- RegisteredCommand：能读回 command；doctor 据此可判 exe 是否存在 ----
            var hostLite = new McpConfigInstaller.Host("Trae CN", cfg,
                McpConfigInstaller.HostStyle.McpServers, traeCn?.ExtraEnv);
            Check(McpConfigInstaller.RegisteredCommand(hostLite) == exe,
                  "trae-cn: RegisteredCommand 读回写入的 command");
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }
}
