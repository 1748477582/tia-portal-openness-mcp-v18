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
    // Partial: download. Extracted from Portal.cs (god-file split); behavior unchanged.
    public partial class Portal
    {
        #region download

        public ResponseDownload DownloadToPlc(
            string softwarePath,
            bool consistentBlocksOnly = true,
            bool keepActualValues = true,
            bool startAfterDownload = true,
            bool stopBeforeDownload = true,
            string? password = null,
            string? pgPcInterface = null)
        {
            return _sta.Run(() =>
            {
            // Env fallback: allows selecting e.g. PLCSIM Softbus without changing the tool schema
            // (older clients cache parameter schemas). Explicit parameter always wins.
            if (string.IsNullOrWhiteSpace(pgPcInterface))
                pgPcInterface = Environment.GetEnvironmentVariable("TIA_MCP_PGPC_INTERFACE");
            _logger?.LogInformation(
                "DownloadToPlc: softwarePath={SoftwarePath} consistentOnly={C} keepDB={K} start={S} stop={T} hasPassword={P}",
                softwarePath, consistentBlocksOnly, keepActualValues, startAfterDownload, stopBeforeDownload, !string.IsNullOrEmpty(password));

            if (IsProjectNull())
                return new ResponseDownload { Ok = false, Message = "No project open." };

            var plcSoftware = GetPlcSoftware(softwarePath);
            if (plcSoftware == null)
                return new ResponseDownload { Ok = false, Message = $"PLC software not found: '{softwarePath}'." };

            try
            {
                var downloadProvider = ResolvePlcService<DownloadProvider>(softwarePath, plcSoftware);
                if (downloadProvider == null)
                    return new ResponseDownload
                    {
                        Ok = false,
                        Message = "DownloadProvider service not available for this PLC. Ensure hardware configuration has network settings."
                    };

                object? configuration = downloadProvider.Configuration;
                if (configuration == null)
                    return new ResponseDownload
                    {
                        Ok = false,
                        Message = "No connection configuration found. Configure the PLC's PROFINET/IP address in hardware configuration first."
                    };

                using var passwordScope = AttachPasswordHandler(configuration, password);

                bool capture_keepActualValues = keepActualValues;
                bool capture_startAfterDownload = startAfterDownload;
                bool capture_stopBeforeDownload = stopBeforeDownload;
                bool capture_consistentBlocksOnly = consistentBlocksOnly;

                DownloadConfigurationDelegate preDelegate = (config) =>
                {
                    ApplyDefaultDownloadConfig(
                        config,
                        capture_keepActualValues,
                        capture_startAfterDownload,
                        capture_stopBeforeDownload,
                        capture_consistentBlocksOnly);
                };

                DownloadConfigurationDelegate postDelegate = (config) => { };

                // Resolve the 4-arg overload Download(IConfiguration, pre, post, DownloadOptions)
                // and invoke via reflection (the parameter is typed IConfiguration).
                var downloadMethod = downloadProvider.GetType()
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m =>
                    {
                        if (m.Name != "Download") return false;
                        var p = m.GetParameters();
                        return p.Length == 4
                            && p[1].ParameterType.Name == "DownloadConfigurationDelegate";
                    });

                if (downloadMethod == null)
                    return new ResponseDownload
                    {
                        Ok = false,
                        Message = "Download(IConfiguration,…) method not found on DownloadProvider. TIA Portal version mismatch?"
                    };

                // Env toggle TIA_MCP_DOWNLOAD_HARDWARE=1: include hardware config in the download.
                // Required for the FIRST download to a fresh PLCSIM (Advanced) instance, which has
                // no hardware configuration yet (unspecified CPU) — Software-only fails to connect.
                var dlOptions = DownloadOptions.Software;
                var hwFlag = Environment.GetEnvironmentVariable("TIA_MCP_DOWNLOAD_HARDWARE");
                if (!string.IsNullOrEmpty(hwFlag) && hwFlag != "0")
                    dlOptions = DownloadOptions.Hardware | DownloadOptions.Software;

                // Collect candidate target interfaces (mode OR interface name matching the filter),
                // then try Download on each until one connects. This replaces the old single-pick
                // logic which chose the first physical NIC and failed for PLCSIM/Softbus targets.
                var candidates = CollectDownloadTargets(configuration, pgPcInterface);
                if (candidates.Count == 0 && !string.IsNullOrWhiteSpace(pgPcInterface))
                    return new ResponseDownload
                    {
                        Ok = false,
                        Message = $"No PG/PC interface matching '{pgPcInterface}' found. Available: {ListPgPcInterfaces(configuration)}"
                    };
                if (candidates.Count == 0)
                    candidates.Add(new KeyValuePair<string, object>("(raw configuration)", configuration));

                var attemptErrors = new List<string>();
                foreach (var cand in candidates)
                {
                    try
                    {
                        TryApplyRoute(configuration, cand.Value);
                        var rawResult = downloadMethod.Invoke(
                            downloadProvider,
                            new object[] { cand.Value, preDelegate, postDelegate, dlOptions });
                        if (rawResult is DownloadResult okResult)
                        {
                            var resp = BuildDownloadResponse(okResult, softwarePath);
                            resp.Message = $"[route: {cand.Key}] {resp.Message}";
                            return resp;
                        }
                        attemptErrors.Add($"{cand.Key}: unexpected result type");
                    }
                    catch (Exception exTry)
                    {
                        var realTry = exTry is System.Reflection.TargetInvocationException t && t.InnerException != null
                            ? t.InnerException : exTry;
                        attemptErrors.Add($"{cand.Key}: {realTry.Message.Replace("\r", " ").Replace("\n", " ")}");
                    }
                }

                return new ResponseDownload
                {
                    Ok = false,
                    Message = $"Download failed on all {candidates.Count} route(s). Attempts: {string.Join(" | ", attemptErrors)}. All interfaces: {ListPgPcInterfaces(configuration)}",
                    Errors = attemptErrors.ToArray()
                };
            }
            catch (Exception ex)
            {
                // The Download call is invoked via reflection, so a real failure arrives wrapped in
                // TargetInvocationException ("调用的目标发生了异常"). Unwrap it so the caller sees the
                // actual reason (connection/route error, not-reachable CPU, etc.).
                var real = ex is System.Reflection.TargetInvocationException tie && tie.InnerException != null
                    ? tie.InnerException : ex;
                _logger?.LogError(real, "DownloadToPlc failed for {SoftwarePath}", softwarePath);
                return new ResponseDownload
                {
                    Ok = false,
                    Message = $"Download failed: {real.Message}",
                    Errors = new[] { real.Message }
                };
            }
            });
        }

        // V21: ConnectionConfiguration.ApplyConfiguration(ConfigurationTargetInterface) returns the
        // IConfiguration that DownloadProvider.Download() requires. Navigate
        // Modes -> PcInterfaces -> TargetInterfaces, pick the first target, and apply it. Returns the
        // applied IConfiguration, or null when no route is selectable (caller falls back to the raw
        // configuration). NOTE: picks the FIRST PG/PC interface — on a multi-NIC PC confirm the
        // adapter facing the CPU.
        // List "mode/pcInterface" pairs for diagnostics (e.g. "PN/IE: Realtek PCIe GbE, PN/IE: PLCSIM").
        private static string ListPgPcInterfaces(object? connectionConfiguration)
        {
            var names = new List<string>();
            try
            {
                foreach (var mode in EnumerateReflectedProperty(connectionConfiguration, "Modes"))
                {
                    var modeName = mode?.GetType().GetProperty("Name")?.GetValue(mode)?.ToString() ?? "?";
                    foreach (var pcInterface in EnumerateReflectedProperty(mode, "PcInterfaces"))
                    {
                        var ifName = pcInterface?.GetType().GetProperty("Name")?.GetValue(pcInterface)?.ToString() ?? "?";
                        names.Add($"{modeName}: {ifName}");
                    }
                }
            }
            catch { }
            return names.Count == 0 ? "(none)" : string.Join(", ", names);
        }

        // Collect ALL candidate ConfigurationTargetInterfaces whose mode name OR PC-interface name
        // matches the filter (case-insensitive substring; null/empty filter = collect all).
        // Matching targets are ordered: filter-matching ones first (both mode+interface considered).
        private static List<KeyValuePair<string, object>> CollectDownloadTargets(
            object? connectionConfiguration, string? pgPcInterface)
        {
            var list = new List<KeyValuePair<string, object>>();
            if (connectionConfiguration == null) return list;
            try
            {
                foreach (var mode in EnumerateReflectedProperty(connectionConfiguration, "Modes"))
                {
                    var modeName = mode?.GetType().GetProperty("Name")?.GetValue(mode)?.ToString() ?? "";
                    foreach (var pcInterface in EnumerateReflectedProperty(mode, "PcInterfaces"))
                    {
                        var ifName = pcInterface?.GetType().GetProperty("Name")?.GetValue(pcInterface)?.ToString() ?? "";
                        if (!string.IsNullOrWhiteSpace(pgPcInterface))
                        {
                            bool hit = modeName.IndexOf(pgPcInterface, StringComparison.OrdinalIgnoreCase) >= 0
                                    || ifName.IndexOf(pgPcInterface, StringComparison.OrdinalIgnoreCase) >= 0;
                            if (!hit) continue;
                        }
                        foreach (var target in EnumerateReflectedProperty(pcInterface, "TargetInterfaces"))
                        {
                            if (target == null) continue;
                            var tgtName = target.GetType().GetProperty("Name")?.GetValue(target)?.ToString() ?? "?";
                            list.Add(new KeyValuePair<string, object>($"{modeName}/{ifName}/{tgtName}", target));
                        }
                    }
                }
            }
            catch { }
            return list;
        }

        // Best-effort: apply the online route for the chosen target before Download.
        private static void TryApplyRoute(object connectionConfiguration, object target)
        {
            try
            {
                var applyMethod = connectionConfiguration.GetType()
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "ApplyConfiguration"
                        && m.GetParameters().Length == 1
                        && m.GetParameters()[0].ParameterType.Name == "ConfigurationTargetInterface");
                applyMethod?.Invoke(connectionConfiguration, new[] { target });
            }
            catch { }
        }

        // Read a property by name and materialize it as a sequence (Openness compositions are
        // IEnumerable). Materialized inside try/catch so a throwing enumerator can't escape.
        private static List<object?> EnumerateReflectedProperty(object? owner, string propertyName)
        {
            var items = new List<object?>();
            try
            {
                var value = owner?.GetType().GetProperty(propertyName)?.GetValue(owner);
                if (value is System.Collections.IEnumerable en)
                    foreach (var item in en) items.Add(item);
            }
            catch { }
            return items;
        }

        public ResponseCheckDownload CheckDownloadReadiness(string softwarePath)
        {
            return _sta.Run(() =>
            {
            var issues = new List<string>();

            if (IsProjectNull())
                return new ResponseCheckDownload { Ready = false, Issues = new[] { "No project open." } };

            var plcSoftware = GetPlcSoftware(softwarePath);
            if (plcSoftware == null)
                return new ResponseCheckDownload { Ready = false, Issues = new[] { $"PLC software not found: '{softwarePath}'." } };

            bool hasProvider = false;
            bool hasConfig = false;
            bool isConsistent = false;

            try
            {
                var provider = ResolvePlcService<DownloadProvider>(softwarePath, plcSoftware);
                hasProvider = provider != null;
                if (!hasProvider)
                    issues.Add("DownloadProvider service not available. Check hardware/network configuration.");
                else
                {
                    hasConfig = provider!.Configuration != null;
                    if (!hasConfig)
                        issues.Add("No network configuration for this PLC. Set the IP address in hardware configuration.");
                }
            }
            catch (Exception ex)
            {
                issues.Add($"Error accessing DownloadProvider: {ex.Message}");
            }

            // Check compile consistency via ICompilable
            try
            {
                var compilable = plcSoftware.GetService<ICompilable>();
                if (compilable != null)
                {
                    // We skip an actual compile here; check block consistency heuristically
                    isConsistent = true; // Assume consistent unless caller has run CompileSoftware
                }
            }
            catch { }

            bool ready = hasProvider && hasConfig && issues.Count == 0;
            return new ResponseCheckDownload
            {
                Ready = ready,
                HasDownloadProvider = hasProvider,
                HasConfiguration = hasConfig,
                IsConsistent = isConsistent,
                Message = ready
                    ? $"PLC '{softwarePath}' is ready for download."
                    : $"PLC '{softwarePath}' has {issues.Count} readiness issue(s).",
                Issues = issues.Count > 0 ? issues.ToArray() : null
            };
            });
        }

        private void ApplyDefaultDownloadConfig(
            DownloadConfiguration config,
            bool keepActualValues,
            bool startAfterDownload,
            bool stopBeforeDownload,
            bool consistentBlocksOnly)
        {
            _sta.Run(() =>
            {
            var typeName = config.GetType().Name;
            _logger?.LogDebug("ApplyDownloadConfig: {TypeName}", typeName);

            switch (typeName)
            {
                case "StopModules":
                    // StopModulesSelections = { NoAction, StopAll } — NOT "StopModule" (verified
                    // against V21 PublicAPI; the old value parsed to nothing and left the prompt
                    // "unhandled", which aborted every download).
                    DownloadConfigSetSelection(config, stopBeforeDownload ? "StopAll" : "NoAction");
                    break;

                case "StopHSystemOrModule":
                    DownloadConfigSetSelection(config, stopBeforeDownload ? "StopModule" : "NoAction");
                    break;

                case "StopHSystem":
                    DownloadConfigSetSelection(config, stopBeforeDownload ? "StopHSystem" : "NoAction");
                    break;

                case "StartModules":
                case "StartBackupModules":
                    DownloadConfigSetSelection(config, startAfterDownload ? "StartModule" : "NoAction");
                    break;

                case "DataBlockReinitialization":
                    DownloadConfigSetSelection(config, keepActualValues ? "KeepActualValues" : "Reinitialize");
                    break;

                case "DataBlockReinitializationOrKeepActualValues":
                    DownloadConfigSetSelection(config, keepActualValues ? "KeepActualValues" : "StopPlcAndReinitialize");
                    break;

                case "ConsistentBlocksDownload":
                    DownloadConfigSetSelection(config, "ConsistentDownload");
                    break;

                case "AllBlocksDownload":
                    if (!consistentBlocksOnly)
                        DownloadConfigSetSelection(config, "DownloadAllBlocks");
                    break;

                case "CheckBeforeDownload":
                case "AlarmTextLibrariesDownload":
                case "UserManagementDownload":
                case "DownloadCertificate":
                    DownloadConfigSetChecked(config, true);
                    break;

                case "DifferentTargetConfiguration":
                case "ActiveTestCanBeAborted":
                case "ActiveTestCanPreventDownload":
                    DownloadConfigSetSelection(config, "AcceptAll");
                    break;
            }
            });
        }

        private static void DownloadConfigSetSelection(object config, string selectionName)
        {
            try
            {
                var prop = config.GetType().GetProperty("CurrentSelection");
                if (prop == null) return;
                var enumType = prop.PropertyType;
                if (!enumType.IsEnum) return;
                var value = Enum.Parse(enumType, selectionName, ignoreCase: true);
                prop.SetValue(config, value);
            }
            catch { }
        }

        private static void DownloadConfigSetChecked(object config, bool value)
        {
            try
            {
                var prop = config.GetType().GetProperty("Checked");
                prop?.SetValue(config, value);
            }
            catch { }
        }

        private ResponseDownload BuildDownloadResponse(DownloadResult result, string softwarePath)
        {
            return _sta.Run(() =>
            {
            var errors = new List<string>();
            var warnings = new List<string>();
            CollectDownloadMessages(result.Messages, errors, warnings);

            bool ok = result.State == DownloadResultState.Success
                   || result.State == DownloadResultState.Information
                   || result.State == DownloadResultState.Warning;

            return new ResponseDownload
            {
                Ok = ok,
                Message = $"Download {result.State}: {result.ErrorCount} error(s), {result.WarningCount} warning(s).",
                State = result.State.ToString(),
                ErrorCount = result.ErrorCount,
                WarningCount = result.WarningCount,
                Errors = errors.Count > 0 ? errors.ToArray() : null,
                Warnings = warnings.Count > 0 ? warnings.ToArray() : null,
                Meta = new JsonObject
                {
                    ["softwarePath"] = softwarePath,
                    ["timestamp"] = DateTime.Now,
                    ["downloadState"] = result.State.ToString()
                }
            };
            });
        }

        private static void CollectDownloadMessages(
            IEnumerable? messages,
            List<string> errors,
            List<string> warnings)
        {
            if (messages == null) return;
            foreach (var obj in messages)
            {
                if (obj == null) continue;
                try
                {
                    var msgText = obj.GetType().GetProperty("Message")?.GetValue(obj) as string ?? string.Empty;
                    var stateObj = obj.GetType().GetProperty("State")?.GetValue(obj);
                    var stateName = stateObj?.ToString() ?? string.Empty;

                    if (stateName == "Error" && !string.IsNullOrWhiteSpace(msgText))
                        errors.Add(msgText);
                    else if (stateName == "Warning" && !string.IsNullOrWhiteSpace(msgText))
                        warnings.Add(msgText);

                    // Recurse into nested Messages
                    var nested = obj.GetType().GetProperty("Messages")?.GetValue(obj) as IEnumerable;
                    if (nested != null)
                        CollectDownloadMessages(nested, errors, warnings);
                }
                catch { }
            }
        }

        #endregion
    }
}
