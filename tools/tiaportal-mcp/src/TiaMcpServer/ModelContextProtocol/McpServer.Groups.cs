using ModelContextProtocol;
using ModelContextProtocol.Server;
using System;
using System.ComponentModel;
using System.Text.Json.Nodes;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.ModelContextProtocol
{
    // Block-group operations. Kept in a partial file so the McpServer god-file is not
    // touched. Fills the long-standing gap "MCP cannot operate on block groups":
    //   - CreatePlcBlockGroup: native create of (nested) program-block user groups.
    //   - MoveBlockToGroup: organize an existing block into a group (export/delete/
    //     import round-trip, because Openness has no block-reparent API).
    public static partial class McpServer
    {
        [McpServerTool(Name = "CreatePlcBlockGroup"), Description("[L2][PLC-Software] Create a (nested) program-block group/folder, creating any missing parent groups along the path. Idempotent — returns ok if the group already exists. groupPath uses '/' separators relative to 'Program blocks' root, e.g. '01_手动控制/手动意图'. Requires: Connect + OpenProject. Use this to set up the 手动/手自动接口/自动 layer folders, then MoveBlockToGroup to organize blocks into them.")]
        public static ResponseMessage CreatePlcBlockGroup(
            [Description("softwarePath: PLC software path, e.g. 'PLC_1'")] string softwarePath,
            [Description("groupPath: '/'-separated group path under Program blocks, e.g. '01_手动控制/手动意图'")] string groupPath)
        {
            try
            {
                var group = Portal.EnsurePlcBlockGroup(softwarePath, groupPath, out var created);
                if (group == null)
                {
                    throw new McpException($"Could not create block group '{groupPath}': PlcSoftware not found at '{softwarePath}'", McpErrorCode.InvalidParams);
                }
                return new ResponseMessage
                {
                    Message = created.Count > 0
                        ? $"PLC block group '{groupPath}' ready (created: {string.Join(", ", created)})"
                        : $"PLC block group '{groupPath}' already existed",
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = true,
                        ["createdCount"] = created.Count
                    }
                };
            }
            catch (PortalException pex)
            {
                throw McpError.WithRecovery(pex, $"Failed creating PLC block group '{groupPath}' [{pex.Code}]: {pex.Message}");
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw McpError.WithRecovery(ex, $"Unexpected error creating PLC block group '{groupPath}': {ex.Message}");
            }
        }

        [McpServerTool(Name = "MoveBlockToGroup"), Description("[L2][PLC-Software] Move/organize an existing block into a program-block group (found anywhere by exact name). Openness cannot reparent a block, so this exports the block, deletes it, and re-imports it into the target group (SIMATIC SD .s7dcl preferred, SimaticML XML fallback for STL/mixed-language). The block number and references are preserved. autoCreateGroup creates the target group path if missing. Requires: Connect + OpenProject + block consistent (compile first). After moving, call CompileAndDiagnosePlc to confirm 0 errors. Note: avoid moving OBs with event bindings via this round-trip.")]
        public static ResponseMessage MoveBlockToGroup(
            [Description("softwarePath: PLC software path, e.g. 'PLC_1'")] string softwarePath,
            [Description("blockName: exact block name to move (searched across all groups)")] string blockName,
            [Description("targetGroupPath: '/'-separated destination group under Program blocks, e.g. '02_手自动接口'")] string targetGroupPath,
            [Description("autoCreateGroup: create the target group path if it does not exist (default true)")] bool autoCreateGroup = true)
        {
            try
            {
                var summary = Portal.MoveBlockToGroup(softwarePath, blockName, targetGroupPath, autoCreateGroup);
                return new ResponseMessage
                {
                    Message = summary,
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true }
                };
            }
            catch (PortalException pex)
            {
                throw McpError.WithRecovery(pex, $"Failed moving block '{blockName}' to '{targetGroupPath}' [{pex.Code}]: {pex.Message}");
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw McpError.WithRecovery(ex, $"Unexpected error moving block '{blockName}' to '{targetGroupPath}': {ex.Message}");
            }
        }

        [McpServerTool(Name = "DeleteBlock"), Description("[L2][PLC-Software] Delete a PLC block by exact name (searched across all program-block groups). Idempotent for absent blocks: if the block is not found it reports not-found with success=true instead of erroring. Requires: Connect + OpenProject. After deleting, call CompileAndDiagnosePlc to confirm the project still compiles. Deletion is destructive and cannot be undone except by re-importing the block's exported source, so only delete blocks you own/created.")]
        public static ResponseMessage DeleteBlock(
            [Description("softwarePath: PLC software path, e.g. 'PLC_1'")] string softwarePath,
            [Description("blockName: exact block name to delete (searched across all groups)")] string blockName)
        {
            try
            {
                var deleted = Portal.DeleteBlock(softwarePath, blockName);
                return new ResponseMessage
                {
                    Message = deleted
                        ? $"Block '{blockName}' deleted from '{softwarePath}'"
                        : $"Block '{blockName}' was not found in '{softwarePath}' (nothing to delete)",
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true, ["deleted"] = deleted }
                };
            }
            catch (PortalException pex)
            {
                throw McpError.WithRecovery(pex, $"Failed deleting block '{blockName}' [{pex.Code}]: {pex.Message}");
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw McpError.WithRecovery(ex, $"Unexpected error deleting block '{blockName}': {ex.Message}{McpHints.Recovery(ex)}");
            }
        }

        [McpServerTool(Name = "SetBlockNumber"), Description("[L2][PLC-Software] Set the 'Number' (编号) attribute of a PLC block by exact name. The number must stay unique within the PLC software (pre-checked; a duplicate is rejected with a clear error). Returns the previous number. Requires: Connect + OpenProject. After changing, call CompileAndDiagnosePlc. WARNING: changing a block number can break cross-references if other code calls the block by (type, old number) — verify references afterwards.")]
        public static ResponseMessage SetBlockNumber(
            [Description("softwarePath: PLC software path, e.g. 'PLC_1'")] string softwarePath,
            [Description("blockName: exact block name whose number to change")] string blockName,
            [Description("number: new block number (int, must be unique within the PLC)")] int number)
        {
            try
            {
                var previous = Portal.SetBlockNumber(softwarePath, blockName, number);
                return new ResponseMessage
                {
                    Message = $"Block '{blockName}' number changed from {previous} to {number}",
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true, ["previousNumber"] = previous, ["newNumber"] = number }
                };
            }
            catch (PortalException pex)
            {
                throw McpError.WithRecovery(pex, $"Failed setting number on block '{blockName}' [{pex.Code}]: {pex.Message}");
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw McpError.WithRecovery(ex, $"Unexpected error setting number on block '{blockName}': {ex.Message}{McpHints.Recovery(ex)}");
            }
        }

        [McpServerTool(Name = "MoveBlocksToGroup"), Description("[L2][PLC-Software] Bulk-organize PLC blocks into a program-block group. Moves EVERY block matching the optional nameRegex / blockType filter (blockType e.g. 'FB','FC','DB','OB') into targetGroupPath. Openness cannot reparent a block, so each is exported, deleted, and re-imported into the target group (SIMATIC SD .s7dcl preferred, SimaticML XML fallback). Per-item failures are reported, not fatal. autoCreateGroup creates the target path if missing. Requires: Connect + OpenProject + blocks consistent. After moving, call CompileAndDiagnosePlc to confirm 0 errors.")]
        public static ResponseMessage MoveBlocksToGroup(
            [Description("softwarePath: PLC software path, e.g. 'PLC_1'")] string softwarePath,
            [Description("targetGroupPath: '/' separated destination group under Program blocks, e.g. '02_手自动接口'")] string targetGroupPath,
            [Description("nameRegex: optional case-insensitive regex to filter block names (empty = all)")] string nameRegex = "",
            [Description("blockType: optional case-insensitive substring of the block's class name to filter, e.g. 'FB' (empty = all)")] string blockType = "",
            [Description("autoCreateGroup: create the target group path if it does not exist (default true)")] bool autoCreateGroup = true)
        {
            try
            {
                var summary = Portal.MoveBlocksToGroup(softwarePath, targetGroupPath, nameRegex, blockType, autoCreateGroup);
                return new ResponseMessage
                {
                    Message = summary,
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true }
                };
            }
            catch (PortalException pex)
            {
                throw McpError.WithRecovery(pex, $"Failed moving blocks to '{targetGroupPath}' [{pex.Code}]: {pex.Message}");
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw McpError.WithRecovery(ex, $"Unexpected error moving blocks to '{targetGroupPath}': {ex.Message}{McpHints.Recovery(ex)}");
            }
        }

        [McpServerTool(Name = "AutoClassifyBlocks"), Description("[L2][PLC-Software] Auto-organize ALL PLC blocks into subtype folders (FB/FC/DB/OB/Other). Each block is routed to a folder matching its subtype. If your project already has subtype folders with custom names, pass folderMappingJson e.g. {\"FB\":\"功能块\",\"FC\":\"函数\",\"DB\":\"数据块\",\"OB\":\"组织块\"} to map subtype->folder. Folders are created when missing (autoCreate=true). Uses the same export/delete/import round-trip; per-item failures reported, not fatal. Requires: Connect + OpenProject. After, run CompileAndDiagnosePlc.")]
        public static ResponseMessage AutoClassifyBlocks(
            [Description("softwarePath: PLC software path, e.g. 'PLC_1'")] string softwarePath,
            [Description("folderMappingJson: optional JSON object mapping subtype -> folder name, e.g. \"{\\\"FB\\\":\\\"功能块\\\",\\\"FC\\\":\\\"函数\\\"}\". Empty = use subtype abbreviation as folder name")] string folderMappingJson = "",
            [Description("autoCreate: create the subtype folders if they do not exist (default true)")] bool autoCreate = true)
        {
            try
            {
                var summary = Portal.AutoClassifyBlocks(softwarePath, folderMappingJson, autoCreate);
                return new ResponseMessage
                {
                    Message = summary,
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true }
                };
            }
            catch (PortalException pex)
            {
                throw McpError.WithRecovery(pex, $"Failed auto-classifying blocks [{pex.Code}]: {pex.Message}");
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw McpError.WithRecovery(ex, $"Unexpected error auto-classifying blocks: {ex.Message}{McpHints.Recovery(ex)}");
            }
        }
    }
}
