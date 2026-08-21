using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Siemens.Engineering;
using Siemens.Engineering.SW.Blocks;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens
{
    // Partial: block impact analysis (the "big picture" tool).
    // Answers two questions that Openness cannot answer directly because it has NO
    // reverse call-index API:
    //   1. What is this block's interface (Input/Output/Static/Constant/Return pins)?
    //   2. Which other blocks CALL it (the blast radius if you change it)?
    // Both are built on the same Export->SimaticML->XML-parse approach already proven
    // in Portal.CausalTrace.cs (Openness cannot export while TIA is ONLINE, and
    // IsConsistent=false blocks must be compiled first). Read-only on the project.
    public partial class Portal
    {
        public ResponseJsonReport AnalyzeBlockImpact(string softwarePath, string blockName, string blockScope = "")
        {
            return _sta.Run(() =>
            {
            ValidateBlockName(blockName, "AnalyzeBlockImpact");
            var data = new JsonObject
            {
                ["softwarePath"] = softwarePath,
                ["blockName"] = blockName,
                ["timestamp"] = DateTime.Now.ToString("O"),
                ["readOnly"] = true
            };
            var warnings = new JsonArray();

            if (IsProjectNull())
                return new ResponseJsonReport { Ok = false, Message = "No project open. Attach first.", Data = data };
            if (string.IsNullOrWhiteSpace(blockName))
                return new ResponseJsonReport { Ok = false, Message = "blockName is required.", Data = data };

            var target = GetBlock(softwarePath, blockName);
            if (target == null)
                return new ResponseJsonReport { Ok = false, Message = $"Block '{blockName}' not found in '{softwarePath}'.", Data = data };

            string targetName = target.Name;
            int targetNumber = SafeGetNumber(target);
            string targetPath = GetBlockPath(target);
            string targetType = target.GetType().Name;

            data["target"] = new JsonObject
            {
                ["name"] = targetName,
                ["number"] = targetNumber,
                ["path"] = targetPath,
                ["type"] = targetType,
                ["isConsistent"] = target.IsConsistent
            };

            // ---- interface (pins) from the target block's own export ----
            var iface = ParseInterface(target, warnings);
            if (iface != null) data["interface"] = iface;

            // ---- callers: scan all in-scope code blocks ----
            List<PlcBlock> blocks;
            try { blocks = GetBlocks(softwarePath, blockScope ?? ""); }
            catch (Exception ex) { return new ResponseJsonReport { Ok = false, Message = $"GetBlocks failed: {ex.Message}", Data = data }; }

            var codeBlocks = blocks.Where(b => !(b is DataBlock)).ToList();
            data["scannedBlockCount"] = codeBlocks.Count;

            var callers = new JsonArray();
            int analyzedOk = 0;

            var tmpDir = Path.Combine(Path.GetTempPath(), "tia_impact_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tmpDir);
            try
            {
                foreach (var block in codeBlocks)
                {
                    if (block.Name.Equals(targetName, StringComparison.OrdinalIgnoreCase)) continue; // skip self
                    if (!block.IsConsistent) { warnings.Add($"Skipped inconsistent block '{block.Name}' (compile first)."); continue; }
                    string xmlPath = Path.Combine(tmpDir, block.Name + ".xml");
                    try { block.Export(new FileInfo(xmlPath), ExportOptions.None); }
                    catch (Exception ex) { warnings.Add($"Export failed for '{block.Name}': {ex.Message}"); continue; }
                    XDocument doc;
                    try { doc = XDocument.Load(xmlPath); }
                    catch (Exception ex) { warnings.Add($"Parse failed for '{block.Name}': {ex.Message}"); continue; }

                    if (BlockCalls(doc, targetName))
                    {
                        callers.Add(new JsonObject
                        {
                            ["block"] = block.Name,
                            ["blockPath"] = GetBlockPath(block),
                            ["type"] = block.GetType().Name
                        });
                    }
                    analyzedOk++;
                }
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }

            data["callers"] = callers;
            data["callerCount"] = callers.Count;
            data["analyzedBlockCount"] = analyzedOk;
            if (warnings.Count > 0) data["warnings"] = warnings;

            string summary;
            if (codeBlocks.Count > 0 && analyzedOk == 0)
            {
                bool onlineMode = warnings.Any(w => w!.ToString().IndexOf("online mode", StringComparison.OrdinalIgnoreCase) >= 0);
                summary = onlineMode
                    ? "INCONCLUSIVE: no block could be exported because TIA is connected ONLINE (Openness cannot export blocks in online mode). Go offline in TIA (Online ▸ Go offline) — the project stays open — then retry."
                    : $"INCONCLUSIVE: none of {codeBlocks.Count} code block(s) could be exported/parsed (see warnings); no caller scan was performed.";
            }
            else
            {
                summary = $"Block '{targetName}' (#{targetNumber}, {targetType}) is called by {callers.Count} block(s). ";
                summary += callers.Count == 0
                    ? "No in-project callers found — it may be called only from HMI/Startup or not used at all. Changing its interface is low-risk for other blocks."
                    : "See 'callers' for the list. Changing this block's interface (pins/number) will affect these callers — review them before editing.";
                if (iface == null) summary += " Interface could not be extracted (block may be inconsistent or know-how-protected).";
            }

            return new ResponseJsonReport
            {
                Ok = true,
                Message = summary,
                Data = data,
                Warnings = warnings.Count > 0 ? warnings.Select(w => w!.ToString()).ToArray() : null,
                Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true }
            };
            });
        }

        private static int SafeGetNumber(PlcBlock block)
        {
            try { return block.Number; }
            catch { return 0; }
        }

        // Parse <Interface><Sections><Section Name="..."><Member Name=".." Datatype=".."/></Section></Sections>
        // from the block's exported SimaticML. Namespace/version agnostic (LocalName).
        private JsonObject? ParseInterface(PlcBlock block, JsonArray warnings)
        {
            var tmpDir = Path.Combine(Path.GetTempPath(), "tia_iface_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tmpDir);
            try
            {
                string xmlPath = Path.Combine(tmpDir, block.Name + ".xml");
                try { block.Export(new FileInfo(xmlPath), ExportOptions.None); }
                catch (Exception ex) { warnings.Add($"Interface export failed for '{block.Name}': {ex.Message}"); return null; }
                XDocument doc;
                try { doc = XDocument.Load(xmlPath); }
                catch (Exception ex) { warnings.Add($"Interface parse failed for '{block.Name}': {ex.Message}"); return null; }

                var iface = new JsonObject();
                var sections = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Interface")?
                                  .Descendants().FirstOrDefault(e => e.Name.LocalName == "Sections");
                if (sections == null) return iface;
                foreach (var sec in sections.Elements().Where(e => e.Name.LocalName == "Section"))
                {
                    string secName = sec.Attribute("Name")?.Value ?? "Unknown";
                    var members = new JsonArray();
                    foreach (var m in sec.Elements().Where(e => e.Name.LocalName == "Member"))
                    {
                        string mName = m.Attribute("Name")?.Value ?? "";
                        if (mName.Length == 0) continue;
                        var mo = new JsonObject { ["name"] = mName };
                        string mType = m.Attribute("Datatype")?.Value ?? m.Attribute("DataType")?.Value ?? "";
                        if (mType.Length > 0) mo["datatype"] = mType;
                        members.Add(mo);
                    }
                    iface[secName] = members;
                }
                return iface;
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        // Detect whether an exported block's SimaticML contains a <Call> whose called
        // block name matches targetName. Covers <CalledBlock><Component Name="X"/> and
        // <CallStructure> shapes; tolerates instance-DB style ("X.DB" -> "X").
        private static bool BlockCalls(XDocument doc, string targetName)
        {
            var calls = doc.Descendants()
                .Where(e => e.Name.LocalName == "Call" || e.Name.LocalName == "CallStructure")
                .ToList();
            foreach (var call in calls)
            {
                foreach (var comp in call.Descendants().Where(e => e.Name.LocalName == "Component"))
                {
                    string name = comp.Attribute("Name")?.Value ?? "";
                    if (name.Length == 0) continue;
                    string baseName = name.Contains(".") ? name.Substring(name.LastIndexOf('.') + 1) : name;
                    if (baseName.Equals(targetName, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            // Fallback: a Component named exactly target sitting directly under Call/CalledBlock/CallStructure
            return doc.Descendants().Any(e =>
                e.Name.LocalName == "Component" &&
                (e.Attribute("Name")?.Value ?? "").Equals(targetName, StringComparison.OrdinalIgnoreCase) &&
                e.Parent != null && (e.Parent.Name.LocalName == "CalledBlock" ||
                                     e.Parent.Name.LocalName == "CallStructure" ||
                                     e.Parent.Name.LocalName == "Call"));
        }
    }
}
