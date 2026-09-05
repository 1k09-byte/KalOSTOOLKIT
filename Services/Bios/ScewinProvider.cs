using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KaliteKit.Models.Bios;

namespace KaliteKit.Services.Bios;

/// <summary>
/// IBiosProvider backed by SCEWIN_64.exe.  Delegates to <see cref="ScewinService"/>
/// for process management and <see cref="ScewinParser"/> for file I/O.
/// </summary>
public sealed class ScewinProvider : IBiosProvider
{
    private readonly ScewinService _scewin;
    private readonly LoggingService _log;

    /// <summary>Cache of the last export's raw file content for round-trip serialization.</summary>
    private string? _lastExportContent;

    /// <summary>Cache of the last parsed settings (needed for diff serialization).</summary>
    private IReadOnlyList<BiosSetting>? _lastSettings;

    public ScewinProvider(ScewinService scewin, LoggingService log)
    {
        _scewin = scewin;
        _log = log;
    }

    public BiosVendor SupportedVendor => BiosVendor.AmiGeneric;
    public string DisplayName => "AMI SCEWIN Setup Configuration Editor";

    public async Task<IReadOnlyList<BiosSetting>> GetSettingsAsync(CancellationToken ct = default)
    {
        var (filePath, result) = await _scewin.ExportAsync(ct);

        if (!result.Success || filePath is null)
        {
            _log.Error($"SCEWIN export failed: {result.HumanMessage}");
            throw new InvalidOperationException($"SCEWIN export failed: {result.HumanMessage}");
        }

        try
        {
            _lastExportContent = File.ReadAllText(filePath);
            _lastSettings = ScewinParser.Parse(_lastExportContent);
            if (_lastSettings.Count == 0)
            {
                var info = new FileInfo(filePath);
                var lines = _lastExportContent.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).Length;
                var message = $"SCEWIN returned an export, but no BIOS settings could be recognized ({info.Length:N0} bytes, {lines:N0} lines). The installed SCEWIN version may use an unsupported export format.";
                _log.Error(message);
                throw new InvalidOperationException(message);
            }

            _log.Success($"Parsed {_lastSettings.Count} BIOS settings from SCEWIN export.");
            return _lastSettings;
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to parse SCEWIN export: {ex.Message}");
            throw new InvalidOperationException($"Failed to parse SCEWIN export: {ex.Message}", ex);
        }
    }

    public async Task<ApplyResult> ApplySettingsAsync(
        IEnumerable<BiosSettingChange> changes,
        string? supervisorPassword,
        CancellationToken ct = default)
    {
        if (_lastExportContent is null || _lastSettings is null)
        {
            return new ApplyResult(false,
                new[] { "No export data available. Run Export first." }, false);
        }

        var changeList = changes.ToList();
        if (changeList.Count == 0)
        {
            return new ApplyResult(false,
                new[] { "No changes to apply." }, false);
        }

        // Validate all values are in the options list
        var errors = new List<string>();
        foreach (var change in changeList)
        {
            var setting = _lastSettings.FirstOrDefault(s =>
                s.Name.Equals(change.Name, StringComparison.OrdinalIgnoreCase));
            if (setting?.PossibleValues is { Count: > 0 } opts)
            {
                if (!opts.Contains(change.NewValue))
                    errors.Add($"'{change.NewValue}' is not a valid option for '{change.Name}'.");
            }
        }
        if (errors.Count > 0)
            return new ApplyResult(false, errors, false);

        // Generate the full import file with patched values
        string importContent = ScewinParser.SerializeFull(_lastExportContent, changeList);

        var importPath = Path.Combine(Path.GetTempPath(), $"scewin_import_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
        await File.WriteAllTextAsync(importPath, importContent, ct);

        var result = await _scewin.ImportAsync(importPath, ct);

        if (result.Success)
        {
            _log.Success($"Applied {changeList.Count} BIOS setting changes.");
            return new ApplyResult(true, Array.Empty<string>(), true);
        }

        return new ApplyResult(false, new[] { result.HumanMessage }, false);
    }
}
