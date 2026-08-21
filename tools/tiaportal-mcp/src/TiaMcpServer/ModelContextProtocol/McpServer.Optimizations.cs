using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Siemens.Engineering.SW.Blocks;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.ModelContextProtocol
{
    // Partial: optimization features requested 2026-08-17.
    // Grounded in the REAL backend (Portal.* static methods + existing
    // AnalyzeBlockImpact / CollectCompilerMessages), NOT the fictional APIs
    // from the draft spec (which referenced Engineering.GetBlocksWithHierarchyAsync,
    // GetSkeletonSummaryAsync, GetBlockSourceAsync, GetDiagnosticsAsync, CreateFbAsync,
    // ImportInterfaceAsync — none of which exist in this codebase).
    public static partial class McpServer
    {
        #region helpers

        private static int SafeBlockNumber(PlcBlock b)
        {
            try { return b.Number; }
            catch { return 0; }
        }

        private static string BucketType(string typeName)
        {
            if (typeName.Contains("FunctionBlock") || typeName.EndsWith("FB")) return "FB";
            if (typeName.Contains("Function")) return "FC";
            if (typeName.Contains("DataBlock") || typeName.EndsWith("DB")) return "DB";
            if (typeName.Contains("OrganizationBlock") || typeName.EndsWith("OB")) return "OB";
            return typeName;
        }

        private static JsonArray ToJsonArray(List<string> list)
        {
            var a = new JsonArray();
            if (list != null) foreach (var s in list) a.Add(s);
            return a;
        }

        #endregion

        #region Feature 1 — AI-friendly project skeleton (replace "get_project_skeleton" draft)

        [McpServerTool(Name = "GetProjectSkeleton"), Description("[L1][PLC-Software] AI-friendly PROJECT SKELETON. Returns used DB/FB/FC/OB numbers, block counts by type, top-level block names and group names — so you can auto-pick free numbers and understand structure BEFORE creating/editing. AI workflow: call this (or GetBlocksWithHierarchy) FIRST, then SuggestBlockNumber before importing, then AnalyzeBlockImpact before changing a block. Requires: Connect + OpenProject.")]
        public static ResponseMessage GetProjectSkeleton(
            [Description("softwarePath: PLC software path, e.g. 'PLC_1'")] string softwarePath)
        {
            try
            {
                var blocks = Portal.GetBlocks(softwarePath, "");
                var usedDb = new JsonArray(); var usedFb = new JsonArray();
                var usedFc = new JsonArray(); var usedOb = new JsonArray();
                var countDict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                foreach (var b in blocks)
                {
                    string bucket = BucketType(b.GetType().Name);
                    int n = SafeBlockNumber(b);
                    switch (bucket)
                    {
                        case "FB": if (n > 0) usedFb.Add(n); break;
                        case "FC": if (n > 0) usedFc.Add(n); break;
                        case "DB": if (n > 0) usedDb.Add(n); break;
                        case "OB": if (n > 0) usedOb.Add(n); break;
                    }
                    if (countDict.ContainsKey(bucket)) countDict[bucket]++;
                    else countDict[bucket] = 1;
                }

                var topLevel = new JsonArray();
                var groups = new JsonArray();
                try
                {
                    var root = Portal.GetBlockRootGroup(softwarePath);
                    if (root != null)
                    {
                        foreach (var b in root.Blocks) topLevel.Add(b.Name);
                        foreach (var g in root.Groups) groups.Add(g.Name);
                    }
                }
                catch { /* best-effort */ }

                var counts = new JsonObject();
                foreach (var kv in countDict) counts[kv.Key] = kv.Value;

                return new ResponseMessage
                {
                    Message = $"Skeleton for '{softwarePath}': {blocks.Count} blocks.",
                    Meta = new JsonObject
                    {
                        ["blockCount"] = blocks.Count,
                        ["counts"] = counts,
                        ["usedDbNumbers"] = usedDb,
                        ["usedFbNumbers"] = usedFb,
                        ["usedFcNumbers"] = usedFc,
                        ["usedObNumbers"] = usedOb,
                        ["topLevelBlocks"] = topLevel,
                        ["topLevelGroups"] = groups,
                        ["timestamp"] = DateTime.Now,
                        ["success"] = true
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw McpError.WithRecovery(ex, $"Unexpected error building project skeleton for '{softwarePath}': {ex.Message}");
            }
        }

        #endregion

        #region Feature 2 — suggest / check a free block number (replace "CreateFb auto-assign" draft)

        [McpServerTool(Name = "SuggestBlockNumber"), Description("[L2][PLC-Software] Returns the next FREE number for a block type (FB/FC/DB/OB) so you avoid collisions when creating/importing. If you pass preferredNumber and it is free, that number is returned; otherwise the next free one is suggested. Pair with ImportBlock / RegenerateBlockFromSource / ImportPlcProgramFromDirectory. Requires: Connect + OpenProject.")]
        public static ResponseMessage SuggestBlockNumber(
            [Description("softwarePath: PLC software path, e.g. 'PLC_1'")] string softwarePath,
            [Description("blockType: FB | FC | DB | OB (case-insensitive)")] string blockType,
            [Description("preferredNumber: optional number to claim; if taken a free one is suggested")] int? preferredNumber = null)
        {
            try
            {
                var blocks = Portal.GetBlocks(softwarePath, "");
                var used = new HashSet<int>();
                foreach (var b in blocks)
                    if (BucketType(b.GetType().Name).Equals(blockType, StringComparison.OrdinalIgnoreCase))
                    { int n = SafeBlockNumber(b); if (n > 0) used.Add(n); }

                bool preferredOk = preferredNumber.HasValue && preferredNumber.Value > 0 && !used.Contains(preferredNumber.Value);
                int suggestion = preferredOk ? preferredNumber.Value : 1;
                while (used.Contains(suggestion)) suggestion++;

                return new ResponseMessage
                {
                    Message = $"Suggested {blockType} number: {suggestion}",
                    Meta = new JsonObject
                    {
                        ["blockType"] = blockType,
                        ["suggestedNumber"] = suggestion,
                        ["preferredNumber"] = preferredNumber,
                        ["preferredAvailable"] = preferredOk,
                        ["usedCount"] = used.Count,
                        ["timestamp"] = DateTime.Now,
                        ["success"] = true
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw McpError.WithRecovery(ex, $"Unexpected error suggesting block number for '{softwarePath}': {ex.Message}");
            }
        }

        #endregion

        #region Feature 3 — call chain (callers via AnalyzeBlockImpact; callees via SCL parse)

        [McpServerTool(Name = "GetCallChain"), Description("[L2][PLC-Software] Build a CALL relationship view for a block. direction=callers (default) returns AnalyzeBlockImpact: the block's interface + which blocks CALL it (blast radius if you change it). direction=callees parses the block's exported SCL to find which blocks it CALLs, recursively up to depth (best-effort; needs block consistent). Requires: Connect + OpenProject.")]
        public static ResponseJsonReport GetCallChain(
            [Description("softwarePath: PLC software path")] string softwarePath,
            [Description("blockName: target block name (or Group/Name)")] string blockName,
            [Description("direction: callers | callees (default callers)")] string direction = "callers",
            [Description("depth: recursion depth for callees (default 2)")] int depth = 2)
        {
            try
            {
                if (!(direction ?? "callers").Equals("callees", StringComparison.OrdinalIgnoreCase))
                    return Portal.AnalyzeBlockImpact(softwarePath, blockName, "");
                return BuildCalleeChain(softwarePath, blockName, depth);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw McpError.WithRecovery(ex, $"Unexpected error building call chain for '{blockName}': {ex.Message}");
            }
        }

        private static ResponseJsonReport BuildCalleeChain(string softwarePath, string blockName, int depth)
        {
            var data = new JsonObject
            {
                ["softwarePath"] = softwarePath,
                ["startBlock"] = blockName,
                ["direction"] = "callees",
                ["timestamp"] = DateTime.Now.ToString("O"),
                ["readOnly"] = true
            };
            var edges = new JsonArray();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            WalkCallees(softwarePath, blockName, depth, 0, edges, visited);
            data["edges"] = edges;
            return new ResponseJsonReport { Ok = true, Message = $"Callee chain for '{blockName}' (depth {depth}).", Data = data };
        }

        private static void WalkCallees(string softwarePath, string blockName, int maxDepth, int level, JsonArray edges, HashSet<string> visited)
        {
            if (level >= maxDepth || visited.Contains(blockName)) return;
            visited.Add(blockName);
            string exportDir = Path.Combine(Path.GetTempPath(), "tia_callee_" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(exportDir);
                string sclPath = Portal.ExportBlockSourceUtf8(softwarePath, blockName, exportDir, false);
                if (File.Exists(sclPath))
                {
                    string src = File.ReadAllText(sclPath);
                    foreach (var callee in ExtractCalledBlocks(src))
                    {
                        edges.Add(new JsonObject { ["from"] = blockName, ["to"] = callee });
                        WalkCallees(softwarePath, callee, maxDepth, level + 1, edges, visited);
                    }
                }
            }
            catch { /* best-effort: skip blocks that can't be exported/parsed */ }
            finally
            {
                try { Directory.Delete(exportDir, true); } catch { }
            }
        }

        private static List<string> ExtractCalledBlocks(string scl)
        {
            var result = new List<string>();
            // SCL call forms:  CALL "FB_Motor"   /   CALL FB10   /   UC "FC_Pump"   /   CC "Name"
            foreach (Match m in Regex.Matches(scl, @"\b(CALL|UC|CC)\s+(?:""([^""]+)""|((?:FB|FC|OB|DB)\d+))", RegexOptions.IgnoreCase))
            {
                string name = (m.Groups[2].Success && m.Groups[2].Value.Length > 0) ? m.Groups[2].Value : m.Groups[3].Value;
                if (!string.IsNullOrWhiteSpace(name) && !result.Contains(name, StringComparer.OrdinalIgnoreCase))
                    result.Add(name);
            }
            return result;
        }

        #endregion

        #region Feature 4 — import interface variables from JSON (replace "ImportInterface" draft)

        [McpServerTool(Name = "ImportBlockInterface"), Description("[L2][PLC-Software] Add interface variables (Input/Output/InOut/Static/Temp/Constant) to an existing block from a JSON spec, WITHOUT touching its logic. Exports the block SCL (V18-safe UTF-8+BOM), injects the VAR_* sections, regenerates via RegenerateBlockFromSource. JSON: {\"Input\":[{\"name\":\"Start\",\"type\":\"Bool\"}],\"Output\":[...],\"InOut\":[...],\"Static\":[...],\"Temp\":[...],\"Constant\":[...]}. Pair with ExportBlockSourceUtf8/RegenerateBlockFromSource. Requires: Connect + OpenProject + block consistent.")]
        public static ResponseMessage ImportBlockInterface(
            [Description("softwarePath: PLC software path")] string softwarePath,
            [Description("blockPath: fully qualified 'Group/Name' from GetSoftwareTree")] string blockPath,
            [Description("interfaceJson: JSON spec of variables to add per section")] string interfaceJson,
            [Description("exportDir: temp directory for the exported SCL (cleaned up automatically)")] string exportDir)
        {
            try
            {
                Directory.CreateDirectory(exportDir);
                string sclPath = Portal.ExportBlockSourceUtf8(softwarePath, blockPath, exportDir, false);
                string modified = InjectInterface(sclPath, interfaceJson);
                string outPath = sclPath + ".mod.scl";
                File.WriteAllText(outPath, modified);
                string groupPath = GroupOf(blockPath);
                bool ok = Portal.RegenerateBlockFromSource(softwarePath, groupPath, outPath);
                return new ResponseMessage
                {
                    Message = ok ? $"Interface imported into '{blockPath}'." : $"Regenerate failed for '{blockPath}'.",
                    Meta = new JsonObject
                    {
                        ["blockPath"] = blockPath,
                        ["regenerated"] = ok,
                        ["timestamp"] = DateTime.Now,
                        ["success"] = ok
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw McpError.WithRecovery(ex, $"Unexpected error importing interface for '{blockPath}': {ex.Message}");
            }
        }

        private static string GroupOf(string blockPath)
        {
            int idx = blockPath.LastIndexOf('/');
            return idx > 0 ? blockPath.Substring(0, idx) : "";
        }

        private static string InjectInterface(string sclPath, string interfaceJson)
        {
            string src = File.ReadAllText(sclPath);
            using var doc = JsonDocument.Parse(interfaceJson);
            var root = doc.RootElement;
            // JSON key -> SCL section keyword
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Input"] = "VAR_INPUT", ["Output"] = "VAR_OUTPUT", ["InOut"] = "VAR_IN_OUT",
                ["Static"] = "VAR", ["Temp"] = "VAR_TEMP", ["Constant"] = "VAR_CONSTANT"
            };
            foreach (var kv in map)
            {
                if (!root.TryGetProperty(kv.Key, out var arr) || arr.ValueKind != JsonValueKind.Array) continue;
                var decls = new List<string>();
                foreach (var item in arr.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    string name = item.TryGetProperty("name", out var n) ? (n.GetString() ?? "") : "";
                    string type = item.TryGetProperty("type", out var t) ? (t.GetString() ?? "") : "";
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(type)) continue;
                    string dflt = item.TryGetProperty("default", out var d) ? (d.GetString() ?? "") : "";
                    decls.Add(string.IsNullOrWhiteSpace(dflt) ? $"    {name} : {type};" : $"    {name} : {type} := {dflt};");
                }
                if (decls.Count == 0) continue;
                src = InsertIntoSection(src, kv.Value, decls);
            }
            return src;
        }

        private static string InsertIntoSection(string src, string section, List<string> decls)
        {
            // Insert before the section's END_VAR; if the section is missing, insert before BEGIN.
            var start = Regex.Match(src, @"\b" + Regex.Escape(section) + @"\b", RegexOptions.IgnoreCase);
            if (start.Success)
            {
                int endVar = src.IndexOf("END_VAR", start.Index, StringComparison.OrdinalIgnoreCase);
                if (endVar > 0) return src.Insert(endVar, string.Join("\n", decls) + "\n");
            }
            var begin = Regex.Match(src, @"\bBEGIN\b", RegexOptions.IgnoreCase);
            if (begin.Success)
            {
                string block = $"{section}\n{string.Join("\n", decls)}\nEND_VAR\n";
                return src.Insert(begin.Index, block);
            }
            return src + "\n" + section + "\n" + string.Join("\n", decls) + "\nEND_VAR\n";
        }

        #endregion

        #region Feature 5 — compile diagnostics summary (compact form of CompileAndDiagnosePlc)

        [McpServerTool(Name = "GetCompileDiagnostics"), Description("[L1][PLC-Software] Compile the PLC and return a CATEGORIZED diagnostic summary (error/warning/info counts + leaf messages) so an AI can auto-fix. Compact form of CompileAndDiagnosePlc. Requires: Connect + OpenProject.")]
        public static ResponseMessage GetCompileDiagnostics(
            [Description("softwarePath: PLC software path, e.g. 'PLC_1'")] string softwarePath,
            [Description("password: optional safety password")] string password = "")
        {
            try
            {
                var result = Portal.CompileSoftware(softwarePath, password);
                var collected = CollectCompilerMessages(result.Messages);
                return new ResponseMessage
                {
                    Message = $"Software '{softwarePath}' compiled. State={result.State} Errors={result.ErrorCount} Warnings={result.WarningCount}",
                    Meta = new JsonObject
                    {
                        ["state"] = result.State.ToString(),
                        ["errorCount"] = result.ErrorCount,
                        ["warningCount"] = result.WarningCount,
                        ["errors"] = ToJsonArray(collected.Errors),
                        ["warnings"] = ToJsonArray(collected.Warnings),
                        ["info"] = ToJsonArray(collected.Info),
                        ["timestamp"] = DateTime.Now,
                        ["success"] = !result.State.ToString().Equals("Error", StringComparison.OrdinalIgnoreCase)
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw McpError.WithRecovery(ex, $"Unexpected error compiling '{softwarePath}': {ex.Message}");
            }
        }

        #endregion
    }
}
