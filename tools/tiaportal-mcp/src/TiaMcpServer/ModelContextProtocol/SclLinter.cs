using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TiaMcpServer.ModelContextProtocol
{
    /// <summary>
    /// Severity of a SCL lint finding.
    /// </summary>
    public enum SclLintSeverity
    {
        Warning,
        Error
    }

    /// <summary>
    /// A single static pre-check finding for SCL source text.
    /// Line / Column are 1-based, matching what a human sees in the editor.
    /// </summary>
    public class SclLintFinding
    {
        public int Line { get; set; }
        public int Column { get; set; }
        public string Code { get; set; } = "";
        public string Title { get; set; } = "";
        public string Message { get; set; } = "";
        public string Suggestion { get; set; } = "";
        public SclLintSeverity Severity { get; set; } = SclLintSeverity.Warning;
    }

    /// <summary>
    /// Offline, dependency-free static pre-check for SCL source text.
    /// Catches four common compile-breakers BEFORE "Generate blocks from source":
    ///   SCL001 - duplicate formal parameter in a call
    ///   SCL002 - invalid "DB".DB(...) access syntax
    ///   SCL003 - FB multi-instance declared inside an OB
    ///   SCL004 - HW_*/PORT/CONN_* interface type declared in VAR_TEMP
    /// Non-blocking: it only reports, it never modifies anything.
    /// </summary>
    public static class SclLinter
    {
        // Hardware / interface types that are illegal inside VAR_TEMP (must be static/interface or in a DB).
        private static readonly HashSet<string> HardwareTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "PORT",
            "HW_ANY", "HW_DEVICE", "HW_IO", "HW_SUBMODULE", "HW_MODULE", "HW_INTERFACE",
            "HW_IEPORT", "HW_DPMASTER", "HW_DPINTERFACE", "HW_PBUS", "HW_PNETIO",
            "HW_PROFINETIO", "HW_PNINTERFACE", "HW_GPINTERFACE",
            "CONN_OUC", "CONN_PTP", "CONN_PRG", "CONN_RFID", "CONN_WIFI", "CONN_MODBUS",
            "CONN_PNIO", "CONN_DPSLAVE", "CONN_IOD", "CONN_DPMASTER", "CONN_ASIO"
        };

        private static readonly Regex InvalidDbAccess =
            new Regex("\"[^\"\\n]*\"\\s*\\.\\s*DB\\s*\\(", RegexOptions.Compiled);

        public static IReadOnlyList<SclLintFinding> Lint(string sclContent)
        {
            var findings = new List<SclLintFinding>();
            if (string.IsNullOrEmpty(sclContent))
                return findings;

            // Strip comments (keep newlines so line numbers stay aligned with the original).
            string stripped = StripComments(sclContent);

            CheckDuplicateFormals(stripped, findings);
            CheckInvalidDbAccess(stripped, findings);
            CheckObAndHardwareSections(stripped, findings);

            return findings;
        }

        public static JsonArray ToJson(IReadOnlyList<SclLintFinding> findings)
        {
            var arr = new JsonArray();
            foreach (var f in findings)
            {
                arr.Add(new JsonObject
                {
                    ["line"] = f.Line,
                    ["column"] = f.Column,
                    ["code"] = f.Code,
                    ["title"] = f.Title,
                    ["message"] = f.Message,
                    ["suggestion"] = f.Suggestion,
                    ["severity"] = f.Severity.ToString()
                });
            }
            return arr;
        }

        public static string FormatText(IReadOnlyList<SclLintFinding> findings)
        {
            if (findings.Count == 0)
                return "SCL lint: clean - no issues found.";

            var sb = new StringBuilder();
            sb.AppendLine($"SCL lint: {findings.Count} issue(s):");
            foreach (var f in findings)
            {
                sb.AppendLine($"  [{f.Severity}] {f.Code} (line {f.Line}:{f.Column}): {f.Title} - {f.Message}");
                if (!string.IsNullOrEmpty(f.Suggestion))
                    sb.AppendLine($"      fix: {f.Suggestion}");
            }
            return sb.ToString();
        }

        // ---------- internal checks ----------

        // SCL001: duplicate formal parameter inside a single call.
        private static void CheckDuplicateFormals(string s, List<SclLintFinding> findings)
        {
            int depth = 0;
            // Stack of (openDepth, formals). formals = list of (name, index, depth).
            var callStack = new Stack<(int openDepth, List<(string name, int index, int depth)> formals)>();

            int n = s.Length;
            for (int i = 0; i < n; i++)
            {
                char c = s[i];
                if (c == '(')
                {
                    depth++;
                    if (IsCalleeBefore(s, i))
                    {
                        callStack.Push((depth, new List<(string, int, int)>()));
                    }
                }
                else if (c == ')')
                {
                    if (callStack.Count > 0 && callStack.Peek().openDepth == depth)
                    {
                        var ctx = callStack.Pop();
                        FlagDuplicateFormals(ctx.formals, s, findings);
                    }
                    if (depth > 0) depth--;
                }
                else if (c == '=' && i > 0 && s[i - 1] == ':')
                {
                    // found ":="
                    string name = ReadBackwardIdentifier(s, i - 1);
                    if (!string.IsNullOrEmpty(name) && callStack.Count > 0)
                    {
                        var top = callStack.Peek();
                        top.formals.Add((name, i, depth));
                    }
                }
            }
        }

        private static void FlagDuplicateFormals(List<(string name, int index, int depth)> formals, string s, List<SclLintFinding> findings)
        {
            // Only consider formals at the call's top level (depth == first formal's depth grouping by name).
            var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in formals)
            {
                if (seen.TryGetValue(f.name, out int firstIndex))
                {
                    findings.Add(new SclLintFinding
                    {
                        Line = LineFromIndex(s, f.index),
                        Column = ColumnFromIndex(s, f.index),
                        Code = "SCL001",
                        Title = "Duplicate formal parameter in call",
                        Message = $"Formal parameter '{f.name}' is assigned more than once in the same call. TIA Portal rejects duplicate formals at compile time.",
                        Suggestion = $"Remove the redundant '{f.name} :=' assignment (first occurrence at line {LineFromIndex(s, firstIndex)}).",
                        Severity = SclLintSeverity.Error
                    });
                }
                else
                {
                    seen[f.name] = f.index;
                }
            }
        }

        // SCL002: invalid "DB".DB(...) access.
        private static void CheckInvalidDbAccess(string s, List<SclLintFinding> findings)
        {
            foreach (Match m in InvalidDbAccess.Matches(s))
            {
                int idx = m.Index;
                findings.Add(new SclLintFinding
                {
                    Line = LineFromIndex(s, idx),
                    Column = ColumnFromIndex(s, idx),
                    Code = "SCL002",
                    Title = "Invalid \"DB\".DB(...) access syntax",
                    Message = "The pattern \"Name\".DB(...) is not valid SCL. Access a DB by its symbol/number directly: use \"DB_Name\".VarName, DBx.VarName, or DBx.DBX/DBW/DBD offsets.",
                    Suggestion = "Replace \"DB\".DB(...) with the correct DB access operator for TIA Portal SCL.",
                    Severity = SclLintSeverity.Warning
                });
            }
        }

        // SCL003 + SCL004: per-block / per-section declaration checks.
        private static void CheckObAndHardwareSections(string stripped, List<SclLintFinding> findings)
        {
            string[] lines = stripped.Split('\n');
            bool inOb = false;
            string? section = null; // current VAR_* section name, or null

            var blockHeader = new Regex(@"^(ORGANIZATION_BLOCK|OB|FUNCTION_BLOCK|FUNCTION|DATA_BLOCK)\b");
            var blockEnd = new Regex(@"^END_(ORGANIZATION_BLOCK|OB|FUNCTION_BLOCK|FUNCTION|DATA_BLOCK)\b");
            var sectionOpen = new Regex(@"^(VAR_TEMP|VAR_INPUT|VAR_OUTPUT|VAR_IN_OUT|VAR_CONSTANT|VAR_STAT|VAR_GLOBAL|VAR)\b");
            var sectionClose = new Regex(@"^END_VAR\b");
            var decl = new Regex(@"(""[^""]+""|#?[\w]+)\s*:\s*([A-Za-z_]\w*)");

            for (int ln = 0; ln < lines.Length; ln++)
            {
                string line = lines[ln].Trim();

                var bh = blockHeader.Match(line);
                if (bh.Success)
                {
                    inOb = bh.Value == "ORGANIZATION_BLOCK" || bh.Value == "OB";
                    section = null;
                    continue;
                }
                if (blockEnd.IsMatch(line))
                {
                    inOb = false;
                    section = null;
                    continue;
                }
                var so = sectionOpen.Match(line);
                if (so.Success)
                {
                    section = so.Value;
                    continue;
                }
                if (sectionClose.IsMatch(line))
                {
                    section = null;
                    continue;
                }

                if (section == null)
                    continue;

                foreach (Match m in decl.Matches(line))
                {
                    string nameTok = m.Groups[1].Value;
                    string typeTok = m.Groups[2].Value;

                    // SCL003: FB instance inside an OB.
                    if (inOb && Regex.IsMatch(typeTok, @"^FB", RegexOptions.IgnoreCase))
                    {
                        findings.Add(new SclLintFinding
                        {
                            Line = ln + 1,
                            Column = m.Index + 1,
                            Code = "SCL003",
                            Title = "FB instance declared inside an OB",
                            Message = $"An OB cannot contain a multi-instance FB declaration ('{nameTok} : {typeTok}'). OBs have no static interface for FB instances.",
                            Suggestion = "Declare the FB instance in an FB/FC VAR section, or reference it through a global DB / PLC tag table.",
                            Severity = SclLintSeverity.Error
                        });
                    }

                    // SCL004: hardware/interface type in VAR_TEMP.
                    if (section == "VAR_TEMP" && HardwareTypes.Contains(typeTok))
                    {
                        findings.Add(new SclLintFinding
                        {
                            Line = ln + 1,
                            Column = m.Index + 1,
                            Code = "SCL004",
                            Title = "Hardware/interface type declared in VAR_TEMP",
                            Message = $"'{typeTok}' cannot be a temporary (VAR_TEMP) variable. Interface/hardware types must be static or part of the block interface / a DB.",
                            Suggestion = "Move the declaration to VAR (or VAR_INPUT/OUTPUT/IN_OUT), or reference it via a global DB / PLC tag.",
                            Severity = SclLintSeverity.Error
                        });
                    }
                }
            }
        }

        // ---------- helpers ----------

        private static string StripComments(string src)
        {
            var sb = new StringBuilder(src.Length);
            int i = 0, n = src.Length;
            while (i < n)
            {
                char c = src[i];
                if (c == '/' && i + 1 < n && src[i + 1] == '/')
                {
                    while (i < n && src[i] != '\n') i++;
                    continue;
                }
                if (c == '(' && i + 1 < n && src[i + 1] == '*')
                {
                    i += 2;
                    while (i < n && !(src[i] == '*' && i + 1 < n && src[i + 1] == ')')) i++;
                    i += 2; // skip *)
                    continue;
                }
                if (c == '\'') // single-quoted string literal: collapse to '' so positions stay sane
                {
                    sb.Append("''");
                    i++;
                    while (i < n && src[i] != '\'') i++;
                    if (i < n) i++; // skip closing '
                    continue;
                }
                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }

        // True when the '(' at position parenIdx is a call: preceded (ignoring spaces) by an
        // identifier or a double-quoted symbolic name. Excludes control structures like IF/WHILE/FOR
        // only loosely, but those rarely contain ':=' formals so false positives are negligible.
        private static bool IsCalleeBefore(string s, int parenIdx)
        {
            int j = parenIdx - 1;
            while (j >= 0 && char.IsWhiteSpace(s[j])) j--;
            if (j < 0) return false;
            if (s[j] == '"')
            {
                // quoted symbolic name, e.g. "inst"(
                return true;
            }
            if (s[j] == '#' || char.IsLetter(s[j]) || s[j] == '_')
            {
                // identifier, e.g. FB1( or func(
                return true;
            }
            return false;
        }

        // Read the identifier immediately before pos (pos points at ':' of ':=').
        private static string ReadBackwardIdentifier(string s, int pos)
        {
            int j = pos - 1;
            while (j >= 0 && char.IsWhiteSpace(s[j])) j--;
            if (j < 0) return "";
            if (s[j] == '"')
            {
                int end = j;
                int start = j - 1;
                while (start >= 0 && s[start] != '"') start--;
                if (start < 0) return "";
                return s.Substring(start + 1, end - start - 1);
            }
            if (s[j] == '#' || char.IsLetter(s[j]) || s[j] == '_' || char.IsDigit(s[j]))
            {
                int end = j;
                int start = j;
                while (start >= 0 && (s[start] == '#' || char.IsLetterOrDigit(s[start]) || s[start] == '_'))
                    start--;
                return s.Substring(start + 1, end - start);
            }
            return "";
        }

        private static int LineFromIndex(string s, int index)
        {
            int line = 1;
            int limit = Math.Min(index, s.Length);
            for (int i = 0; i < limit; i++)
                if (s[i] == '\n') line++;
            return line;
        }

        private static int ColumnFromIndex(string s, int index)
        {
            int col = 0;
            int i = index;
            while (i >= 0 && i < s.Length && s[i] != '\n')
            {
                i--;
                col++;
            }
            return col;
        }
    }
}
