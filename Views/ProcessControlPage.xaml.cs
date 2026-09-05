using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using KaliteKit.Models.ProcessControl;
using KaliteKit.Services;
using KaliteKit.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace KaliteKit.Views;

public sealed partial class ProcessControlPage : Page
{
    public ProcessControlViewModel ViewModel { get; }

    private bool _syncingEditor;

    public ProcessControlPage()
    {
        ViewModel = App.Services.GetRequiredService<ProcessControlViewModel>();
        this.InitializeComponent();
        ViewModel.Samples.CollectionChanged += (_, _) => DrawMonitor();
        Loaded += OnLoaded;
        Unloaded += (_, _) => ViewModel.Dispose();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PopulateEditorCombos();
        ViewModel.RefreshRulesAutostart();
        ViewModel.RefreshProcesses();
        DrawMonitor();
    }

    // ── Header toggles ───────────────────────────────────────────────────

    private void EngineToggle_Toggled(object sender, RoutedEventArgs e) => ViewModel.ToggleEngine();

    private void BoostToggle_Toggled(object sender, RoutedEventArgs e) => ViewModel.ToggleBoost();

    private void RunInBackground_Toggled(object sender, RoutedEventArgs e) => ViewModel.SetRunInBackground(((ToggleSwitch)sender).IsOn);

    private void RestoreAll_Click(object sender, RoutedEventArgs e) => ViewModel.RestoreAll();

    // ── Process actions ──────────────────────────────────────────────────

    private void Kill_Click(object sender, RoutedEventArgs e) => _ = ViewModel.KillSelectedAsync();

    private async void Restart_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedProcess == null)
        {
            ViewModel.StatusText = "Select a process to restart.";
            return;
        }
        await ViewModel.RestartSelectedAsync();
    }

    private void Restore_Click(object sender, RoutedEventArgs e) => ViewModel.RestoreSelected();

    private void AllowClose_Click(object sender, RoutedEventArgs e) => ViewModel.AllowCloseSelected();

    private void CreateRuleForProcess_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.NewRule(ViewModel.SelectedProcess);
        OpenRuleEditor(ProcessList);
    }

    private void ApplyRuleFlyout_Opening(object sender, object e) => PopulateApplyRuleMenu(ApplyRuleFlyout.Items);

    private void PresetFlyout_Opening(object sender, object e) => PopulatePresetMenu(PresetFlyout.Items);

    private void ProcessMenu_Opening(object sender, object e)
    {
        PopulateApplyRuleMenu(CtxApplyRule.Items);
        PopulatePresetMenu(CtxPresets.Items);
    }

    /// <summary>
    /// Right-click does not change ListView selection by default, and every
    /// context-menu action reads SelectedProcess — so the tapped row must be
    /// selected before the menu opens, or the actions hit the wrong (or no)
    /// process. RightTapped fires before the flyout's Opening, so this sticks.
    /// </summary>
    private void ProcessList_RightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement fe && fe.DataContext is ProcessItem item)
        {
            ProcessList.SelectedItem = item;
        }
    }

    private void PopulateApplyRuleMenu(IList<MenuFlyoutItemBase> items)
    {
        items.Clear();
        foreach (var rule in ViewModel.Rules)
        {
            var item = new MenuFlyoutItem { Text = string.IsNullOrEmpty(rule.Name) ? rule.ProcessName : rule.Name };
            string id = rule.Id;
            item.Click += (_, _) => ViewModel.ApplyRuleToSelected(id);
            items.Add(item);
        }
        if (ViewModel.Rules.Count == 0)
        {
            items.Add(new MenuFlyoutItem { Text = "(no rules yet — create one on the Rules tab)", IsEnabled = false });
        }
    }

    private void PopulatePresetMenu(IList<MenuFlyoutItemBase> items)
    {
        items.Clear();
        foreach (var preset in Enum.GetValues<CoreIsolationPreset>())
        {
            var item = new MenuFlyoutItem { Text = PresetLabel(preset) };
            var p = preset;
            item.Click += (_, _) =>
            {
                string message = ViewModel.ApplyPresetToSelectedProcess(p);
                if (!string.IsNullOrEmpty(message)) ViewModel.StatusText = message;
            };
            if (!ViewModel.PresetAvailable(preset)) item.IsEnabled = false;
            items.Add(item);
        }
    }

    private static string PresetLabel(CoreIsolationPreset preset) => preset switch
    {
        CoreIsolationPreset.ECoresOff => "E-Cores Off",
        CoreIsolationPreset.PCoresOff => "P-Cores Off",
        CoreIsolationPreset.Ccd0Off => "CCD0 Off",
        CoreIsolationPreset.Ccd1Off => "CCD1 Off",
        CoreIsolationPreset.SmtOff => "SMT Off",
        _ => preset.ToString(),
    };

    // ── Rule editor ──────────────────────────────────────────────────────

    private void NewRule_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.NewRule();
        OpenRuleEditor(sender as FrameworkElement ?? RulesList);
    }

    private void EditRule_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedRule == null)
        {
            ViewModel.StatusText = "Select a rule to edit.";
            return;
        }
        ViewModel.EditRule(ViewModel.SelectedRule);
        OpenRuleEditor(sender as FrameworkElement ?? RulesList);
    }

    private void RulesList_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        if (ViewModel.SelectedRule == null) return;
        ViewModel.EditRule(ViewModel.SelectedRule);
        OpenRuleEditor(RulesList);
    }

    private void OpenRuleEditor(FrameworkElement anchor)
    {
        _syncingEditor = true;
        SyncEditorCombos();
        InstanceIndexBox.Text = ViewModel.EditingRule?.InstanceIndex?.ToString() ?? string.Empty;
        MaxInstancesBox.Text = ViewModel.EditingRule?.MaxInstances?.ToString() ?? string.Empty;
        _syncingEditor = false;
        PopulateCorePicker();

        // MenuFlyoutItem clicks arrive while the context menu is still closing;
        // showing another flyout in the same dispatcher pass gets swallowed.
        // Defer one pass so the menu is fully gone before the editor opens.
        anchor.DispatcherQueue.TryEnqueue(() =>
        {
            try { RuleEditorFlyout.ShowAt(anchor); }
            catch (Exception ex)
            {
                ViewModel.StatusText = $"Could not open rule editor: {ex.Message}";
            }
        });
    }

    private void PopulateEditorCombos()
    {
        // Match mode (indices map 1:1 to RuleMatchMode).
        MatchModeBox.Items.Clear();
        MatchModeBox.Items.Add(new ComboBoxItem { Content = "Process name" });
        MatchModeBox.Items.Add(new ComboBoxItem { Content = "Full path" });
        MatchModeBox.Items.Add(new ComboBoxItem { Content = "Command line" });

        // CPU priority: index 0 = keep.
        CpuPriorityBox.Items.Clear();
        CpuPriorityBox.Items.Add(new ComboBoxItem { Content = "(keep)" });
        foreach (var level in Enum.GetValues<CpuPriorityLevel>())
        {
            CpuPriorityBox.Items.Add(new ComboBoxItem { Content = level.ToString() });
        }

        IoPriorityBox.Items.Clear();
        IoPriorityBox.Items.Add(new ComboBoxItem { Content = "(keep)" });
        foreach (var level in Enum.GetValues<IoPriorityLevel>())
        {
            // Windows reserves High I/O priority for its own use — it can never
            // be set on a user process, so don't offer a choice that always fails.
            IoPriorityBox.Items.Add(new ComboBoxItem
            {
                Content = level == IoPriorityLevel.High ? "High (reserved by Windows)" : level.ToString(),
                IsEnabled = level != IoPriorityLevel.High,
            });
        }

        MemoryPriorityBox.Items.Clear();
        MemoryPriorityBox.Items.Add(new ComboBoxItem { Content = "(keep)" });
        MemoryPriorityBox.Items.Add(new ComboBoxItem { Content = "1 (Lowest)" });
        MemoryPriorityBox.Items.Add(new ComboBoxItem { Content = "2 (Low)" });
        MemoryPriorityBox.Items.Add(new ComboBoxItem { Content = "3 (Medium)" });
        MemoryPriorityBox.Items.Add(new ComboBoxItem { Content = "4 (High)" });
        MemoryPriorityBox.Items.Add(new ComboBoxItem { Content = "5 (Highest, default)" });
    }

    private void SyncEditorCombos()
    {
        var rule = ViewModel.EditingRule;
        if (rule == null) return;

        MatchModeBox.SelectedIndex = (int)rule.MatchMode;
        CpuPriorityBox.SelectedIndex = rule.CpuPriority is { } cpu ? (int)cpu + 1 : 0;
        IoPriorityBox.SelectedIndex = rule.IoPriority is { } io ? (int)io + 1 : 0;
        MemoryPriorityBox.SelectedIndex = rule.MemoryPriority is { } mem ? (int)mem : 0;
    }

    private void Editor_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingEditor) return;
        ReadEditorIntoRule();
    }

    /// <summary>Applies every editor control back onto the rule being edited.</summary>
    private void ReadEditorIntoRule()
    {
        var rule = ViewModel.EditingRule;
        if (rule == null) return;

        rule.MatchMode = (RuleMatchMode)Math.Max(0, MatchModeBox.SelectedIndex);
        rule.CpuPriority = CpuPriorityBox.SelectedIndex > 0 ? (CpuPriorityLevel)(CpuPriorityBox.SelectedIndex - 1) : null;
        rule.IoPriority = IoPriorityBox.SelectedIndex > 0 ? (IoPriorityLevel)(IoPriorityBox.SelectedIndex - 1) : null;
        rule.MemoryPriority = MemoryPriorityBox.SelectedIndex > 0 ? (MemoryPriorityLevel)MemoryPriorityBox.SelectedIndex : null;
    }

    private void RulePreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string tag) return;
        if (!Enum.TryParse(tag, out CoreIsolationPreset preset))
        {
            ViewModel.StatusText = "Unknown preset.";
            return;
        }
        string message = ViewModel.ApplyPresetToRule(preset);
        if (!string.IsNullOrEmpty(message)) ViewModel.StatusText = message;
    }

    private void ClearPin_Click(object sender, RoutedEventArgs e) => ViewModel.ClearRulePin();

    // ── Core/thread picker ─────────────────────────────────────────────

    /// <summary>Logical-CPU checkbox built from live topology; tracks its CPU-set id.</summary>
    private sealed record CoreToggle(CheckBox Box, uint CpuSetId);

    private readonly List<CoreToggle> _coreToggles = new();

    /// <summary>Builds the per-logical-CPU checkbox grid from the detected topology and reflects the rule's current pin.</summary>
    private void PopulateCorePicker()
    {
        _coreToggles.Clear();
        CorePickerPanel.Children.Clear();

        var topo = ViewModel.GetTopology();
        var sets = topo.CpuSets;
        if (sets.Count == 0)
        {
            CoreSelectionText.Text = "CPU topology unavailable — per-core selection disabled.";
            return;
        }

        var pinned = ViewModel.EditingRule?.CpuSetIds?.ToHashSet() ?? new HashSet<uint>();
        foreach (var group in sets
                     .GroupBy(s => (s.Group, s.CoreIndex))
                     .OrderBy(g => g.Key.Group).ThenBy(g => g.Key.CoreIndex))
        {
            var corePanel = new StackPanel { Spacing = 2 };
            bool isHybrid = topo.HasHybridCores;
            string classTag = isHybrid ? (group.First().IsPerformance ? "P" : "E")
                                       : $"C{group.Key.CoreIndex}";
            corePanel.Children.Add(new TextBlock
            {
                Text = $"{classTag} {group.Key.CoreIndex}",
                FontSize = 10,
                Opacity = 0.65,
                HorizontalAlignment = HorizontalAlignment.Center,
            });

            foreach (var set in group.OrderBy(s => s.LogicalProcessorIndex))
            {
                var box = new CheckBox
                {
                    MinWidth = 0,
                    Padding = new Thickness(0),
                    IsChecked = pinned.Contains(set.Id),
                };
                ToolTipService.SetToolTip(box, $"Logical CPU {set.LogicalProcessorIndex} (set {set.Id})");
                box.Checked += (_, _) => OnCoreToggleChanged();
                box.Unchecked += (_, _) => OnCoreToggleChanged();
                _coreToggles.Add(new CoreToggle(box, set.Id));
                corePanel.Children.Add(box);
            }

            CorePickerPanel.Children.Add(corePanel);
        }

        SyncCoreSelectionText();
    }

    /// <summary>Writes the checkbox state into the rule's CPU-set pin while the editor is open.</summary>
    private void OnCoreToggleChanged()
    {
        if (_syncingEditor) return;
        var rule = ViewModel.EditingRule;
        if (rule == null) return;

        var selected = _coreToggles.Where(t => t.Box.IsChecked == true).Select(t => t.CpuSetId).ToList();
        rule.CpuSetIds = selected.Count > 0 ? selected : new List<uint>();
        rule.AffinityMask = 0;
        SyncCoreSelectionText();
    }

    private void SyncCoreSelectionText()
    {
        int selected = _coreToggles.Count(t => t.Box.IsChecked == true);
        CoreSelectionText.Text = selected == 0
            ? "Any core (no pin)"
            : $"{selected} of {_coreToggles.Count} logical CPU(s) pinned";
    }

    private void CoreSelectAll_Click(object sender, RoutedEventArgs e)
    {
        _syncingEditor = true;
        foreach (var t in _coreToggles) t.Box.IsChecked = true;
        _syncingEditor = false;
        OnCoreToggleChanged();
    }

    private void CoreClearAll_Click(object sender, RoutedEventArgs e)
    {
        _syncingEditor = true;
        foreach (var t in _coreToggles) t.Box.IsChecked = false;
        _syncingEditor = false;
        OnCoreToggleChanged();
    }

    private async void SaveRule_Click(object sender, RoutedEventArgs e)
    {
        ReadEditorIntoRule();
        ViewModel.SetEditorNumerics(InstanceIndexBox.Text, MaxInstancesBox.Text);
        if (await ViewModel.SaveRuleAsync())
        {
            RuleEditorFlyout.Hide();
        }
    }

    private void CancelRuleEdit_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.CancelRuleEdit();
        RuleEditorFlyout.Hide();
    }

    private void DeleteRule_Click(object sender, RoutedEventArgs e) => ViewModel.DeleteSelectedRule();

    private void RuleEnabledChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox box || box.DataContext is not ProcessRule rule) return;
        ViewModel.UpdateRuleQuick(rule);
    }

    // ── Rules toolbar ────────────────────────────────────────────────────

    private async void ExportRules_Click(object sender, RoutedEventArgs e) => await ViewModel.ExportRulesAsync();

    private async void ImportRules_Click(object sender, RoutedEventArgs e) => await ViewModel.ImportRulesAsync();

    // ── Engine ───────────────────────────────────────────────────────────

    private void SaveEngine_Click(object sender, RoutedEventArgs e) => ViewModel.SaveAutoBalanceSettings();

    private void RepairAutostart_Click(object sender, RoutedEventArgs e)
    {
        ProcessControlService.EnableRulesAutostart();
        ViewModel.RefreshRulesAutostart();
        ViewModel.StatusText = "Login autostart registered — sticky rules enforce from logon.";
    }

    /// <summary>
    /// Hands rule enforcement back to a fresh background session before the
    /// UI exits: releases the engine mutex, spawns KaliteKit.exe --rules, and the
    /// background process owns the engine from then on (no enforcement gap).
    /// </summary>
    public static void HandEngineToBackgroundSession()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            ProcessControlService.EndEngineSession();
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "--rules",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
        }
        catch { }
    }

    // ── Action log ───────────────────────────────────────────────────────

    /// <summary>Draws 25/50/75% reference lines and labels behind the graph so bar heights have meaning.</summary>
    private void DrawScaleLines(Microsoft.UI.Xaml.Controls.Canvas target, double width, double height)
    {
        try
        {
            if (target == null || width <= 0 || height <= 0) return;
            target.Children.Clear();
            target.Width = width;
            target.Height = height;
            var lineColor = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128));
            foreach (double pct in new[] { 25, 50, 75 })
            {
                double y = height - height * pct / 100.0;
                target.Children.Add(new Rectangle
                {
                    Width = width,
                    Height = 1,
                    Fill = lineColor,
                });
                var line = target.Children[^1] as Rectangle;
                Canvas.SetLeft(line!, 0);
                Canvas.SetTop(line!, y);

                var label = new TextBlock
                {
                    Text = $"{pct}%",
                    FontSize = 10,
                    Opacity = 0.45,
                    Margin = new Thickness(2, -14, 0, 0),
                };
                target.Children.Add(label);
                Canvas.SetLeft(label, 0);
                Canvas.SetTop(label, y);
            }
        }
        catch { }
    }

    private async void ExportActions_Click(object sender, RoutedEventArgs e) => await ViewModel.ExportActionsAsync();

    // ── Monitor drawing ──────────────────────────────────────────────────

    private void DrawMonitor()
    {
        try
        {
            DrawScaleLines(CoreScaleCanvas, CoreCanvas.ActualWidth == 0 ? CoreCanvas.Width : (CoreCanvas.ActualWidth < 400 ? 400 : CoreCanvas.ActualWidth), CoreCanvas.Height);
            DrawScaleLines(HistoryScaleCanvas, HistoryCanvas.Width, HistoryCanvas.Height);

            CoreCanvas.Children.Clear();
            var last = ViewModel.Samples.LastOrDefault();
            if (last != null && last.Cores.Length > 0)
            {
                const double barWidth = 26;
                const double gap = 6;
                double height = CoreCanvas.Height;
                CoreCanvas.Width = last.Cores.Length * (barWidth + gap);
                for (int i = 0; i < last.Cores.Length; i++)
                {
                    double usage = Math.Clamp(last.Cores[i], 0, 100);
                    var bar = new Rectangle
                    {
                        Width = barWidth,
                        Height = Math.Max(2, height * usage / 100.0),
                        RadiusX = 3,
                        RadiusY = 3,
                        Fill = new SolidColorBrush(usage > 85 ? Colors.OrangeRed : Colors.SteelBlue),
                    };
                    Canvas.SetLeft(bar, i * (barWidth + gap));
                    Canvas.SetTop(bar, height - bar.Height);
                    CoreCanvas.Children.Add(bar);
                }
            }

            HistoryCanvas.Children.Clear();
            if (ViewModel.Samples.Count > 1)
            {
                double width = HistoryCanvas.Width;
                double height = HistoryCanvas.Height;
                double step = width / 90.0;
                var points = new PointCollection();
                for (int i = 0; i < ViewModel.Samples.Count; i++)
                {
                    double y = height - Math.Clamp(ViewModel.Samples[i].TotalCpu, 0, 100) / 100.0 * height;
                    points.Add(new Point(i * step, y));
                }
                HistoryCanvas.Children.Add(new Polyline
                {
                    Stroke = new SolidColorBrush(Colors.SteelBlue),
                    StrokeThickness = 2,
                    Points = points,
                });
            }
        }
        catch { /* drawing must never crash the page */ }
    }
}