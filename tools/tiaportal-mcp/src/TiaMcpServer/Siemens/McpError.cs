using System;
using System.Linq;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;

namespace TiaMcpServer.Siemens
{
    /// <summary>
    /// Unified translator from backend/runtime exceptions to the MCP error contract.
    /// This is the single choke-point that replaces the ~150 scattered
    /// <c>catch (Exception ex) when (ex is not McpException) { throw new McpException(msg, ex, McpErrorCode.InternalError); }</c>
    /// boilerplate sites.
    ///
    /// What it does that the old boilerplate did NOT:
    ///  1. Maps <see cref="PortalException"/>'s fine-grained <see cref="PortalErrorCode"/> onto a specific
    ///     <see cref="McpErrorCode"/> (NotFound/InvalidParams/InvalidState/NotSupportedOnVersion → InvalidParams;
    ///     the rest → InternalError) instead of always flattening to InternalError.
    ///  2. Surfaces the structured recovery hint (<see cref="TiaMcpServer.ModelContextProtocol.McpHints.Recovery"/>)
    ///     so an AI driver is told WHAT TO DO, not just given a raw error.
    ///  3. Appends "Did you mean: …" candidates carried on <see cref="PortalException.Candidates"/>.
    ///  4. Logs at the correct level (Warning for expected PortalExceptions, Error for unexpected failures).
    /// A pre-structured <see cref="McpException"/> passes through untouched, so callers that already build
    /// a specific error are preserved.
    /// </summary>
    internal static class McpError
    {
        /// <summary>
        /// Wraps an already-formatted tool error message with the proper <see cref="McpErrorCode"/>
        /// and structured recovery hint.
        /// Use inside a catch: <c>throw McpError.WithRecovery(ex, $"original message {ex.Message}");</c>
        /// </summary>
        public static McpException WithRecovery(Exception ex, string rawMessage, ILogger? logger = null)
        {
            if (ex is McpException mex) return mex; // already structured — preserve verbatim

            var portalEx = ex as PortalException;
            McpErrorCode code = portalEx != null ? Map(portalEx.Code) : McpErrorCode.InternalError;

            string candidates = string.Empty;
            if (portalEx?.Candidates != null && portalEx.Candidates.Any())
                candidates = "  Did you mean: " + string.Join(", ", portalEx.Candidates) + "?";

            // Avoid duplicating a recovery hint that the original message may already embed.
            string recovery = rawMessage.Contains("▶ RECOVERY")
                ? string.Empty
                : TiaMcpServer.ModelContextProtocol.McpHints.Recovery(ex);

            if (portalEx != null)
                logger?.LogWarning(ex, "[{PortalCode}->{McpCode}] {Op}: {Msg}", portalEx.Code, code, rawMessage, ex.Message);
            else
                logger?.LogError(ex, "[{McpCode}] {Op}: {Msg}", code, rawMessage, ex.Message);

            return new McpException($"[{code}] {rawMessage}{candidates}{recovery}", ex, code);
        }

        /// <summary>
        /// Maps the fine-grained <see cref="PortalErrorCode"/> onto the (intentionally limited) MCP SDK
        /// error codes. The SDK only exposes a couple of client-meaningful codes; we collapse the
        /// backend taxonomy onto them while still emitting the original <see cref="PortalErrorCode"/>
        /// name inside the error message for full fidelity.
        /// </summary>
        public static McpErrorCode Map(PortalErrorCode code) => code switch
        {
            PortalErrorCode.NotFound => McpErrorCode.InvalidParams,          // client supplied a wrong name/path
            PortalErrorCode.InvalidParams => McpErrorCode.InvalidParams,
            PortalErrorCode.InvalidState => McpErrorCode.InvalidParams,     // e.g. block not consistent, project not open
            PortalErrorCode.NotSupportedOnVersion => McpErrorCode.InvalidParams,
            PortalErrorCode.ExportFailed => McpErrorCode.InternalError,
            PortalErrorCode.ImportFailed => McpErrorCode.InternalError,
            PortalErrorCode.OpennessError => McpErrorCode.InternalError,
            _ => McpErrorCode.InternalError
        };
    }
}
