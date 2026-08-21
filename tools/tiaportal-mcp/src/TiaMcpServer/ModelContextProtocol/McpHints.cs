using System;

namespace TiaMcpServer.ModelContextProtocol
{
    /// <summary>
    /// Central recovery-hint translator. Turns a raw Openness/runtime exception into a short,
    /// actionable suffix appended to the generic "Unexpected error …: {ex.Message}" tool errors,
    /// so a less-capable AI driver is told WHAT TO DO instead of just seeing a raw stack message.
    /// Returns "" when nothing useful can be inferred (keeps clean errors clean).
    /// One helper, injected once into the 175 uniform catch sites — no per-tool edits.
    /// </summary>
    public static class McpHints
    {
        public static string Recovery(Exception? ex)
        {
            string m = Flatten(ex);
            if (m.Length == 0) return "";

            // not connected
            if (Has(m, "not connected") || Has(m, "connect first") || Has(m, "_portal") || Has(m, "no tia portal"))
                return Tip("call Connect first (the server also auto-connects when a TIA Portal is already running).");

            // no project bound
            if (Has(m, "no project is open") || Has(m, "project is null"))
                return Tip("open a project first: AttachToOpenProject(projectName) if it is already open in the TIA UI, else OpenProject(path) or CreateProject.");

            // project already open by another session/UI
            if (Has(m, "already open") || Has(m, "opened by") || Has(m, "in use by another"))
                return Tip("the project is already open elsewhere — use AttachToOpenProject(projectName) instead of OpenProject.");

            // name / path not found  -> covers the wrong-softwarePath / wrong-block-name case
            if (Has(m, "not found") || Has(m, "does not exist") || Has(m, "could not be found") ||
                Has(m, "no such") || Has(m, "unable to locate") || Has(m, "cannot find"))
                return Tip("the name/path may be wrong — call GetProjectTree / GetSoftwareTree / GetBlocks to read the REAL names (plc software path defaults to 'PLC_1', HMI to 'HMI_RT_1') instead of guessing.");

            // version mismatch (V20 exe vs V21 XML etc.)
            if (Has(m, "engineering version") || (Has(m, "version") && Has(m, "not supported")))
                return Tip("TIA version mismatch — run the exe that matches the installed TIA (V20 vs V21), and ensure imported XML's <Engineering version> matches.");

            // openness group / permissions
            if (Has(m, "openness") && (Has(m, "group") || Has(m, "permission") || Has(m, "denied")))
                return Tip("add the Windows user to the 'Siemens TIA Openness' group (call EnsureOpennessUserGroup), then retry.");

            // know-how protected blocks
            if (Has(m, "know-how") || Has(m, "knowhow") || Has(m, "protected"))
                return Tip("the block is know-how protected and cannot be read/exported via Openness; unprotect it in the TIA UI.");

            return "";
        }

        private static string Tip(string s) => "  ▶ RECOVERY: " + s;

        private static bool Has(string haystack, string needle) =>
            haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

        // Concatenate this exception's message with its inner chain so signatures buried in
        // inner Openness exceptions are still matched.
        private static string Flatten(Exception? ex)
        {
            if (ex == null) return "";
            var sb = new System.Text.StringBuilder();
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (sb.Length > 0) sb.Append(" | ");
                sb.Append(e.Message);
            }
            return sb.ToString();
        }
    }
}
