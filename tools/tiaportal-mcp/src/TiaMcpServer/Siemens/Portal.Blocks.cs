using Microsoft.Extensions.Logging;
using Siemens.Engineering;
using Siemens.Engineering.Cax;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.Connection;
using Siemens.Engineering.Download;
using Siemens.Engineering.Download.Configurations;
using Siemens.Engineering.Hmi;
using Siemens.Engineering.Online;
using Siemens.Engineering.Online.Configurations;
using Siemens.Engineering.SW.Alarm;
using Siemens.Engineering.SW.OpcUa;
#if !TIA_V18
using Siemens.Engineering.HmiUnified;
#endif
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.Multiuser;
using Siemens.Engineering.Safety;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Types;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Security;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens
{
    // Partial: blocks/types. Extracted from Portal.cs (god-file split); behavior unchanged.
    public partial class Portal
    {
        #region blocks/types

        public PlcBlock? GetBlock(string softwarePath, string blockPath)
        {
            _logger?.LogInformation($"Getting block by path: {blockPath}");

            if (IsProjectNull())
            {
                return null;
            }

            var softwareContainer = GetSoftwareContainer(softwarePath);
            if (softwareContainer?.Software is PlcSoftware plcSoftware)
            {
                var blockGroup = plcSoftware?.BlockGroup;

                if (blockGroup != null)
                {
                    var path = blockPath.Contains("/") ? blockPath.Substring(0, blockPath.LastIndexOf("/")) : string.Empty;
                    var regexName = blockPath.Contains("/") ? blockPath.Substring(blockPath.LastIndexOf("/") + 1) : blockPath;

                    PlcBlock? block = null;

                    var group = GetPlcBlockGroupByPath(softwarePath, path);
                    if (group != null)
                    {
                        if (regexName.IndexOfAny(_regexChars) >= 0)
                        {
                            try
                            {
                                var regex = new Regex(regexName, RegexOptions.IgnoreCase);
                                block = group.Blocks.FirstOrDefault(b => regex.IsMatch(b.Name)) as PlcBlock;
                            }
                            catch (Exception)
                            {
                                // Invalid regex, return null
                                return null;
                            }
                        }
                        else
                        {
                            block = group.Blocks.FirstOrDefault(b => b.Name.Equals(regexName, StringComparison.OrdinalIgnoreCase));
                        }

                        return block;
                    }
                }
            }

            return null;
        }

        public PlcType? GetType(string softwarePath, string typePath)
        {
            _logger?.LogInformation($"Getting type by path: {typePath}");

            if (IsProjectNull())
            {
                return null;
            }

            var softwareContainer = GetSoftwareContainer(softwarePath);
            if (softwareContainer?.Software is PlcSoftware plcSoftware)
            {
                var typeGroup = plcSoftware?.TypeGroup;

                if (typeGroup != null)
                {
                    var path = typePath.Contains("/") ? typePath.Substring(0, typePath.LastIndexOf("/")) : string.Empty;
                    var regexName = typePath.Contains("/") ? typePath.Substring(typePath.LastIndexOf("/") + 1) : typePath;

                    PlcType? type = null;

                    var group = GetPlcTypeGroupByPath(softwarePath, path);
                    if (group != null)
                    {
                        if (regexName.IndexOfAny(_regexChars) >= 0)
                        {
                            try
                            {
                                var regex = new Regex(regexName, RegexOptions.IgnoreCase);
                                type = group.Types.FirstOrDefault(t => regex.IsMatch(t.Name)) as PlcType;
                            }
                            catch (Exception)
                            {
                                // Invalid regex, return null
                                return null;
                            }
                        }
                        else
                        {
                            type = group.Types.FirstOrDefault(t => t.Name.Equals(regexName, StringComparison.OrdinalIgnoreCase));
                        }

                        return type;
                    }
                }
            }

            return null;
        }

        public string GetBlockPath(PlcBlock block)
        {
            return _sta.Run(() =>
            {
            if (block == null)
            {
                return string.Empty;
            }

            if (block.Parent is PlcBlockGroup parentGroup)
            {
                var groupPath = GetPlcBlockGroupPath(parentGroup);
                return string.IsNullOrEmpty(groupPath) ? block.Name : $"{groupPath}/{block.Name}";
            }

            return block.Name;
            });
        }

        public List<PlcBlock> GetBlocks(string softwarePath, string regexName = "")
        {
            return _sta.Run(() =>
            {
            _logger?.LogInformation("Getting blocks...");

            if (IsProjectNull())
            {
                return [];
            }

            var list = new List<PlcBlock>();

            try
            {
                var softwareContainer = GetSoftwareContainer(softwarePath);
                if (softwareContainer?.Software is PlcSoftware plcSoftware)
                {
                    var group = plcSoftware?.BlockGroup;

                    if (group != null)
                    {
                        GetBlocksRecursive(group, list, regexName);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error getting blocks");
            }

            return list;
            });
        }

        public PlcBlockGroup? GetBlockRootGroup(string softwarePath)
        {
            _logger?.LogInformation("Getting block root group...");

            if (IsProjectNull())
            {
                return null;
            }

            try
            {
                var softwareContainer = GetSoftwareContainer(softwarePath);
                if (softwareContainer?.Software is PlcSoftware plcSoftware)
                {
                    return plcSoftware.BlockGroup;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error getting block root group");
            }

            return null;
        }

        public List<PlcType> GetTypes(string softwarePath, string regexName = "")
        {
            return _sta.Run(() =>
            {
            _logger?.LogInformation("Getting types...");

            if (IsProjectNull())
            {
                return [];
            }

            var list = new List<PlcType>();

            try
            {
                var softwareContainer = GetSoftwareContainer(softwarePath);
                if (softwareContainer?.Software is PlcSoftware plcSoftware)
                {
                    var group = plcSoftware?.TypeGroup;

                    if (group != null)
                    {
                        GetTypesRecursive(group, list, regexName);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error getting user defined types");
            }

            return list;
            });
        }

        public PlcBlock? ExportBlock(string softwarePath, string blockPath, string exportPath, bool preservePath = false)
        {
            _logger?.LogInformation($"Exporting block by path: {blockPath}");

            try
            {
                if (IsProjectNull())
                {
                    throw new PortalException(PortalErrorCode.InvalidState, "No project is open. If a project is already open in the TIA Portal UI, call AttachToOpenProject(projectName); otherwise call OpenProject(path) for a local .apXX project, or CreateProject to start a new one. (Connect is attempted automatically.)");
                }

                var block = Guard.RequireNotNull(GetBlock(softwarePath, blockPath), "Block", blockPath);

                if (preservePath)
                {
                    var groupPath = "";
                    if (block.Parent is PlcBlockGroup parentGroup)
                    {
                        groupPath = GetPlcBlockGroupPath(parentGroup);
                    }

                    exportPath = Path.Combine(exportPath, groupPath.Replace('/', '\\'), $"{block.Name}.xml");
                }
                else
                {
                    exportPath = Path.Combine(exportPath, $"{block.Name}.xml");
                }

                // TIA Portal never exports inconsistent blocks
                if (!block.IsConsistent)
                {
                    throw new PortalException(PortalErrorCode.InvalidState, "Block is inconsistent; TIA Portal does not export inconsistent blocks.");
                }

                if (File.Exists(exportPath))
                {
                    File.Delete(exportPath);
                }

                block.Export(new FileInfo(exportPath), ExportOptions.None);

                return block;
            }
            catch (Exception ex)
            {
                //If the exception is already a PortalException, use it; otherwise, wrap it in a new PortalException
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.ExportFailed, "Export failed", null, ex);

                pex.Data["softwarePath"] = softwarePath;
                pex.Data["blockPath"] = blockPath;
                pex.Data["exportPath"] = exportPath;

                _logger?.LogError(pex, "ExportBlock failed for {SoftwarePath} {BlockPath} -> {ExportPath}", softwarePath, blockPath, exportPath);
                throw pex;
            }
        }

        public PlcType? ExportType(string softwarePath, string typePath, string exportPath, bool preservePath = false)
        {
            _logger?.LogInformation($"Exporting type by path: {typePath}");

            try
            {
                if (IsProjectNull())
                {
                    throw new PortalException(PortalErrorCode.InvalidState, "No project is open. If a project is already open in the TIA Portal UI, call AttachToOpenProject(projectName); otherwise call OpenProject(path) for a local .apXX project, or CreateProject to start a new one. (Connect is attempted automatically.)");
                }

                var type = Guard.RequireNotNull(GetType(softwarePath, typePath), "Type", typePath);

                // TIA Portal never exports inconsistent types
                if (!type.IsConsistent)
                {
                    throw new PortalException(PortalErrorCode.InvalidState, "Type is inconsistent; TIA Portal does not export inconsistent types.");
                }

                if (preservePath)
                {
                    var groupPath = "";
                    if (type.Parent is PlcTypeGroup parentGroup)
                    {
                        groupPath = GetPlcTypeGroupPath(parentGroup);
                    }

                    exportPath = Path.Combine(exportPath, groupPath.Replace('/', '\\'), $"{type.Name}.xml");
                }
                else
                {
                    exportPath = Path.Combine(exportPath, $"{type.Name}.xml");
                }

                if (File.Exists(exportPath))
                {
                    File.Delete(exportPath);
                }

                type.Export(new FileInfo(exportPath), ExportOptions.None);

                return type;
            }
            catch (Exception ex)
            {
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.ExportFailed, "Export failed", null, ex);

                if (!pex.Data.Contains("softwarePath")) pex.Data["softwarePath"] = softwarePath;
                if (!pex.Data.Contains("typePath")) pex.Data["typePath"] = typePath;
                if (!pex.Data.Contains("exportPath")) pex.Data["exportPath"] = exportPath;

                _logger?.LogError(pex, "ExportType failed for {SoftwarePath} {TypePath} -> {ExportPath}", softwarePath, typePath, exportPath);
                throw pex;
            }
        }

        // Prepare a block/type XML file for Openness import. Two things are fixed on a temp
        // copy (the user's original file is never touched):
        //   1) Engineering version: Openness rejects an XML whose <Engineering version="Vxx"/>
        //      is newer than the connected portal ("The engineering version 'V21' ... is not
        //      supported."). The XML builders historically hardcode V21, so on a V20 portal
        //      every import fails. The header is rewritten to the detected major version.
        //   2) Encoding/BOM: block/type XML carrying Chinese comments must be UTF-8 *with BOM*
        //      or TIA imports the text as mojibake (中文乱码). Callers (and the model that wrote
        //      the file) frequently emit BOM-less UTF-8, so we always re-emit with a BOM here.
        private static string PrepareXmlForImport(string path)
        {
            try
            {
                var bytes = File.ReadAllBytes(path);
                bool hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
                var text = File.ReadAllText(path, Encoding.UTF8);

                var fixedText = text;
                int major = Engineering.TiaMajorVersion;
                if (major > 0)
                {
                    fixedText = Regex.Replace(text,
                        "<Engineering\\s+version=\"V\\d+\"\\s*/>",
                        $"<Engineering version=\"V{major}\" />");
                }

                // Already correct: version matches (or unknown) AND a BOM is present -> import as-is.
                if (fixedText == text && hasBom) return path;

                var tmp = Path.Combine(Path.GetTempPath(), "tia_mcp_import_" + Guid.NewGuid().ToString("N") + ".xml");
                File.WriteAllText(tmp, fixedText, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                return tmp;
            }
            catch
            {
                return path; // best effort; on any failure import the original file
            }
        }

        /// <summary>
        /// Extract the block name from a SimaticML XML file by locating the &lt;Name&gt;
        /// element inside the block's &lt;AttributeList&gt;. Falls back to the filename stem
        /// when XML parsing is unavailable or the element is not found.
        /// </summary>
        private static string? ExtractBlockNameFromImportXml(string xmlPath)
        {
            try
            {
                var xml = File.ReadAllText(xmlPath);
                var match = Regex.Match(xml,
                    @"<SW\.Blocks\.\w+[^>]*>.*?<AttributeList>.*?<Name>([^<]+)</Name>",
                    RegexOptions.Singleline);
                if (match.Success)
                {
                    var name = match.Groups[1].Value.Trim();
                    if (!string.IsNullOrEmpty(name)) return name;
                }
            }
            catch { }
            // Fallback: use filename without extension
            return Path.GetFileNameWithoutExtension(xmlPath);
        }

        /// <summary>
        /// Backup an existing block in the group before it gets overwritten by an import.
        /// Returns the path to the backup .xml file, or null if the block doesn't exist yet
        /// (new import, no rollback needed) or backup failed (best-effort, won't block import).
        /// </summary>
        private string? BackupBlockBeforeImport(PlcBlockGroup group, string blockName)
        {
            try
            {
                var existing = group.Blocks.Find(blockName);
                if (existing == null) return null;

                var backupPath = Path.Combine(Path.GetTempPath(),
                    $"tia_mcp_rollback_{Guid.NewGuid():N}_{blockName}.xml");
                existing.Export(new FileInfo(backupPath), ExportOptions.None);
                _logger?.LogInformation("Rollback: backed up block '{BlockName}' to {BackupPath}",
                    blockName, backupPath);
                return backupPath;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Rollback: failed to backup block '{BlockName}' (best-effort, import will proceed)",
                    blockName);
                return null;
            }
        }

        /// <summary>
        /// Restore a block from a backup file after a failed import.
        /// Deletes the partially-imported/failed block first, then re-imports the backup.
        /// The backup file is cleaned up regardless of success or failure.
        /// </summary>
        private void RestoreBlockFromBackup(PlcBlockGroup group, string backupPath, string blockName)
        {
            if (string.IsNullOrEmpty(backupPath) || !File.Exists(backupPath)) return;

            try
            {
                // Delete the failed/corrupted block from the failed import attempt
                var failed = group.Blocks.Find(blockName);
                if (failed != null)
                {
                    failed.Delete();
                    _logger?.LogWarning("Rollback: deleted partially-imported block '{BlockName}'",
                        blockName);
                }

                // Re-import the pre-import backup
                var backupFi = new FileInfo(backupPath);
                group.Blocks.Import(backupFi, ImportOptions.Override);
                _logger?.LogInformation("Rollback: restored block '{BlockName}' from {BackupPath}",
                    blockName, backupPath);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex,
                    "Rollback: CRITICAL - failed to restore block '{BlockName}' from {BackupPath}. " +
                    "The block may be in an inconsistent state!",
                    blockName, backupPath);
            }
            finally
            {
                try { File.Delete(backupPath); } catch { }
            }
        }

        public bool ImportBlock(string softwarePath, string groupPath, string importPath)
        {
            return _sta.Run(() =>
            {
            AuditLogger.Record("ImportBlock", $"softwarePath={softwarePath}, groupPath={groupPath}, importPath={importPath}");
            _logger?.LogInformation($"Importing block from path: {importPath}");

            // Extract block name early so we can rollback even if the import fails mid-way.
            var blockName = ExtractBlockNameFromImportXml(importPath);
            PlcBlockGroup? group = null;
            string? backupPath = null;

            try
            {
                if (IsProjectNull())
                    throw new PortalException(PortalErrorCode.InvalidState, "No project is open. If a project is already open in the TIA Portal UI, call AttachToOpenProject(projectName); otherwise call OpenProject(path) for a local .apXX project, or CreateProject to start a new one. (Connect is attempted automatically.)");

                if (!File.Exists(importPath))
                    throw new PortalException(PortalErrorCode.InvalidParams, $"Import file not found: {importPath}");
                var importFi = new FileInfo(importPath);
                if (importFi.Length > 10 * 1024 * 1024)
                    throw new PortalException(PortalErrorCode.InvalidParams, $"Import file too large ({importFi.Length} bytes, max 10 MB): {importPath}");

                var softwareContainer = GetSoftwareContainer(softwarePath);
                if (softwareContainer?.Software is not PlcSoftware plcSoftware)
                    throw new PortalException(PortalErrorCode.NotFound,
                        softwareContainer?.Software == null
                            ? $"Software container not found for path '{softwarePath}'"
                            : $"Software at '{softwarePath}' is not PlcSoftware (type={softwareContainer.Software.GetType().Name})");

                group = GetPlcBlockGroupByPath(softwarePath, groupPath);
                if (group == null)
                    throw new PortalException(PortalErrorCode.NotFound,
                        $"PLC block group not found for groupPath='{groupPath}'; use empty string for root program blocks");

                if (!new FileInfo(importPath).Exists)
                    throw new PortalException(PortalErrorCode.InvalidParams, $"Import file not found: {importPath}");

                // Snapshot existing block before overwriting (best-effort rollback safety net).
                if (!string.IsNullOrEmpty(blockName))
                    backupPath = BackupBlockBeforeImport(group, blockName!);

                var fileInfo = new FileInfo(PrepareXmlForImport(importPath));

                var imported = group.Blocks.Import(fileInfo, ImportOptions.Override);
                if (imported == null || imported.Count == 0)
                    throw new PortalException(PortalErrorCode.ImportFailed, "Blocks.Import returned an empty collection");

                return true;
            }
            catch (Exception ex)
            {
                // Attempt rollback: restore the pre-import backup if we have one.
                if (group != null && !string.IsNullOrEmpty(backupPath) && !string.IsNullOrEmpty(blockName))
                {
                    try { RestoreBlockFromBackup(group, backupPath!, blockName!); }
                    catch (Exception rollbackEx)
                    {
                        _logger?.LogError(rollbackEx, "Rollback failed for block '{BlockName}'; block may be in an inconsistent state", blockName);
                    }
                }

                // Surface the real Openness error to callers — without this the message
                // is just "Import failed" which is useless for diagnosing bad LAD/SCL XML.
                var inner = UnwrapImportError(ex);
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.ImportFailed, $"Import failed: {inner}", null, ex);
                pex.Data["softwarePath"] = softwarePath;
                pex.Data["groupPath"] = groupPath;
                pex.Data["importPath"] = importPath;
                string rollbackNote = !string.IsNullOrEmpty(backupPath) ? " (auto-rollback attempted)" : "";
                _logger?.LogError(pex, "ImportBlock failed for {SoftwarePath} group={GroupPath} file={ImportPath}: {Inner}{RollbackNote}", softwarePath, groupPath, importPath, inner, rollbackNote);
                throw pex;
            }
            });
        }

        /// <summary>
        /// Export a block to an XML file, then RE-WRITE that file as UTF-8 WITH BOM so that
        /// any downstream edit / re-import is guaranteed to be encoding-safe. This kills the
        /// classic "å˜é‡åˆ..." mojibake that appears when a UTF-8 file is read as GBK/Latin-1.
        /// The block is exported by Openness (which already emits UTF-8), and we normalize the
        /// on-disk bytes to UTF-8+BOM so external editors and re-import behave consistently.
        /// </summary>
        public string ExportBlockSourceUtf8(string softwarePath, string blockPath, string exportPath, bool preservePath = false)
        {
            return _sta.Run(() =>
            {
            try
            {
                if (IsProjectNull())
                    throw new PortalException(PortalErrorCode.InvalidState, "No project is open. If a project is already open in the TIA Portal UI, call AttachToOpenProject(projectName); otherwise call OpenProject(path) for a local .apXX project.");

                var block = Guard.RequireNotNull(GetBlock(softwarePath, blockPath), "Block", blockPath);

                if (!block.IsConsistent)
                    throw new PortalException(PortalErrorCode.InvalidState, "Block is inconsistent; TIA Portal does not export inconsistent blocks. Compile it first.");

                string finalPath;
                if (preservePath)
                {
                    var groupPath = block.Parent is PlcBlockGroup parentGroup ? GetPlcBlockGroupPath(parentGroup) : "";
                    finalPath = Path.Combine(exportPath, groupPath.Replace('/', '\\'), $"{block.Name}.xml");
                }
                else
                {
                    finalPath = Path.Combine(exportPath, $"{block.Name}.xml");
                }

                // Step 1: let Openness write the file.
                if (File.Exists(finalPath)) File.Delete(finalPath);
                block.Export(new FileInfo(finalPath), ExportOptions.None);

                // Step 2: normalize on-disk encoding to UTF-8 WITH BOM (idempotent if already so).
                NormalizeToUtf8Bom(finalPath);

                return finalPath;
            }
            catch (Exception ex)
            {
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.ExportFailed, "ExportBlockSourceUtf8 failed", null, ex);
                _logger?.LogError(pex, "ExportBlockSourceUtf8 failed for {SoftwarePath} {BlockPath} -> {ExportPath}", softwarePath, blockPath, exportPath);
                throw pex;
            }
            });
        }

        /// <summary>
        /// Re-import an (already edited) block source file back into the project, with encoding
        /// fully normalized first so Chinese comments never mojibake. Works for both SimaticML
        /// .xml and SCL/.s7dcl text files: the file is read as UTF-8, rewritten as UTF-8 WITH BOM,
        /// then fed through the existing ImportBlock pipeline (which also re-runs PrepareXmlForImport
        /// for version/BOM correction). This avoids the .scl "UTF-8 without BOM or it fails at line 0"
        /// trap by forcing BOM on.
        /// </summary>
        public bool RegenerateBlockFromSource(string softwarePath, string groupPath, string sourceFilePath)
        {
            return _sta.Run(() =>
            {
            AuditLogger.Record("RegenerateBlockFromSource", $"softwarePath={softwarePath}, groupPath={groupPath}, sourceFilePath={sourceFilePath}");
            try
            {
                if (IsProjectNull())
                    throw new PortalException(PortalErrorCode.InvalidState, "No project is open. If a project is already open in the TIA Portal UI, call AttachToOpenProject(projectName); otherwise call OpenProject(path) for a local .apXX project.");

                if (!File.Exists(sourceFilePath))
                    throw new PortalException(PortalErrorCode.InvalidParams, $"Source file not found: {sourceFilePath}");

                var srcFi = new FileInfo(sourceFilePath);
                if (srcFi.Length > 10 * 1024 * 1024)
                    throw new PortalException(PortalErrorCode.InvalidParams, $"Source file too large ({srcFi.Length} bytes, max 10 MB): {sourceFilePath}");

                // Normalize to UTF-8 WITH BOM in place (re-import reads this copy).
                NormalizeToUtf8Bom(sourceFilePath);

                // Delegate to the existing ImportBlock which already handles version/BOM fixing.
                return ImportBlock(softwarePath, groupPath, sourceFilePath);
            }
            catch (Exception ex)
            {
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.ImportFailed, "RegenerateBlockFromSource failed", null, ex);
                _logger?.LogError(pex, "RegenerateBlockFromSource failed for {SoftwarePath} group={GroupPath} file={ImportPath}", softwarePath, groupPath, sourceFilePath);
                throw pex;
            }
            });
        }

        /// <summary>
        /// Force a file's on-disk bytes to UTF-8 WITH BOM, regardless of how it was originally encoded.
        /// Reads with a tolerant decoder (falls back to the raw bytes if standard UTF-8 fails) so we
        /// never amplify mojibake — we only fix the byte-level encoding marker, not the characters.
        /// </summary>
        private static void NormalizeToUtf8Bom(string path)
        {
            // Decode with a fallback that keeps raw bytes visible if the text isn't valid UTF-8,
            // so we don't accidentally double-mangle an already-broken file.
            var bytes = File.ReadAllBytes(path);
            bool hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            if (hasBom) return; // already UTF-8+BOM, leave as-is

            string text;
            try
            {
                text = File.ReadAllText(path, new UTF8Encoding(false, false));
            }
            catch
            {
                // Not valid UTF-8 — read raw and re-emit; caller can inspect for mojibake separately.
                text = System.Text.Encoding.Default.GetString(bytes);
            }

            File.WriteAllText(path, text, new UTF8Encoding(true)); // UTF-8 WITH BOM
        }

        // Walk InnerException chain and concatenate type+message — Openness wraps the
        // useful XML-validation error several layers deep.
        private static string UnwrapImportError(Exception ex)
        {
            var parts = new List<string>();
            var cur = ex;
            int depth = 0;
            while (cur != null && depth < 6)
            {
                parts.Add($"{cur.GetType().Name}: {cur.Message}");
                cur = cur.InnerException;
                depth++;
            }
            return string.Join(" | ", parts);
        }

        public ResponseImportBatch ImportBlocksFromDirectory(string softwarePath, string groupPath, string dir, string regexName = "", bool overwrite = true)
        {
            return _sta.Run(() =>
            {
            AuditLogger.Record("ImportBlocksFromDirectory", $"softwarePath={softwarePath}, groupPath={groupPath}, dir={dir}, regexName={regexName}, overwrite={overwrite}");
            var imported = new List<string>();
            var failed = new List<ImportFailure>();

            try
            {
                if (IsProjectNull())
                {
                    failed.Add(new ImportFailure { Path = dir, Error = "Project is null" });
                    return new ResponseImportBatch { Imported = imported, Failed = failed };
                }

                if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                {
                    failed.Add(new ImportFailure { Path = dir, Error = "Directory not found" });
                    return new ResponseImportBatch { Imported = imported, Failed = failed };
                }

                var softwareContainer = GetSoftwareContainer(softwarePath);
                if (softwareContainer?.Software is not PlcSoftware)
                {
                    failed.Add(new ImportFailure { Path = dir, Error = $"PlcSoftware not found at '{softwarePath}'" });
                    return new ResponseImportBatch { Imported = imported, Failed = failed };
                }

                var group = GetPlcBlockGroupByPath(softwarePath, groupPath);
                if (group == null)
                {
                    failed.Add(new ImportFailure { Path = dir, Error = $"Block group not found (groupPath='{groupPath}')" });
                    return new ResponseImportBatch { Imported = imported, Failed = failed };
                }

                Regex? regex = null;
                if (!string.IsNullOrWhiteSpace(regexName))
                {
                    regex = new Regex(regexName, RegexOptions.IgnoreCase);
                }

                foreach (var file in Directory.EnumerateFiles(dir, "*.xml", SearchOption.TopDirectoryOnly))
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    if (regex != null && !regex.IsMatch(name))
                    {
                        continue;
                    }

                    string? backupPath = null;
                    try
                    {
                        if (!new FileInfo(file).Exists)
                        {
                            failed.Add(new ImportFailure { Path = file, Error = "File not found" });
                            continue;
                        }
                        var fi = new FileInfo(PrepareXmlForImport(file));

                        if (!overwrite)
                        {
                            try
                            {
                                var exists = group.Blocks.Find(name);
                                if (exists != null)
                                {
                                    failed.Add(new ImportFailure { Path = file, Error = $"Block '{name}' already exists (overwrite=false)" });
                                    continue;
                                }
                            }
                            catch
                            {
                                // best effort only; if Find fails, we still import with Override semantics below
                            }
                        }

                        // Snapshot existing block before overwriting (best-effort rollback).
                        backupPath = BackupBlockBeforeImport(group, name);

                        var list = group.Blocks.Import(fi, ImportOptions.Override);
                        if (list != null && list.Count > 0)
                        {
                            imported.AddRange(list.Select(b => b?.Name).Where(n => !string.IsNullOrWhiteSpace(n))!.Cast<string>());
                        }
                        else
                        {
                            imported.Add(name);
                        }
                    }
                    catch (Exception ex)
                    {
                        // Attempt rollback for this file.
                        try { RestoreBlockFromBackup(group, backupPath!, name); }
                        catch (Exception rollbackEx)
                        {
                            _logger?.LogError(rollbackEx, "Rollback failed for block '{BlockName}' during batch import", name);
                        }
                        failed.Add(new ImportFailure { Path = file, Error = ex.ToString() });
                    }
                }

                return new ResponseImportBatch { Imported = imported, Failed = failed };
            }
            catch (Exception ex)
            {
                failed.Add(new ImportFailure { Path = dir, Error = ex.ToString() });
                return new ResponseImportBatch { Imported = imported, Failed = failed };
            }
            });
        }

        public bool ImportType(string softwarePath, string groupPath, string importPath)
        {
            return _sta.Run(() =>
            {
            _logger?.LogInformation($"Importing type from path: {importPath}");

            try
            {
                if (IsProjectNull())
                    throw new PortalException(PortalErrorCode.InvalidState, "No project is open. If a project is already open in the TIA Portal UI, call AttachToOpenProject(projectName); otherwise call OpenProject(path) for a local .apXX project, or CreateProject to start a new one. (Connect is attempted automatically.)");

                var softwareContainer = GetSoftwareContainer(softwarePath);
                if (softwareContainer?.Software is not PlcSoftware plcSoftware)
                    throw new PortalException(PortalErrorCode.NotFound,
                        softwareContainer?.Software == null
                            ? $"Software container not found for path '{softwarePath}'"
                            : $"Software at '{softwarePath}' is not PlcSoftware (type={softwareContainer.Software.GetType().Name})");

                var group = GetPlcTypeGroupByPath(softwarePath, groupPath);
                if (group == null)
                    throw new PortalException(PortalErrorCode.NotFound,
                        $"PLC type group not found for groupPath='{groupPath}'; use empty string for root PLC data types");

                if (!new FileInfo(importPath).Exists)
                    throw new PortalException(PortalErrorCode.InvalidParams, $"Import file not found: {importPath}");
                var fileInfo = new FileInfo(PrepareXmlForImport(importPath));

                var imported = group.Types.Import(fileInfo, ImportOptions.Override);
                if (imported == null || imported.Count == 0)
                    throw new PortalException(PortalErrorCode.ImportFailed, "Types.Import returned an empty collection");

                return true;
            }
            catch (Exception ex)
            {
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.ImportFailed, "Import failed", null, ex);
                pex.Data["softwarePath"] = softwarePath;
                pex.Data["groupPath"] = groupPath;
                pex.Data["importPath"] = importPath;
                _logger?.LogError(pex, "ImportType failed for {SoftwarePath} group={GroupPath} file={ImportPath}", softwarePath, groupPath, importPath);
                throw pex;
            }
            });
        }

        public IEnumerable<PlcBlock>? ExportBlocks(string softwarePath, string exportPath, string regexName = "", bool preservePath = false)
        {
            _logger?.LogInformation("Exporting blocks...");

            if (IsProjectNull())
            {
                throw new PortalException(PortalErrorCode.InvalidState, "No project is open. If a project is already open in the TIA Portal UI, call AttachToOpenProject(projectName); otherwise call OpenProject(path) for a local .apXX project, or CreateProject to start a new one. (Connect is attempted automatically.)");
            }

            var exportList = new List<PlcBlock>();
            var failures = new List<string>();
            
            PlcBlock[] list;

            try
            {
                list = GetBlocks(softwarePath, regexName).ToArray();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to retrieve block list for {SoftwarePath}", softwarePath);
                return exportList;
            }

            for (int k = 0; k < list.Count(); k++)
            {
                var block = list[k];

                _logger?.LogDebug($"- Exporting block {k}/{list.Count()} : {block.Name}");

                string path;
                if (preservePath)
                {
                    var groupPath = "";
                    if (block.Parent is PlcBlockGroup parentGroup)
                    {
                        groupPath = GetPlcBlockGroupPath(parentGroup);
                    }
                    path = Path.Combine(exportPath, groupPath.Replace('/', '\\'), $"{block.Name}.xml");
                }
                else
                {
                    path = Path.Combine(exportPath, $"{block.Name}.xml");
                }

                try
                {
                    if (!block.IsConsistent)
                    {
                        _logger?.LogWarning("Skipping inconsistent block {Name}", block.Name);

                        continue;
                    }

                    var dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    if (File.Exists(path))
                    {
                        try { File.Delete(path); }
                        catch (Exception ioEx)
                        {
                            failures.Add($"{block.Name}: cannot delete existing file ({ioEx.Message})");
                            _logger?.LogError(ioEx, "Delete failed for {File}", path);

                            continue;
                        }
                    }

                    try
                    {
                        block.Export(new FileInfo(path), ExportOptions.None);
                    }
                    catch (LicenseNotFoundException licEx)
                    {
                        failures.Add($"{block.Name}: license not found ({licEx.Message})");
                        _logger?.LogError(licEx, "License issue exporting {Block}", block.Name);

                        continue;
                    }
                    catch (EngineeringTargetInvocationException engEx)
                    {
                        failures.Add($"{block.Name}: target invocation failed ({engEx.Message})");
                        _logger?.LogError(engEx, "TargetInvocationException exporting {Block}", block.Name);

                        continue;
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"{block.Name}: export failed ({ex.Message})");
                        _logger?.LogError(ex, "Export failed for {Block}", block.Name);

                        continue;
                    }

                    exportList.Add(block);
                }
                catch (Exception ex)
                {
                    // Catch only truly unexpected wrapper-level errors
                    failures.Add($"{block.Name}: unexpected exception ({ex.Message})");
                    _logger?.LogError(ex, "Unexpected error at block {Block}", block.Name);
                    // continue with next block
                }
            }

            if (failures.Count > 0)
            {
                _logger?.LogWarning($"ExportBlocks completed with {failures.Count} failures out of {list.Count()}. First failure: {failures[0]}");
                // Optionally: _logger?.LogDebug("All failures: {Failures}", string.Join("; ", failures));
            }
            else
            {
                _logger?.LogInformation($"ExportBlocks completed successfully. Exported {exportList.Count} blocks.");
            }

            return exportList;
        }

        public IEnumerable<PlcType>? ExportTypes(string softwarePath, string exportPath, string regexName = "", bool preservePath = false)
        {
            _logger?.LogInformation("Exporting types...");

            if (IsProjectNull())
            {
                return null;
            }

            var exportList = new List<PlcType>();
            var failures = new List<string>();

            PlcType[] list;

            try
            {
                list = GetTypes(softwarePath, regexName).ToArray();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to retrieve type list for {SoftwarePath}", softwarePath);
                return exportList;
            }

            for (int i = 0; i < list.Count(); i++)
            {
                var type = list[i];

                _logger?.LogDebug("- Exporting type {Index}/{Total} : {Name}", i, list.Count(), type.Name);

                string path;
                if (preservePath)
                {
                    var groupPath = "";
                    if (type.Parent is PlcTypeGroup parentGroup)
                    {
                        groupPath = GetPlcTypeGroupPath(parentGroup);
                    }
                    path = Path.Combine(exportPath, groupPath.Replace('/', '\\'), $"{type.Name}.xml");
                }
                else
                {
                    path = Path.Combine(exportPath, $"{type.Name}.xml");
                }

                try
                {
                    if (!type.IsConsistent)
                    {
                        _logger?.LogWarning("Skipping inconsistent type {Name}", type.Name);
                        continue;
                    }

                    var dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    if (File.Exists(path))
                    {
                        try
                        {
                            File.Delete(path);
                        }
                        catch (Exception ioEx)
                        {
                            failures.Add($"{type.Name}: cannot delete existing file ({ioEx.Message})");
                            _logger?.LogError(ioEx, "Delete failed for {File}", path);
                            continue;
                        }
                    }

                    try
                    {
                        type.Export(new FileInfo(path), ExportOptions.None);
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"{type.Name}: export failed ({ex.Message})");
                        _logger?.LogError(ex, "Export failed for type {Type}", type.Name);
                        continue;
                    }

                    exportList.Add(type);
                }
                catch (Exception ex)
                {
                    failures.Add($"{type.Name}: unexpected exception ({ex.Message})");
                    _logger?.LogError(ex, "Unexpected error at type {Type}", type.Name);
                }
            }

            if (failures.Count > 0)
            {
                _logger?.LogWarning($"ExportTypes completed with {failures.Count} failures out of {list.Count()}. First failure: {failures[0]}");
            }
            else
            {
                _logger?.LogInformation($"ExportTypes completed successfully. Exported {exportList.Count} types.");
            }

            return exportList;
        }

        public (string TempDir, List<string> Paths)? ExportBlockToTemp(string softwarePath, string blockPath, bool preservePath = false)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "TiaMcpServer_Export_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            var blk = ExportBlock(softwarePath, blockPath, tempDir, preservePath);
            if (blk == null) return null;

            var paths = Directory.GetFiles(tempDir, "*.xml", SearchOption.AllDirectories).ToList();
            return (tempDir, paths);
        }

        public (string TempDir, List<string> Paths)? ExportTypeToTemp(string softwarePath, string typePath, bool preservePath = false)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "TiaMcpServer_Export_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            var t = ExportType(softwarePath, typePath, tempDir, preservePath);
            if (t == null) return null;

            var paths = Directory.GetFiles(tempDir, "*.xml", SearchOption.AllDirectories).ToList();
            return (tempDir, paths);
        }

        public (string TempDir, List<string> Paths)? ExportBlocksToTemp(string softwarePath, string regexName = "", bool preservePath = false)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "TiaMcpServer_Export_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            var list = ExportBlocks(softwarePath, tempDir, regexName, preservePath);
            if (list == null) return null;

            var paths = Directory.GetFiles(tempDir, "*.xml", SearchOption.AllDirectories).ToList();
            return (tempDir, paths);
        }

        public (string TempDir, List<string> Paths)? ExportTypesToTemp(string softwarePath, string regexName = "", bool preservePath = false)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "TiaMcpServer_Export_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            var list = ExportTypes(softwarePath, tempDir, regexName, preservePath);
            if (list == null) return null;

            var paths = Directory.GetFiles(tempDir, "*.xml", SearchOption.AllDirectories).ToList();
            return (tempDir, paths);
        }
        

        public bool ExportAsDocuments(string softwarePath, string blockPath, string exportPath, bool preservePath = false)
        {
            return _sta.Run(() =>
            {
                        _logger?.LogInformation($"Exporting block as documents by path: {blockPath}");
                        var success = false;
                        try
                        {
                            if (IsProjectNull())
                            {
                                throw new PortalException(PortalErrorCode.InvalidState, "No project is open. If a project is already open in the TIA Portal UI, call AttachToOpenProject(projectName); otherwise call OpenProject(path) for a local .apXX project, or CreateProject to start a new one. (Connect is attempted automatically.)");
                            }

                            Capability.RequireSupported(TiaFeature.DocumentExport);


                            var softwareContainer = GetSoftwareContainer(softwarePath);
                            if (softwareContainer?.Software is PlcSoftware plcSoftware)
                            {
                                if (plcSoftware != null)
                                {
                                    // Export code blocks as documents
                                    // https://docs.tia.siemens.cloud/r/en-us/v20/creating-and-managing-blocks/exporting-and-importing-blocks-in-simatic-sd-format-s7-1200-s7-1500/exporting-and-importing-blocks-in-simatic-sd-format-s7-1200-s7-1500

                                    var groupPath = blockPath.Contains("/") ? blockPath.Substring(0, blockPath.LastIndexOf("/")) : string.Empty;
                                    var blockName = blockPath.Contains("/") ? blockPath.Substring(blockPath.LastIndexOf("/") + 1) : blockPath;

                                    var group = GetPlcBlockGroupByPath(softwarePath, groupPath);

                                    //group?.Blocks.ForEach(b => Console.WriteLine($"Block: {b.Name}, Type: {b.GetType().Name}"));

                                    // join exportPath and groupPath
                                    if (!Directory.Exists(exportPath))
                                    {
                                        Directory.CreateDirectory(exportPath);
                                    }

                                    if (preservePath && !string.IsNullOrEmpty(groupPath))
                                    {
                                        exportPath = Path.Combine(exportPath, groupPath);

                                        if (!Directory.Exists(exportPath))
                                        {
                                            Directory.CreateDirectory(exportPath);
                                        }
                                    }

                                    try
                                    {
                                        // delete files s7dcl/s7res if already exists
                                        var blockFiles7dclPath = Path.Combine(exportPath, $"{blockName}.s7dcl");
                                        if (File.Exists(blockFiles7dclPath))
                                        {
                                            File.Delete(blockFiles7dclPath);
                                        }
                                        var blockFiles7resPath = Path.Combine(exportPath, $"{blockName}.s7res");
                                        if (File.Exists(blockFiles7resPath))
                                        {
                                            File.Delete(blockFiles7resPath);
                                        }

            #if !TIA_V18
                                        var result = group?.Blocks.Find(blockName)?.ExportAsDocuments(new DirectoryInfo(exportPath), blockName);
                                        if (result != null && result.State == DocumentResultState.Success)
                                        {
                                            success = true;
                                        }
            #else
                                        throw new PortalException(PortalErrorCode.NotSupportedOnVersion, "ExportAsDocuments requires TIA Portal V20 or newer");
            #endif
                                    }
                                    catch (EngineeringNotSupportedException ex)
                                    {
                                        // The export or import of blocks with mixed programming languages is not possible
                                        throw new PortalException(PortalErrorCode.ExportFailed, $"EngineeringNotSupportedException at block '{blockName}'. {ex.Message}", null, ex);
                                    }
                                    catch (Exception ex)
                                    {
                                        throw new PortalException(PortalErrorCode.ExportFailed, $"Exception at block '{blockName}'. {ex.Message}", null, ex);
                                    }

                                }

                            }


                        }
                        catch (Exception ex)
                        {
                            var pex = ex as PortalException ?? new PortalException(PortalErrorCode.ExportFailed, "Export failed", null, ex);

                            pex.Data["softwarePath"] = softwarePath;
                            pex.Data["blockPath"] = blockPath;
                            pex.Data["exportPath"] = exportPath;

                            _logger?.LogError(pex, "ExportAsDocuments failed for {SoftwarePath} {BlockPath} -> {ExportPath}", softwarePath, blockPath, exportPath);
                            throw pex;
                        }
                        return success;
            });
        }

        // TIA portal crashes when exporting blocks as documents, :-(
        /// <summary>
        /// Per-block failure reasons from the most recent ExportBlocksAsDocuments call (empty on full success).
        /// The tool layer surfaces this so a "totalBlocks &gt; 0 but exportedBlocks == 0" no longer looks silent.
        /// </summary>
        public IReadOnlyList<string> LastExportAsDocumentsFailures { get; private set; } = new List<string>();

        public IEnumerable<PlcBlock>? ExportBlocksAsDocuments(string softwarePath, string exportPath, string regexName = "", bool preservePath = false)
        {
            _logger?.LogInformation("Exporting blocks as documents...");

            if (IsProjectNull())
            {
                return null;
            }

            if (Engineering.TiaMajorVersion < 20)
            {
                _logger?.LogWarning("ExportBlocksAsDocuments is only supported on TIA Portal V20 or newer");
                return null;
            }

            var exportList = new List<PlcBlock>();
            var failures = new List<string>();

            PlcBlock[] list;
            try
            {
                list = GetBlocks(softwarePath, regexName).ToArray();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, $"Failed to retrieve block list for {softwarePath}");
                return exportList;
            }

            for (int i = 0; i < list.Count(); i++)
            {
                var block = list[i];

                _logger?.LogDebug($"- Exporting block as document {i}/{list.Count()} : {block.Name}");

                // Skip inconsistent blocks (TIA generally won’t export them)
                if (!block.IsConsistent)
                {
                    _logger?.LogWarning($"Skipping inconsistent block {block.Name}");
                    continue;
                }

                // Determine base directory (preserve group path if requested)
                string targetDir = exportPath;
                if (preservePath && block.Parent is PlcBlockGroup parentGroup)
                {
                    var groupPath = GetPlcBlockGroupPath(parentGroup);
                    if (!string.IsNullOrWhiteSpace(groupPath))
                    {
                        targetDir = Path.Combine(exportPath, groupPath.Replace('/', '\\'));
                    }
                }

                try
                {
                    if (!Directory.Exists(targetDir))
                    {
                        Directory.CreateDirectory(targetDir);
                    }
                }
                catch (Exception ex)
                {
                    failures.Add($"{block.Name}: cannot create directory '{targetDir}' ({ex.Message})");
                    _logger?.LogError(ex, $"Directory creation failed for {targetDir}");
                    continue;
                }

                var fileDcl = Path.Combine(targetDir, $"{block.Name}.s7dcl");
                var fileRes = Path.Combine(targetDir, $"{block.Name}.s7res");

                // Clean previous artifacts
                foreach (var f in new[] { fileDcl, fileRes })
                {
                    try
                    {
                        if (File.Exists(f))
                        {
                            File.Delete(f);
                        }
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"{block.Name}: cannot delete existing '{Path.GetFileName(f)}' ({ex.Message})");
                        _logger?.LogError(ex, $"Failed deleting existing file {f}");
                        // Continue anyway; export might overwrite.
                    }
                }

                try
                {
#if !TIA_V18
                    DocumentExportResult? result = null;
                    try
                    {
                        result = block.ExportAsDocuments(new DirectoryInfo(targetDir), block.Name);
                    }
                    catch (EngineeringNotSupportedException ex)
                    {
                        failures.Add($"{block.Name}: not supported ({ex.Message})");
                        _logger?.LogWarning(ex, $"EngineeringNotSupported exporting {block.Name}");
                        continue;
                    }
                    catch (LicenseNotFoundException ex)
                    {
                        failures.Add($"{block.Name}: license not found ({ex.Message})");
                        _logger?.LogError(ex, $"License issue exporting {block.Name}");
                        continue;
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"{block.Name}: export threw ({ex.Message})");
                        _logger?.LogError(ex, $"ExportAsDocuments failed for {block.Name}");
                        continue;
                    }

                    if (result == null)
                    {
                        failures.Add($"{block.Name}: no result returned");
                        continue;
                    }

                    if (result.State == DocumentResultState.Success)
                    {
                        exportList.Add(block);
                    }
                    else
                    {
                        failures.Add($"{block.Name}: result state {result.State}");
                    }
#else
                    failures.Add($"{block.Name}: ExportAsDocuments requires TIA Portal V20 or newer");
                    continue;
#endif
                }
                catch (Exception ex)
                {
                    failures.Add($"{block.Name}: unexpected exception ({ex.Message})");
                    _logger?.LogError(ex, $"Unexpected wrapper error for {block.Name}");
                }
            }

            if (failures.Count > 0)
            {
                _logger?.LogWarning($"ExportBlocksAsDocuments completed with {failures.Count} failures out of {list.Count()}. First failure: {failures[0]}");
                // Optional verbose list:
                // _logger?.LogDebug("All failures: {Failures}", string.Join("; ", failures));
            }
            else
            {
                _logger?.LogInformation($"ExportBlocksAsDocuments completed successfully. Exported {exportList.Count} blocks.");
            }

            LastExportAsDocumentsFailures = failures;
            return exportList;
        }

        public bool ImportFromDocuments(string softwarePath, string groupPath, string importPath, string fileNameWithoutExtension, ImportDocumentOptions option)
        {
            return _sta.Run(() =>
            {
                        _logger?.LogInformation($"Importing block from documents: {fileNameWithoutExtension} in {importPath}");

                        if (IsProjectNull())
                        {
                            return false;
                        }

                        if (Engineering.TiaMajorVersion < 20)
                        {
                            _logger?.LogWarning("ImportFromDocuments is only supported on TIA Portal V20 or newer");
                            return false;
                        }

                        var softwareContainer = GetSoftwareContainer(softwarePath);
                        if (!(softwareContainer?.Software is PlcSoftware plcSoftware))
                        {
                            throw new PortalException(PortalErrorCode.NotFound, $"PLC software '{softwarePath}' not found. Use GetProjectTree for the exact PLC name.");
                        }

                        var dir = new DirectoryInfo(importPath);
                        if (!dir.Exists)
                        {
                            throw new PortalException(PortalErrorCode.InvalidParams, $"Import directory does not exist: {importPath}");
                        }
                        if (!File.Exists(Path.Combine(importPath, fileNameWithoutExtension + ".s7dcl")))
                        {
                            throw new PortalException(PortalErrorCode.InvalidParams, $"No '{fileNameWithoutExtension}.s7dcl' found under {importPath}. importPath is the DIRECTORY holding the .s7dcl/.s7res, and fileNameWithoutExtension omits the extension.");
                        }

                        // Resolve the target group. Empty path = root. A non-empty path that does NOT resolve is a
                        // caller error — DO NOT silently retarget root (that is how a nested-group import used to
                        // land the block at root and get AutoNumber-renumbered).
                        PlcBlockGroup targetGroup;
                        if (string.IsNullOrWhiteSpace(groupPath))
                        {
                            targetGroup = plcSoftware.BlockGroup;
                        }
                        else
                        {
                            targetGroup = GetPlcBlockGroupByPath(softwarePath, groupPath)
                                ?? throw new PortalException(PortalErrorCode.NotFound,
                                    $"Group path '{groupPath}' not found under PLC '{softwarePath}'. Use GetSoftwareTree for exact group names, or pass an empty groupPath to import at the root.");
                        }

                        // Capture the existing block's identity BEFORE import. Openness Override, when the block
                        // lives in a different group than the import target, deletes+recreates it (losing its
                        // number and original group). We restore the number afterwards so callers/instance DBs
                        // and the project tree stay stable.
                        var existing = FindBlockRecursive(plcSoftware.BlockGroup, fileNameWithoutExtension);
                        int? prevNumber = null;
                        bool prevAutoNumber = false;
                        try { if (existing != null) { prevNumber = existing.Number; prevAutoNumber = existing.AutoNumber; } } catch { }

                        DocumentImportResult? result;
                        try
                        {
            #if !TIA_V18
                            result = targetGroup.Blocks.ImportFromDocuments(dir, fileNameWithoutExtension, option);
            #else
                            result = null;
                            throw new PortalException(PortalErrorCode.NotSupportedOnVersion, "ImportFromDocuments requires TIA Portal V20 or newer");
            #endif
                        }
                        catch (EngineeringNotSupportedException ex)
                        {
                            throw new PortalException(PortalErrorCode.NotSupportedOnVersion, $"ImportFromDocuments not supported for '{fileNameWithoutExtension}': {ex.Message}", null, ex);
                        }
                        catch (EngineeringTargetInvocationException ex)
                        {
                            throw new PortalException(PortalErrorCode.ImportFailed, $"ImportFromDocuments failed for '{fileNameWithoutExtension}' into group '{(string.IsNullOrWhiteSpace(groupPath) ? "<root>" : groupPath)}': {ex.Message}. Check the .s7dcl syntax (types/attributes) and that .s7res matches the S7_MLC ids.", null, ex);
                        }
                        catch (Exception ex)
                        {
                            throw new PortalException(PortalErrorCode.ImportFailed, $"ImportFromDocuments failed for '{fileNameWithoutExtension}' into group '{(string.IsNullOrWhiteSpace(groupPath) ? "<root>" : groupPath)}': {ex.Message}", null, ex);
                        }

                        if (result == null || result.State != DocumentResultState.Success)
                        {
                            throw new PortalException(PortalErrorCode.ImportFailed,
                                $"ImportFromDocuments returned state '{result?.State.ToString() ?? "null"}' for '{fileNameWithoutExtension}'. The document set was not imported.");
                        }

                        // Restore the original block number if Override renumbered it (symbolic/optimized blocks
                        // are addressed by name, so this is cosmetic-but-important for a stable, diffable project).
                        if (prevNumber.HasValue)
                        {
                            var imported = FindBlockRecursive(plcSoftware.BlockGroup, fileNameWithoutExtension);
                            if (imported != null)
                            {
                                try
                                {
                                    if (imported.Number != prevNumber.Value)
                                    {
                                        imported.AutoNumber = false;
                                        imported.Number = prevNumber.Value;
                                    }
                                    else
                                    {
                                        imported.AutoNumber = prevAutoNumber;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    _logger?.LogWarning(ex, $"Could not restore block number {prevNumber} for {fileNameWithoutExtension}");
                                }
                            }
                        }
                        return true;
            });
        }

        /// <summary>Depth-first search for a block by exact name across all nested block groups.</summary>
        private PlcBlock? FindBlockRecursive(PlcBlockGroup group, string blockName)
        {
            if (group == null)
            {
                return null;
            }
            var here = group.Blocks.Find(blockName);
            if (here != null)
            {
                return here;
            }
            foreach (var sub in group.Groups)
            {
                var found = FindBlockRecursive(sub, blockName);
                if (found != null)
                {
                    return found;
                }
            }
            return null;
        }

        public IEnumerable<PlcBlock>? ImportBlocksFromDocuments(string softwarePath, string groupPath, string importPath, string regexName, ImportDocumentOptions option, bool preservePath = false)
        {
            _logger?.LogInformation($"Importing blocks from documents in {importPath} with regex '{regexName}'");

            if (IsProjectNull())
            {
                return null;
            }

            if (Engineering.TiaMajorVersion < 20)
            {
                _logger?.LogWarning("ImportBlocksFromDocuments is only supported on TIA Portal V20 or newer");
                return null;
            }

            var imported = new List<PlcBlock>();

            try
            {
                var softwareContainer = GetSoftwareContainer(softwarePath);
                if (softwareContainer?.Software is PlcSoftware plcSoftware)
                {
                    var group = GetPlcBlockGroupByPath(softwarePath, groupPath);
                    var dir = new DirectoryInfo(importPath);
                    if (!dir.Exists)
                    {
                        _logger?.LogWarning($"Import directory does not exist: {importPath}");
                        return imported;
                    }

                    var rx = string.IsNullOrWhiteSpace(regexName)
                        ? null
                        : new Regex(regexName, RegexOptions.Compiled);

                    // Consider .s7dcl as the primary index; .s7res is optional supplemental
                    var files = dir.GetFiles("*.s7dcl", SearchOption.TopDirectoryOnly);
                    foreach (var file in files)
                    {
                        var name = Path.GetFileNameWithoutExtension(file.Name);
                        if (rx != null && !rx.IsMatch(name))
                        {
                            continue;
                        }

                        try
                        {
#if !TIA_V18
                            var result = (group != null)
                                ? group.Blocks.ImportFromDocuments(dir, name, option)
                                : plcSoftware.BlockGroup.Blocks.ImportFromDocuments(dir, name, option);

                            if (result != null && result.State == DocumentResultState.Success && result.ImportedPlcBlocks != null)
                            {
                                foreach (var blk in result.ImportedPlcBlocks)
                                {
                                    if (blk != null)
                                    {
                                        imported.Add(blk);
                                    }
                                }
                            }
#else
                            _logger?.LogWarning("ImportFromDocuments requires TIA Portal V20 or newer; skipping '{Name}'", name);
#endif
                        }
                        catch (EngineeringNotSupportedException ex)
                        {
                            _logger?.LogWarning(ex, "Skipping '{Name}': not supported (likely mixed languages)", name);
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogWarning(ex, "Skipping '{Name}' due to import error", name);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error importing blocks from documents");
            }

            return imported;
        }

        #endregion
    }
}
