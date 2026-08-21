using System;
using System.IO;
using System.Reflection;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens
{
    /// <summary>
    /// Lightweight operation audit log for mutating TIA operations.
    /// Records who / when / what for traceability (audit gap called out in the 2026-08-21 review).
    /// Writes to the shared McpServer.Logger (if set) AND a daily rolling file next to the exe.
    /// Designed to NEVER throw into the calling operation.
    /// </summary>
    public static class AuditLogger
    {
        private static readonly object _fileLock = new object();

        public static void Record(string action, string detail)
        {
            try
            {
                var who = Environment.UserName;
                try
                {
                    var id = WindowsIdentity.GetCurrent()?.Name;
                    if (!string.IsNullOrEmpty(id)) who = id;
                }
                catch
                {
                    // WindowsIdentity may be unavailable in some host contexts; fall back to Environment.UserName.
                }

                var ts = DateTime.Now;
                var line = $"{ts:yyyy-MM-dd HH:mm:ss}\t{who}\t{action}\t{detail}";

                // 1) structured host logger (picked up by the WorkBuddy/logging pipeline)
                McpServer.Logger?.LogInformation("[AUDIT] {Action} by {Who}: {Detail}", action, who, detail);

                // 2) rolling file next to the executable: <exeDir>/audit/audit-YYYY-MM-DD.log
                try
                {
                    var baseDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".";
                    var logDir = Path.Combine(baseDir, "audit");
                    Directory.CreateDirectory(logDir);
                    var file = Path.Combine(logDir, $"audit-{ts:yyyy-MM-dd}.log");
                    lock (_fileLock)
                    {
                        File.AppendAllText(file, line + Environment.NewLine);
                    }
                }
                catch
                {
                    // file sink is best-effort only
                }
            }
            catch
            {
                // audit must never break the operation it traces
            }
        }
    }
}
