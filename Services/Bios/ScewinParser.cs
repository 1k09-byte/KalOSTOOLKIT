using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using KalOS.Models.Bios;

namespace KalOS.Services.Bios;

/// <summary>
/// Parses and serializes AMI SCEWIN export files. The format is a sequence
/// of key=value blocks separated by blank lines, one per setup question.
/// Round-trips cleanly: parse → mutate Value → serialize back.
/// </summary>
public static class ScewinParser
{
    // ── Parse ──────────────────────────────────────────────────────────

    /// <summary>
    /// Parses a SCEWIN export file into a list of <see cref="BiosSetting"/> records.
    /// </summary>
    public static IReadOnlyList<BiosSetting> Parse(string fileContent)
    {
        var settings = new List<BiosSetting>();
        var blocks = SplitBlocks(fileContent);

        foreach (var block in blocks)
        {
            var fields = ParseBlock(block);
            if (!fields.TryGetValue("Setup Question", out var name) || string.IsNullOrWhiteSpace(name))
                continue;

            fields.TryGetValue("Value", out var currentValue);
            fields.TryGetValue("BIOS Default", out var defaultValue);
            fields.TryGetValue("Help String", out var helpText);
            fields.TryGetValue("Token", out var token);
            fields.TryGetValue("Offset", out var offset);
            fields.TryGetValue("Width", out var widthStr);

            if (currentValue != null)
            {
                var commentIdx = currentValue.IndexOf("//");
                if (commentIdx >= 0) currentValue = currentValue[..commentIdx];
                currentValue = currentValue.Trim();
            }

            if (defaultValue != null)
            {
                var commentIdx = defaultValue.IndexOf("//");
                if (commentIdx >= 0) defaultValue = defaultValue[..commentIdx];
                
                var match = Regex.Match(defaultValue.Trim(), @"^\[[A-Fa-f0-9]+\](.*)");
                if (match.Success) defaultValue = match.Groups[1].Value;
                
                defaultValue = defaultValue.Trim();
            }

            // Parse options list and look for default/active values
            List<string>? options = null;
            string? activeOption = null;
            if (fields.TryGetValue("Options", out var optionsRaw) && !string.IsNullOrWhiteSpace(optionsRaw))
            {
                options = new List<string>();
                foreach (Match match in Regex.Matches(optionsRaw, @"(?<star>\*)?\s*\[(?<code>[0-9A-Fa-f]+)\]\s*(?<label>.*?)(?=\s*\*?\s*\[[0-9A-Fa-f]+\]|\s*//|$)", RegexOptions.Singleline))
                {
                    var label = match.Groups["label"].Value.Trim();
                    if (label.Length == 0) continue;
                    options.Add(label);
                    if (match.Groups["star"].Success)
                        activeOption = label;
                }
            }

            // Extract correct current value
            if (string.IsNullOrWhiteSpace(currentValue) && activeOption != null)
            {
                currentValue = activeOption;
            }

            if (string.IsNullOrWhiteSpace(currentValue) && !string.IsNullOrWhiteSpace(defaultValue))
            {
                currentValue = defaultValue;
                if (currentValue.StartsWith('*'))
                    currentValue = currentValue[1..].Trim();
            }

            // Determine data type
            string dataType = BiosDataType.String;
            if (options is { Count: > 0 })
                dataType = BiosDataType.Enum;
            else if (int.TryParse(currentValue, out _))
                dataType = BiosDataType.Integer;

            // Determine sensitivity (boot/security critical keywords)
            bool isSensitive = IsSensitiveSetting(name);

            // Build raw fields dictionary for round-trip fidelity
            var rawFields = new Dictionary<string, string>(fields);

            var description = helpText?.Trim();
            if (!string.IsNullOrWhiteSpace(defaultValue))
                description = $"{description}\nDefault: {defaultValue}";

            // Token may carry a trailing comment (e.g. "60 // Do NOT change this line")
            // from the export file. Strip it here so it doesn't surface in the UI.
            var cleanToken = token?.Trim();
            if (!string.IsNullOrWhiteSpace(cleanToken))
            {
                var tokenCommentIdx = cleanToken.IndexOf("//");
                if (tokenCommentIdx >= 0)
                    cleanToken = cleanToken[..tokenCommentIdx].Trim();
                if (!string.IsNullOrWhiteSpace(cleanToken))
                    description = $"{description}\nToken: {cleanToken}";
            }

            settings.Add(new BiosSetting(
                Name: name.Trim(),
                CurrentValue: currentValue ?? "",
                DataType: dataType,
                PossibleValues: options?.AsReadOnly(),
                MinValue: null,
                MaxValue: null,
                IsSensitive: isSensitive,
                IsReadOnly: false,
                RawFields: rawFields,
                Description: description?.Trim()));
        }

        return settings;
    }

    /// <summary>
    /// Parses a SCEWIN export file from disk.
    /// </summary>
    public static IReadOnlyList<BiosSetting> ParseFile(string filePath)
        => Parse(File.ReadAllText(filePath, Encoding.UTF8));

    // ── Serialize ──────────────────────────────────────────────────────

    /// <summary>
    /// Serializes a collection of settings back into SCEWIN import format.
    /// Only settings with changed values are included (minimal diff).
    /// </summary>
    public static string SerializeChanges(
        IReadOnlyList<BiosSetting> originalSettings,
        IEnumerable<BiosSettingChange> changes)
    {
        var changeMap = changes.ToDictionary(c => c.Name, c => c.NewValue, StringComparer.OrdinalIgnoreCase);
        var sb = new StringBuilder();

        foreach (var setting in originalSettings)
        {
            if (!changeMap.TryGetValue(setting.Name, out var newValue))
                continue;

            if (setting.RawFields is null) continue;

            // Write out the full block with the updated Value field
            foreach (var kvp in setting.RawFields)
            {
                string value = kvp.Key.Equals("Value", StringComparison.OrdinalIgnoreCase)
                    ? newValue
                    : kvp.Value;

                sb.AppendLine($"{kvp.Key,-22}= {value}");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// Serializes a full settings file (all settings), updating Value or Options fields
    /// from the change map where applicable.
    /// </summary>
    public static string SerializeFull(
        string originalFileContent,
        IEnumerable<BiosSettingChange> changes)
    {
        var changeMap = changes.ToDictionary(c => c.Name, c => c.NewValue, StringComparer.OrdinalIgnoreCase);
        var lines = originalFileContent.Split('\n');
        var sb = new StringBuilder();
        string? currentQuestion = null;

        var knownKeys = new[]
        {
            "Setup Question",
            "Help String",
            "Token",
            "Offset",
            "Width",
            "BIOS Default",
            "Value",
            "Options"
        };

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');
            
            // Check if this line starts a known key
            string? matchedKey = null;
            foreach (var k in knownKeys)
            {
                if (line.TrimStart().StartsWith(k, StringComparison.OrdinalIgnoreCase))
                {
                    var afterKey = line.TrimStart()[k.Length..].TrimStart();
                    if (afterKey.StartsWith('='))
                    {
                        matchedKey = k;
                        break;
                    }
                }
            }

            if (matchedKey == "Setup Question")
            {
                var idx = line.IndexOf('=');
                currentQuestion = line[(idx + 1)..].Trim();
                sb.AppendLine(line);
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                currentQuestion = null;
                sb.AppendLine(line);
                continue;
            }

            if (currentQuestion != null && changeMap.TryGetValue(currentQuestion, out var newVal))
            {
                // If it is the Value line, update it
                if (matchedKey == "Value")
                {
                    var idx = line.IndexOf('=');
                    sb.AppendLine($"{line[..idx]}= {newVal}");
                    continue;
                }

                // If it is an option line (starts with Options or has [xx] prefix)
                var optVal = CleanOption(line, out _, out _);
                if (!string.IsNullOrEmpty(optVal))
                {
                    // The UI sends human-readable labels (e.g. "IUSB4_GPP1"), while
                    // the raw line carries the AMI option code ("[01]IUSB4_GPP1").
                    // Accept both forms so the * marker always lands correctly.
                    var optLabel = StripOptionCode(optVal);
                    bool shouldBeStarred =
                        string.Equals(optVal, newVal, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(optLabel, newVal, StringComparison.OrdinalIgnoreCase);
                    var updatedLine = SetOptionStar(line, shouldBeStarred);
                    sb.AppendLine(updatedLine);
                    continue;
                }
            }

            sb.AppendLine(line);
        }

        return sb.ToString();
    }

    // ── Helpers ────────────────────────────────────────────────────────

    /// <summary>Strips a leading AMI option code ("[01]") leaving the human label.</summary>
    private static string StripOptionCode(string value)
    {
        var match = Regex.Match(value, @"^\[[0-9A-Fa-f]+\]\s*(.*)$");
        return match.Success ? match.Groups[1].Value.Trim() : value;
    }

    private static string CleanOption(string line, out bool wasStarred, out int openBracketIdx)
    {
        wasStarred = false;
        openBracketIdx = -1;

        // Strip comments
        var commentIdx = line.IndexOf("//");
        var content = commentIdx >= 0 ? line[..commentIdx] : line;

        // Find [
        openBracketIdx = content.IndexOf('[');
        if (openBracketIdx < 0) return "";

        // Check if "*" is present before "["
        var beforeBracket = content[..openBracketIdx];
        if (beforeBracket.Contains('*'))
            wasStarred = true;

        var optVal = content[openBracketIdx..].Trim();
        return optVal;
    }

    private static string SetOptionStar(string line, bool shouldBeStarred)
    {
        var optVal = CleanOption(line, out bool wasStarred, out int openBracketIdx);
        if (openBracketIdx < 0) return line;

        if (wasStarred == shouldBeStarred) return line;

        if (shouldBeStarred)
        {
            // Add star. Find first '*' or space before '[' to replace, or insert before '['
            var before = line[..openBracketIdx];
            var after = line[openBracketIdx..];
            
            // If before ends with space, replace it. Otherwise append '*'
            if (before.EndsWith(' '))
                return before[..^1] + "*" + after;
            return before + "*" + after;
        }
        else
        {
            // Remove star
            var before = line[..openBracketIdx];
            var after = line[openBracketIdx..];
            
            // Replace '*' with space
            var starIdx = before.LastIndexOf('*');
            if (starIdx >= 0)
            {
                var sb = new StringBuilder(before);
                sb[starIdx] = ' ';
                return sb.ToString() + after;
            }
            return line;
        }
    }

    private static List<string> SplitBlocks(string content)
    {
        var blocks = new List<string>();
        var current = new StringBuilder();

        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line))
            {
                if (current.Length > 0)
                {
                    blocks.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.AppendLine(line);
            }
        }

        if (current.Length > 0)
            blocks.Add(current.ToString());

        return blocks;
    }

    private static Dictionary<string, string> ParseBlock(string block)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? currentKey = null;

        var knownKeys = new[]
        {
            "Setup Question",
            "Help String",
            "Token",
            "Offset",
            "Width",
            "BIOS Default",
            "Value",
            "Options"
        };

        foreach (var rawLine in block.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            
            // Check if this line starts a known key
            string? matchedKey = null;
            foreach (var k in knownKeys)
            {
                if (line.TrimStart().StartsWith(k, StringComparison.OrdinalIgnoreCase))
                {
                    var afterKey = line.TrimStart()[k.Length..].TrimStart();
                    if (afterKey.StartsWith('='))
                    {
                        matchedKey = k;
                        break;
                    }
                }
            }

            if (matchedKey != null)
            {
                currentKey = matchedKey;
                var idx = line.IndexOf('=');
                var val = line[(idx + 1)..].Trim();
                fields[currentKey] = val;
            }
            else if (currentKey != null && !string.IsNullOrWhiteSpace(line))
            {
                // Append line as continuation of the current key (e.g. for options)
                fields[currentKey] = fields[currentKey] + "\n" + line.Trim();
            }
        }
        return fields;
    }

    private static bool IsSensitiveSetting(string name)
    {
        var upper = name.ToUpperInvariant();
        return upper.Contains("SECURE BOOT") ||
               upper.Contains("PASSWORD") ||
               upper.Contains("TPM") ||
               upper.Contains("BOOT ORDER") ||
               upper.Contains("CSM") ||
               upper.Contains("UEFI BOOT") ||
               upper.Contains("FAST BOOT") ||
               upper.Contains("OS TYPE");
    }
}
