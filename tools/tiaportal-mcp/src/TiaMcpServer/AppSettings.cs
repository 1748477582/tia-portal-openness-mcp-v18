using System;
using System.IO;
using System.Text.Json.Nodes;

namespace TiaMcpServer
{
    /// <summary>
    /// Q6: appsettings.json 配置外置化。
    /// 优先级（高→低）：命令行 CLI > 环境变量 env（由各读取点自处理）> appsettings.json > 内置默认值。
    /// 本类只负责加载 appsettings.json 并持有默认值；CLI 覆盖逻辑在 Program.Main 中合并。
    /// 不引用任何 Siemens/Openness 类型，可在启动早期安全加载。
    /// 文件缺失、损坏或字段类型不匹配时静默回退默认值，绝不阻断启动。
    /// </summary>
    public static class AppSettings
    {
        // ---- 默认值（appsettings.json 未配置时生效）----
        public const int DefaultCompileTimeoutSeconds = 600;
        public const int DefaultCacheMaxEntries = 64;

        public static string? TiaPortalLocation { get; private set; }
        public static int? TiaMajorVersion { get; private set; }
        public static int? Logging { get; private set; }
        public static string? Transport { get; private set; }
        public static string? HttpPrefix { get; private set; }
        public static string? HttpApiKey { get; private set; }
        public static int CompileTimeoutSeconds { get; private set; } = DefaultCompileTimeoutSeconds;
        public static int CacheMaxEntries { get; private set; } = DefaultCacheMaxEntries;
        public static int? StepTimeoutSeconds { get; private set; }

        /// <summary>配置文件默认位置：exe 所在目录。</summary>
        public static string ConfigPath => Path.Combine(AppContext.BaseDirectory, "appsettings.json");

        public static void Load(string? path = null)
        {
            var configPath = path ?? ConfigPath;
            if (!File.Exists(configPath)) return;

            try
            {
                // AllowComments/AllowTrailingCommas: 方便用户维护，字段可带注释与尾逗号。
                var doc = System.Text.Json.JsonDocument.Parse(
                    File.ReadAllText(configPath),
                    new System.Text.Json.JsonDocumentOptions
                    {
                        CommentHandling = System.Text.Json.JsonCommentHandling.Skip,
                        AllowTrailingCommas = true
                    });
                using (doc)
                {
                    var root = doc.RootElement;
                    if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return;

                    if (root.TryGetProperty("tia", out var tia) && tia.ValueKind == System.Text.Json.JsonValueKind.Object)
                    {
                        if (TryGetString(tia, "portalLocation", out var loc)) TiaPortalLocation = loc;
                        if (TryGetInt(tia, "majorVersion", out var mv) && mv > 0) TiaMajorVersion = mv;
                        if (TryGetInt(tia, "compileTimeoutSeconds", out var cts) && cts > 0) CompileTimeoutSeconds = cts;
                        if (TryGetInt(tia, "cacheMaxEntries", out var cme) && cme > 0) CacheMaxEntries = cme;
                        if (TryGetInt(tia, "stepTimeoutSeconds", out var sts) && sts > 0) StepTimeoutSeconds = sts;
                    }

                    if (root.TryGetProperty("mcp", out var mcp) && mcp.ValueKind == System.Text.Json.JsonValueKind.Object)
                    {
                        if (TryGetString(mcp, "transport", out var tr)) Transport = tr.ToLowerInvariant();
                        if (TryGetString(mcp, "httpPrefix", out var hp)) HttpPrefix = hp;
                        if (TryGetString(mcp, "httpApiKey", out var ak)) HttpApiKey = ak;
                    }

                    if (TryGetInt(root, "logging", out var log)) Logging = log;
                }
            }
            catch (Exception ex)
            {
                // 配置损坏不阻断启动：写入诊断日志后继续用默认值。
                try
                {
                    File.AppendAllText(Path.Combine(Path.GetTempPath(), "TiaMcpServer.log"),
                        $"[AppSettings] load failed for '{configPath}': {ex.Message}\n");
                }
                catch { /* ignore */ }
            }
        }

        private static bool TryGetString(System.Text.Json.JsonElement obj, string name, out string value)
        {
            value = string.Empty;
            if (obj.ValueKind != System.Text.Json.JsonValueKind.Object) return false;
            if (!obj.TryGetProperty(name, out var el) || el.ValueKind != System.Text.Json.JsonValueKind.String) return false;
            value = el.GetString()?.Trim() ?? string.Empty;
            return value.Length > 0;
        }

        private static bool TryGetInt(System.Text.Json.JsonElement obj, string name, out int value)
        {
            value = 0;
            if (obj.ValueKind != System.Text.Json.JsonValueKind.Object) return false;
            if (!obj.TryGetProperty(name, out var el)) return false;
            if (el.ValueKind == System.Text.Json.JsonValueKind.Number && el.TryGetInt32(out value)) return true;
            if (el.ValueKind == System.Text.Json.JsonValueKind.String && int.TryParse(el.GetString(), out value)) return true;
            return false;
        }
    }
}
