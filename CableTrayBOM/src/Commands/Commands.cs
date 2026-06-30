using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using CableTrayBOM.Models;
using CableTrayBOM.Services;
using CableTrayBOM.UI;
using WinButton = System.Windows.Controls.Button;
using WinTextBox = System.Windows.Controls.TextBox;
using WinOrientation = System.Windows.Controls.Orientation;

namespace CableTrayBOM.Commands
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class GenerateBOMCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                var uiDoc = commandData.Application.ActiveUIDocument;
                if (uiDoc?.Document == null) { TaskDialog.Show("Error", "No active document."); return Result.Failed; }
                new MainWindow(uiDoc).ShowDialog();
                return Result.Succeeded;
            }
            catch (Exception ex) { message = ex.Message; return Result.Failed; }
        }
    }

    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class QuickSliceCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                var uiDoc = commandData.Application.ActiveUIDocument;
                var doc = uiDoc?.Document;
                if (doc == null) { TaskDialog.Show("Error", "No active document."); return Result.Failed; }

                var rawSelection = uiDoc!.Selection.GetElementIds();
                if (rawSelection.Count == 0)
                {
                    TaskDialog.Show("Quick Slice",
                        "Please select cable tray elements or groups containing cable trays, then run this command.");
                    return Result.Failed;
                }

                // Expand selection: if groups are selected, include their member elements
                var selection = new HashSet<ElementId>(rawSelection);
                foreach (var id in rawSelection)
                {
                    var elem = doc.GetElement(id);
                    if (elem is Group grp)
                    {
                        foreach (var memberId in grp.GetMemberIds())
                            selection.Add(memberId);
                    }
                }

                var inputWindow = new QuickSliceInputWindow();
                if (inputWindow.ShowDialog() != true) return Result.Cancelled;

                double segLenMm = inputWindow.SegmentLengthMm;
                double gapMm = inputWindow.CouplingGapMm;

                var settings = SettingsService.Load();
                var collector = new RevitElementCollector(doc);
                var allSegments = collector.CollectCableTrays(settings);
                var selected = allSegments
                    .Where(s => selection.Contains(RevitCompat.ToElementId(s.ElementId)))
                    .ToList();

                if (selected.Count == 0)
                { TaskDialog.Show("Quick Slice", "No cable trays found in selection."); return Result.Failed; }

                CuttingResult cutResult;
                using (var tx = new Transaction(doc, "Quick Slice - Cut Cable Trays"))
                {
                    var opts = tx.GetFailureHandlingOptions();
                    opts.SetFailuresPreprocessor(new GroupWarningSwallower());
                    tx.SetFailureHandlingOptions(opts);
                    tx.Start();
                    cutResult = new TrayCuttingService(doc, segLenMm, gapMm).CutAllTrays(selected);
                    tx.Commit();
                }

                TaskDialog.Show("Quick Slice Complete",
                    $"{cutResult.Summary}\n\nSegment: {segLenMm:F0} mm, Gap: {gapMm:F1} mm" +
                    (cutResult.Errors.Count > 0 ? $"\n\nErrors:\n{string.Join("\n", cutResult.Errors.Take(10))}" : ""));

                return Result.Succeeded;
            }
            catch (Exception ex) { message = ex.Message; return Result.Failed; }
        }
    }

    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class FixtureBOMCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                var uiDoc = commandData.Application.ActiveUIDocument;
                if (uiDoc?.Document == null) return Result.Failed;
                new MainWindow(uiDoc).ShowDialog();
                return Result.Succeeded;
            }
            catch (Exception ex) { message = ex.Message; return Result.Failed; }
        }
    }

    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class SettingsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                var settings = SettingsService.Load();
                var window = new SettingsWindow(settings);
                if (window.ShowDialog() == true)
                {
                    SettingsService.Save(window.Settings);
                    TaskDialog.Show("Settings", "Joint material settings saved.");
                }
                return Result.Succeeded;
            }
            catch (Exception ex) { message = ex.Message; return Result.Failed; }
        }
    }

    public class QuickSliceInputWindow : Window
    {
        private WinTextBox _txtSegLen;
        private WinTextBox _txtGap;

        public double SegmentLengthMm { get; private set; } = 3000;
        public double CouplingGapMm { get; private set; } = 1;

        public QuickSliceInputWindow()
        {
            Title = "Quick Slice Settings";
            Width = 360; Height = 260;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.NoResize;
            Background = System.Windows.Media.Brushes.WhiteSmoke;

            var sp = new StackPanel { Margin = new Thickness(20) };

            sp.Children.Add(new TextBlock
            {
                Text = "Cut Cable Trays into Standard Pieces",
                FontWeight = FontWeights.Bold, FontSize = 14,
                Margin = new Thickness(0, 0, 0, 16)
            });

            sp.Children.Add(new TextBlock { Text = "Tray Segment Length (mm):", Margin = new Thickness(0, 0, 0, 4) });
            _txtSegLen = new WinTextBox { Text = "3000", Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 0, 0, 12) };
            sp.Children.Add(_txtSegLen);

            sp.Children.Add(new TextBlock { Text = "Coupling Gap (mm):", Margin = new Thickness(0, 0, 0, 4) });
            _txtGap = new WinTextBox { Text = "1", Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 0, 0, 20) };
            sp.Children.Add(_txtGap);

            var btnPanel = new StackPanel { Orientation = WinOrientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };

            var btnCancel = new WinButton
            {
                Content = "Cancel", Width = 90, Height = 30,
                Margin = new Thickness(0, 0, 10, 0)
            };
            btnCancel.Click += (s, e) => { DialogResult = false; Close(); };

            var btnOk = new WinButton
            {
                Content = "Cut Trays", Width = 90, Height = 30,
                FontWeight = FontWeights.Bold
            };
            btnOk.Click += BtnOk_Click;

            btnPanel.Children.Add(btnCancel);
            btnPanel.Children.Add(btnOk);
            sp.Children.Add(btnPanel);

            Content = sp;
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            if (double.TryParse(_txtSegLen.Text, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out double seg) && seg > 0)
                SegmentLengthMm = seg;
            else { System.Windows.MessageBox.Show("Invalid segment length."); return; }

            if (double.TryParse(_txtGap.Text, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out double gap) && gap >= 0)
                CouplingGapMm = gap;
            else { System.Windows.MessageBox.Show("Invalid coupling gap."); return; }

            DialogResult = true;
            Close();
        }
    }

    /// <summary>
    /// Toolbar command: scans model and writes all joint material parameters directly.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class UpdateCountsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                var uiDoc = commandData.Application.ActiveUIDocument;
                var doc = uiDoc?.Document;
                if (doc == null) { TaskDialog.Show("Error", "No active document."); return Result.Failed; }

                var settings = SettingsService.Load();
                var collector = new RevitElementCollector(doc);

                // Scan
                var segments = collector.CollectCableTrays(settings);
                var lights = collector.CollectLightingFixtures();
                var elec = collector.CollectElectricalFixtures();

                // Slice data
                var slicer = new SlicingService(settings);
                foreach (var s in segments) slicer.SliceSegment(s);

                // Write parameters
                int ct = 0, fx = 0;
                using (var tx = new Transaction(doc, "BOM - Update Counts"))
                {
                    tx.Start();
                    var writer = new ParameterWriterService(doc, settings);
                    ct = writer.WriteCableTrayParameters(segments);
                    fx = writer.WriteFixtureParameters(lights);
                    fx += writer.WriteFixtureParameters(elec);
                    tx.Commit();
                }

                TaskDialog.Show("Update Complete",
                    $"Joint material counts updated!\n\n" +
                    $"Cable Trays/Ladders: {ct} elements\nFixtures: {fx} elements");
                return Result.Succeeded;
            }
            catch (Exception ex) { message = ex.Message; return Result.Failed; }
        }
    }

    /// <summary>
    /// Room selection dialog with checkboxes and rename for Unassigned.
    /// </summary>
    public class RoomSelectionWindow : Window
    {
        private StackPanel _checkPanel;
        private List<System.Windows.Controls.CheckBox> _checkBoxes = new();
        public List<string> SelectedRooms { get; private set; } = new();
        public HashSet<string> UnassignedNames { get; private set; } = new();

        public RoomSelectionWindow(List<string> rooms)
        {
            Title = "Select Rooms for Schedules";
            Width = 420; Height = 500;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.CanResize;
            Background = System.Windows.Media.Brushes.WhiteSmoke;

            var mainSp = new StackPanel { Margin = new Thickness(16) };

            mainSp.Children.Add(new TextBlock
            {
                Text = "Select rooms to create per-room schedules:",
                FontWeight = FontWeights.Bold, FontSize = 13,
                Margin = new Thickness(0, 0, 0, 8)
            });

            mainSp.Children.Add(new TextBlock
            {
                Text = "Double-click 'Unassigned' to rename it.",
                FontSize = 11, Foreground = System.Windows.Media.Brushes.Gray,
                Margin = new Thickness(0, 0, 0, 8)
            });

            // Select All / None / Skip buttons
            var btnRow = new StackPanel { Orientation = WinOrientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            var btnAll = new WinButton { Content = "Select All", Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(8, 4, 8, 4) };
            btnAll.Click += (s, e) => { foreach (var cb in _checkBoxes) cb.IsChecked = true; };
            var btnNone = new WinButton { Content = "Select None", Padding = new Thickness(8, 4, 8, 4) };
            btnNone.Click += (s, e) => { foreach (var cb in _checkBoxes) cb.IsChecked = false; };
            var btnSkip = new WinButton { Content = "Overall Schedules Only", Margin = new Thickness(16, 0, 0, 0), Padding = new Thickness(8, 4, 8, 4) };
            btnSkip.Click += (s, e) => { SelectedRooms = new List<string>(); UnassignedNames = new HashSet<string>(); DialogResult = true; Close(); };
            btnRow.Children.Add(btnAll);
            btnRow.Children.Add(btnNone);
            btnRow.Children.Add(btnSkip);
            mainSp.Children.Add(btnRow);

            // Scrollable checkbox list
            _checkPanel = new StackPanel();
            var scroll = new System.Windows.Controls.ScrollViewer
            {
                Content = _checkPanel,
                Height = 300,
                VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                Margin = new Thickness(0, 0, 0, 12),
                BorderBrush = System.Windows.Media.Brushes.LightGray,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(4)
            };

            foreach (var room in rooms)
            {
                if (room == "Unassigned")
                {
                    // Editable row for Unassigned
                    var rowSp = new StackPanel { Orientation = WinOrientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
                    var cb = new System.Windows.Controls.CheckBox { IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
                    var tb = new WinTextBox
                    {
                        Text = "Unassigned",
                        Width = 300,
                        Margin = new Thickness(6, 0, 0, 0),
                        Padding = new Thickness(4, 2, 4, 2),
                        ToolTip = "Edit to rename the schedule for unassigned elements"
                    };
                    cb.Tag = tb; // link checkbox to its textbox
                    rowSp.Children.Add(cb);
                    rowSp.Children.Add(tb);
                    _checkPanel.Children.Add(rowSp);
                    _checkBoxes.Add(cb);
                }
                else
                {
                    var cb = new System.Windows.Controls.CheckBox
                    {
                        Content = room,
                        IsChecked = true,
                        Margin = new Thickness(0, 2, 0, 2)
                    };
                    _checkPanel.Children.Add(cb);
                    _checkBoxes.Add(cb);
                }
            }

            mainSp.Children.Add(scroll);

            var okBtn = new WinButton
            {
                Content = "Create Schedules", Width = 140, Height = 30,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right
            };
            okBtn.Click += (s, e) =>
            {
                SelectedRooms = new List<string>();
                UnassignedNames = new HashSet<string>();
                foreach (var cb in _checkBoxes)
                {
                    if (cb.IsChecked != true) continue;
                    if (cb.Tag is WinTextBox tb)
                    {
                        string name = tb.Text;
                        SelectedRooms.Add(name);
                        UnassignedNames.Add(name); // mark as unassigned
                    }
                    else
                        SelectedRooms.Add(cb.Content?.ToString() ?? "");
                }
                DialogResult = true;
                Close();
            };
            mainSp.Children.Add(okBtn);

            Content = mainSp;
        }
    }

    /// <summary>
    /// Room selection dialog for Scan Model.
    /// Two modes: Scan All (returns null) or Scan Selected Rooms (returns list).
    /// No schedule-related options.
    /// </summary>
    public class ScanRoomSelectionWindow : Window
    {
        private StackPanel _checkPanel;
        private List<System.Windows.Controls.CheckBox> _checkBoxes = new();
        /// <summary>null = scan all rooms, non-empty = filter by selected rooms</summary>
        public List<string>? SelectedRooms { get; private set; }

        public ScanRoomSelectionWindow(List<string> rooms)
        {
            Title = "Select Rooms to Scan";
            Width = 420; Height = 500;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.CanResize;
            Background = System.Windows.Media.Brushes.WhiteSmoke;

            var mainSp = new StackPanel { Margin = new Thickness(16) };

            mainSp.Children.Add(new TextBlock
            {
                Text = "Select which rooms to include in the scan:",
                FontWeight = FontWeights.Bold, FontSize = 13,
                Margin = new Thickness(0, 0, 0, 4)
            });
            mainSp.Children.Add(new TextBlock
            {
                Text = "Only elements in selected rooms will be processed.",
                FontSize = 11, Foreground = System.Windows.Media.Brushes.Gray,
                Margin = new Thickness(0, 0, 0, 8)
            });

            var btnRow = new StackPanel { Orientation = WinOrientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            var btnAll = new WinButton { Content = "Select All", Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(8, 4, 8, 4) };
            btnAll.Click += (s, e) => { foreach (var cb in _checkBoxes) cb.IsChecked = true; };
            var btnNone = new WinButton { Content = "Select None", Padding = new Thickness(8, 4, 8, 4) };
            btnNone.Click += (s, e) => { foreach (var cb in _checkBoxes) cb.IsChecked = false; };
            btnRow.Children.Add(btnAll);
            btnRow.Children.Add(btnNone);
            mainSp.Children.Add(btnRow);

            _checkPanel = new StackPanel();
            var scroll = new System.Windows.Controls.ScrollViewer
            {
                Content = _checkPanel, Height = 280,
                VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                Margin = new Thickness(0, 0, 0, 12),
                BorderBrush = System.Windows.Media.Brushes.LightGray,
                BorderThickness = new Thickness(1), Padding = new Thickness(4)
            };

            // Add "Unassigned" option
            var cbUnassigned = new System.Windows.Controls.CheckBox
            {
                Content = "Unassigned (elements outside rooms)",
                IsChecked = true, Margin = new Thickness(0, 2, 0, 2),
                FontStyle = System.Windows.FontStyles.Italic
            };
            _checkPanel.Children.Add(cbUnassigned);
            _checkBoxes.Add(cbUnassigned);

            foreach (var room in rooms)
            {
                var cb = new System.Windows.Controls.CheckBox
                {
                    Content = room, IsChecked = true, Margin = new Thickness(0, 2, 0, 2)
                };
                _checkPanel.Children.Add(cb);
                _checkBoxes.Add(cb);
            }
            mainSp.Children.Add(scroll);

            // Two action buttons: Scan All (no filter) and Scan Selected
            var actionRow = new StackPanel { Orientation = WinOrientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };

            var btnScanAll = new WinButton
            {
                Content = "Scan Entire Model", Width = 140, Height = 30,
                Margin = new Thickness(0, 0, 8, 0)
            };
            btnScanAll.Click += (s, e) =>
            {
                SelectedRooms = null; // null = no filter, scan everything
                DialogResult = true; Close();
            };

            var btnScanSelected = new WinButton
            {
                Content = "Scan Selected Rooms", Width = 150, Height = 30,
                FontWeight = FontWeights.Bold
            };
            btnScanSelected.Click += (s, e) =>
            {
                SelectedRooms = new List<string>();
                foreach (var cb in _checkBoxes)
                {
                    if (cb.IsChecked != true) continue;
                    string name = cb.Content?.ToString() ?? "";
                    if (name.Contains("Unassigned"))
                        SelectedRooms.Add("Unassigned");
                    else
                        SelectedRooms.Add(name);
                }
                DialogResult = true; Close();
            };

            actionRow.Children.Add(btnScanAll);
            actionRow.Children.Add(btnScanSelected);
            mainSp.Children.Add(actionRow);

            Content = mainSp;
        }
    }

    /// <summary>
    /// Resizable, scrollable results window. Replaces MessageBox for long output.
    /// </summary>
    public class ScrollableResultWindow : Window
    {
        public ScrollableResultWindow(string message, string title, bool isWarning = false)
        {
            Title = title;
            Width = 520; Height = 500;
            MinWidth = 350; MinHeight = 250;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.CanResize;
            Background = System.Windows.Media.Brushes.White;

            var grid = new System.Windows.Controls.Grid();
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });

            // Scrollable text
            var tb = new System.Windows.Controls.TextBox
            {
                Text = message,
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize = 12,
                Margin = new Thickness(12, 12, 12, 0),
                BorderThickness = new Thickness(0),
                Background = System.Windows.Media.Brushes.White
            };
            System.Windows.Controls.Grid.SetRow(tb, 0);
            grid.Children.Add(tb);

            // Button row
            var btnRow = new StackPanel
            {
                Orientation = WinOrientation.Horizontal,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                Margin = new Thickness(12)
            };

            var btnCopy = new WinButton
            {
                Content = "📋 Copy", Width = 80, Height = 28,
                Margin = new Thickness(0, 0, 8, 0)
            };
            btnCopy.Click += (s, e) =>
            {
                System.Windows.Clipboard.SetText(message);
                btnCopy.Content = "✓ Copied";
            };

            var btnOk = new WinButton
            {
                Content = "OK", Width = 80, Height = 28,
                IsDefault = true, FontWeight = FontWeights.Bold
            };
            btnOk.Click += (s, e) => Close();

            btnRow.Children.Add(btnCopy);
            btnRow.Children.Add(btnOk);
            System.Windows.Controls.Grid.SetRow(btnRow, 1);
            grid.Children.Add(btnRow);

            Content = grid;
        }

        /// <summary>
        /// Static helper matching MessageBox.Show signature for easy replacement.
        /// </summary>
        public static void Show(string message, string title, bool isWarning = false)
        {
            var win = new ScrollableResultWindow(message, title, isWarning);
            win.ShowDialog();
        }
    }

    /// <summary>
    /// Makes a command available in all contexts including group edit mode.
    /// </summary>
    public class AlwaysAvailable : IExternalCommandAvailability
    {
        public bool IsCommandAvailable(UIApplication applicationData, CategorySet selectedCategories)
        {
            return true;
        }
    }

    /// <summary>
    /// Automatically dismisses group-related warnings and disconnect errors
    /// during transactions that modify grouped elements.
    /// </summary>
    public class GroupWarningSwallower : IFailuresPreprocessor
    {
        public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
        {
            var failures = failuresAccessor.GetFailureMessages();
            foreach (var failure in failures)
            {
                // Get the failure definition ID
                var defId = failure.GetFailureDefinitionId();

                // Dismiss warnings (non-errors)
                if (failure.GetSeverity() == FailureSeverity.Warning)
                {
                    failuresAccessor.DeleteWarning(failure);
                    continue;
                }

                // For errors: try to resolve with default resolution
                if (failure.GetSeverity() == FailureSeverity.Error)
                {
                    if (failure.HasResolutions())
                    {
                        try
                        {
                            failuresAccessor.ResolveFailure(failure);
                        }
                        catch
                        {
                            try { failuresAccessor.DeleteWarning(failure); } catch { }
                        }
                    }
                }
            }

            // If any failures were resolved, tell Revit to continue
            if (failures.Count > 0)
                return FailureProcessingResult.ProceedWithCommit;

            return FailureProcessingResult.Continue;
        }
    }
}
