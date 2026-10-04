using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TiaMcpServer.Cli
{
    /// <summary>
    /// One-click MCP registration: writes the `tia-portal` server entry into an AI host's
    /// config file (Claude Desktop / Claude Code / Cursor / VS Code), pointing at the exe
    /// that matches the machine's TIA version — no REPLACE_ME, no manual JSON editing.
    /// Merges into existing config (keeps other servers and unrelated keys), backs up the
    /// old file first. Shipped inside the engine so the bundle needs no extra tool.
    /// </summary>
    public static class McpConfigInstaller
    {
        public const string ServerKey = "tia-portal";

        /// <summary>
        /// 一键注册（不带 --host）时的首选宿主名。无参数运行会优先保证该宿主被注册并在输出中
        /// 置顶主推；其余宿主仍按"本机已安装才写入"的既有检测逻辑处理。
        /// </summary>
        public const string DefaultHostName = "TRAE SOLO";

        // Keep Chinese path segments human-readable instead of \uXXXX escapes.
        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        /// <summary>Config file schema family.</summary>
        public enum HostStyle
        {
            /// <summary>Root key "mcpServers", entry {command,args} (Claude Desktop / Claude Code / Cursor).</summary>
            McpServers,
            /// <summary>Root key "servers", entry {type:"stdio",command,args} (VS Code mcp.json).</summary>
            VsCode,
            /// <summary>TOML [mcp_servers.*] sections (OpenAI Codex ~/.codex/config.toml).</summary>
            CodexToml,
        }

        public class Host
        {
            public string Name;
            public string ConfigPath;
            public HostStyle Style;

            /// <summary>
            /// 宿主专属、需要额外写入 server 条目 env 块的键值（如 Trae 的启动/调用超时）。
            /// 为 null 表示该宿主没有额外 env，条目 env 只由 --full 的 TIA_MCP_PROFILE 决定。
            /// 合并规则见 <see cref="BuildServerEntry"/>：profile 与本字典键不冲突时共存于同一 env 对象。
            /// </summary>
            public IReadOnlyDictionary<string, string>? ExtraEnv;

            public Host(string name, string path, HostStyle style)
            {
                Name = name; ConfigPath = path; Style = style;
            }

            /// <summary>
            /// 带宿主专属 env 的构造函数。
            /// </summary>
            /// <param name="name">宿主展示名（同时用于 --host 的模糊匹配）。</param>
            /// <param name="path">该宿主 MCP 配置文件的绝对路径。</param>
            /// <param name="style">配置文件 schema 家族。</param>
            /// <param name="extraEnv">写入条目 env 的宿主专属键值；无则传 null。</param>
            public Host(string name, string path, HostStyle style, IReadOnlyDictionary<string, string>? extraEnv)
            {
                Name = name; ConfigPath = path; Style = style; ExtraEnv = extraEnv;
            }
        }

        public static List<Host> KnownHosts()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return new List<Host>
            {
                new Host("Claude Desktop", Path.Combine(appData, "Claude", "claude_desktop_config.json"), HostStyle.McpServers),
                new Host("Claude Code",    Path.Combine(userProfile, ".claude.json"),                     HostStyle.McpServers),
                new Host("Cursor",         Path.Combine(userProfile, ".cursor", "mcp.json"),              HostStyle.McpServers),
                new Host("VS Code",        Path.Combine(appData, "Code", "User", "mcp.json"),             HostStyle.VsCode),
                // The commercial GUI's config wizard has written these four for a while; the
                // engine's own `config` did not — so the zero-install path (just run 配置MCP.bat),
                // the one meant to have the LOWEST barrier, supported fewer clients than the GUI.
                new Host("Codex",          Path.Combine(userProfile, ".codex", "config.toml"),            HostStyle.CodexToml),
                new Host("Gemini CLI",     Path.Combine(userProfile, ".gemini", "settings.json"),         HostStyle.McpServers),
                new Host("Windsurf",       Path.Combine(userProfile, ".codeium", "windsurf", "mcp_config.json"), HostStyle.McpServers),
                new Host("Cline",          Path.Combine(appData, "Code", "User", "globalStorage",
                                                        "saoudrizwan.claude-dev", "settings",
                                                        "cline_mcp_settings.json"),                       HostStyle.McpServers),
                // Trae 国内版（Trae CN）。全局配置即 Agent 实际读取的文件，格式与 Claude/Cursor 同为
                // 根键 mcpServers + {command,args,env}。TIA headless 冷启动 10–28s，远超 Trae 默认启动
                // 超时，故这里通过其专属 env 把启动超时放宽到 120s、单次工具调用超时放宽到 600s。
                // 注意：Trae 官方要求 command 路径不含空格，含空格交付目录的警告在 CliCommands.Config 里提示。
                // 国际版 Trae/TraeCode 的候选路径为 %APPDATA%\Trae\User\mcp.json（本机当前尚无该文件），
                // 按分期要求本期不实现，勿并入本条目。
                new Host("Trae CN",        Path.Combine(appData, "Trae CN", "User", "mcp.json"), HostStyle.McpServers,
                         new Dictionary<string, string>
                         {
                             ["START_MCP_TIMEOUT_MS"] = "120000",
                             ["RUN_MCP_TIMEOUT_MS"]   = "600000",
                         }),
                // TRAE SOLO（独立 SOLO 客户端，VS Code 内核 Electron 应用，用户数据目录为 TRAE SOLO）。
                // 全局 MCP 配置与其 IDE 版同构：User\mcp.json + 根键 mcpServers。SOLO 是一键注册的
                // 默认/首选宿主（见 CliCommands.Config 的排序与主推）。冷启动同样需要放宽超时。
                // 注意 command 路径不能含空格（官方限制），含空格警告在 CliCommands.Config 里给出。
                new Host("TRAE SOLO",      Path.Combine(appData, "TRAE SOLO", "User", "mcp.json"), HostStyle.McpServers,
                         new Dictionary<string, string>
                         {
                             ["START_MCP_TIMEOUT_MS"] = "120000",
                             ["RUN_MCP_TIMEOUT_MS"]   = "600000",
                         }),
            };
        }

        /// <summary>Full path of the currently running engine exe.</summary>
        public static string OwnExePath()
        {
            try { return System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName; }
            catch { return System.Reflection.Assembly.GetExecutingAssembly().Location; }
        }

        /// <summary>
        /// The exe the config should point at for <paramref name="tiaMajorVersion"/>: this exe
        /// when it matches, otherwise the sibling built for that version (falls back to this
        /// exe — it self-routes at startup anyway, this just avoids the extra hop).
        /// </summary>
        public static string ExeForVersion(int tiaMajorVersion)
        {
            if (tiaMajorVersion == Siemens.EngineRouter.CompiledTiaMajorVersion) return OwnExePath();
            return Siemens.EngineRouter.FindSiblingExe(tiaMajorVersion) ?? OwnExePath();
        }

        public static JsonObject BuildServerEntry(string exePath, int tiaMajorVersion, HostStyle style, bool full = false)
            => BuildServerEntry(exePath, tiaMajorVersion, style, full, null);

        /// <summary>
        /// 构造单个 mcpServers/servers 条目。
        /// </summary>
        /// <param name="exePath">引擎 exe 绝对路径。</param>
        /// <param name="tiaMajorVersion">写入 args 的 TIA 大版本。</param>
        /// <param name="style">宿主 schema（决定是否补 type:"stdio"）。</param>
        /// <param name="full">true 时额外写 TIA_MCP_PROFILE=full。</param>
        /// <param name="extraEnv">宿主专属 env（如 Trae 超时）；与 full 的 profile 键共存，null 表示无。</param>
        /// <returns>可直接挂到 mcpServers/servers 下的条目 JsonObject。</returns>
        public static JsonObject BuildServerEntry(string exePath, int tiaMajorVersion, HostStyle style, bool full,
                                                  IReadOnlyDictionary<string, string>? extraEnv)
        {
            var entry = new JsonObject();
            if (style == HostStyle.VsCode) entry["type"] = "stdio";
            entry["command"] = exePath;
            entry["args"] = new JsonArray("--tia-major-version", tiaMajorVersion.ToString());
            // The engine defaults to the ~48-tool lite roster on its own, so the normal config
            // needs no env at all. Only the opt-out is worth writing — and it is an opt-out with
            // consequences: the full roster exceeds what VS Code/Copilot (128) and Windsurf (100) load.
            // env 块按需创建：--full 的 profile 与宿主专属 env（如 Trae 超时）合并共存。
            bool hasProfile = full;
            bool hasExtra = extraEnv != null && extraEnv.Count > 0;
            if (hasProfile || hasExtra)
            {
                var env = new JsonObject();
                if (hasProfile) env["TIA_MCP_PROFILE"] = "full";
                if (hasExtra)
                    foreach (var kv in extraEnv!)
                        env[kv.Key] = kv.Value;
                entry["env"] = env;
            }
            return entry;
        }

        /// <summary>Pretty single-server snippet for hosts we don't write automatically.</summary>
        public static string Snippet(string exePath, int tiaMajorVersion, HostStyle style = HostStyle.McpServers, bool full = false)
            => Snippet(exePath, tiaMajorVersion, style, full, null);

        /// <summary>
        /// 输出供手动粘贴的单 server JSON/TOML 片段。
        /// </summary>
        /// <param name="extraEnv">宿主专属 env，透传给条目构建；null 表示无。</param>
        /// <returns>格式化后的配置片段文本。</returns>
        public static string Snippet(string exePath, int tiaMajorVersion, HostStyle style, bool full,
                                     IReadOnlyDictionary<string, string>? extraEnv)
        {
            if (style == HostStyle.CodexToml) return CodexTomlSection(exePath, tiaMajorVersion, full);
            string rootKey = style == HostStyle.VsCode ? "servers" : "mcpServers";
            var root = new JsonObject
            {
                [rootKey] = new JsonObject { [ServerKey] = BuildServerEntry(exePath, tiaMajorVersion, style, full, extraEnv) }
            };
            return root.ToJsonString(JsonOpts);
        }

        /// <summary>
        /// Upserts the tia-portal server into one host config. Returns a human-readable status line.
        /// Throws on hard I/O / parse failure so the caller can report it.
        /// </summary>
        public static string Apply(string configPath, string exePath, int tiaMajorVersion, HostStyle style = HostStyle.McpServers, bool full = false)
            => Apply(configPath, exePath, tiaMajorVersion, style, full, null);

        /// <summary>
        /// Upserts the tia-portal server into one host config. Returns a human-readable status line.
        /// Throws on hard I/O / parse failure so the caller can report it.
        /// </summary>
        /// <param name="extraEnv">宿主专属 env，透传进条目；null 表示无（与既有宿主行为一致）。</param>
        public static string Apply(string configPath, string exePath, int tiaMajorVersion, HostStyle style, bool full,
                                   IReadOnlyDictionary<string, string>? extraEnv)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(configPath));
            if (style == HostStyle.CodexToml) return ApplyCodexToml(configPath, exePath, tiaMajorVersion, full);

            JsonObject root;
            if (File.Exists(configPath))
            {
                var text = File.ReadAllText(configPath);
                root = string.IsNullOrWhiteSpace(text)
                    ? new JsonObject()
                    : JsonNode.Parse(text) as JsonObject ?? throw new InvalidDataException("existing config is not a JSON object");
                File.Copy(configPath, configPath + ".bak", overwrite: true);
            }
            else
            {
                root = new JsonObject();
            }

            string rootKey = style == HostStyle.VsCode ? "servers" : "mcpServers";
            if (root[rootKey] is not JsonObject servers)
            {
                servers = new JsonObject();
                root[rootKey] = servers;
            }

            bool existed = servers.ContainsKey(ServerKey);
            servers[ServerKey] = BuildServerEntry(exePath, tiaMajorVersion, style, full, extraEnv);

            AtomicWriteAllText(configPath, root.ToJsonString(JsonOpts));
            return (existed ? "updated" : "wrote") + " " + ServerKey + " -> " + configPath;
        }

        /// <summary>
        /// The engine path a host is currently configured to launch, or null when the host has no
        /// tia-portal entry. Used by `doctor`: "registered" is not the same as "working" — a config
        /// carried over from another machine (or from a moved bundle) still contains the entry while
        /// pointing at an exe that no longer exists, and the host then just fails to start it.
        /// </summary>
        public static string? RegisteredCommand(Host host)
        {
            try
            {
                if (!File.Exists(host.ConfigPath)) return null;
                string text = File.ReadAllText(host.ConfigPath);

                if (host.Style == HostStyle.CodexToml)
                {
                    bool inSection = false;
                    foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
                    {
                        var trimmed = line.TrimStart();
                        if (trimmed.StartsWith("[", StringComparison.Ordinal))
                        {
                            inSection = trimmed.StartsWith("[mcp_servers." + ServerKey + "]", StringComparison.Ordinal);
                            continue;
                        }
                        if (!inSection) continue;
                        var m = System.Text.RegularExpressions.Regex.Match(trimmed, @"^command\s*=\s*(['""])(.*)\1\s*$");
                        if (m.Success) return m.Groups[2].Value;
                    }
                    return null;
                }

                if (JsonNode.Parse(text) is not JsonObject root) return null;
                string rootKey = host.Style == HostStyle.VsCode ? "servers" : "mcpServers";
                if (root[rootKey] is not JsonObject servers) return null;
                if (servers[ServerKey] is not JsonObject entry) return null;
                return entry["command"]?.GetValue<string>();
            }
            catch { return null; }
        }

        /// <summary>The TOML section Codex needs; standalone so `config --print` can show it too.</summary>
        private static string CodexTomlSection(string exePath, int tiaMajorVersion, bool full)
        {
            var sb = new StringBuilder();
            sb.AppendLine("[mcp_servers." + ServerKey + "]");
            sb.AppendLine("command = " + TomlString(exePath));
            sb.AppendLine("args = [\"--tia-major-version\", \"" + tiaMajorVersion + "\"]");
            // TIA needs far longer to come up than Codex's 10s default; without this Codex kills
            // the server mid-startup and reports it as a crash.
            sb.AppendLine("startup_timeout_sec = 120");
            if (full)
            {
                sb.AppendLine();
                sb.AppendLine("[mcp_servers." + ServerKey + ".env]");
                sb.AppendLine("TIA_MCP_PROFILE = \"full\"");
            }
            return sb.ToString();
        }

        /// <summary>
        /// Codex config is TOML. Rather than take on a TOML dependency, drop our own
        /// [mcp_servers.tia-portal] section (and its sub-sections) line by line and append a fresh
        /// one — a [a.b] section is legal anywhere in the file, so everything the user wrote for
        /// other servers survives untouched.
        /// </summary>
        private static string ApplyCodexToml(string configPath, string exePath, int tiaMajorVersion, bool full)
        {
            string text = "";
            bool existed = false;
            if (File.Exists(configPath))
            {
                File.Copy(configPath, configPath + ".bak", overwrite: true);
                var kept = new List<string>();
                bool inTia = false;
                foreach (var line in File.ReadAllText(configPath).Replace("\r\n", "\n").Split('\n'))
                {
                    var trimmed = line.TrimStart();
                    if (trimmed.StartsWith("[", StringComparison.Ordinal))
                    {
                        inTia = trimmed.StartsWith("[mcp_servers." + ServerKey + "]", StringComparison.Ordinal)
                             || trimmed.StartsWith("[mcp_servers." + ServerKey + ".", StringComparison.Ordinal);
                        if (inTia) existed = true;
                    }
                    if (!inTia) kept.Add(line);
                }
                text = string.Join(Environment.NewLine, kept).TrimEnd();
            }

            var sb = new StringBuilder(text);
            if (sb.Length > 0) { sb.AppendLine(); sb.AppendLine(); }
            sb.Append(CodexTomlSection(exePath, tiaMajorVersion, full));
            AtomicWriteAllText(configPath, sb.ToString());
            return (existed ? "updated" : "wrote") + " " + ServerKey + " -> " + configPath;
        }

        /// <summary>Windows path as a TOML literal string (no backslash escaping inside '...').</summary>
        private static string TomlString(string s)
        {
            if (s.IndexOf('\'') < 0 && s.IndexOf('\n') < 0 && s.IndexOf('\r') < 0) return "'" + s + "'";
            return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", "\\n") + "\"";
        }

        /// <summary>Temp file + replace: a crash mid-write must not truncate the user's config.</summary>
        private static void AtomicWriteAllText(string path, string content)
        {
            string tmp = path + ".tmp." + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(tmp, content, new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }
    }
}
