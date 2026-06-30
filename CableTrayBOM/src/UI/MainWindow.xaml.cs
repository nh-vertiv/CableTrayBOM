using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using CableTrayBOM.Commands;
using CableTrayBOM.Models;
using CableTrayBOM.Services;

namespace CableTrayBOM.UI
{
    public partial class MainWindow : Window
    {
        private readonly UIDocument _uiDoc;
        private readonly Document _doc;
        private BOMSettings _settings;
        private string _outputFolder;

        private List<CableTraySegment> _cableTraySegments = new();
        private List<FixtureElement> _lightingFixtures = new();
        private List<FixtureElement> _electricalFixtures = new();
        private HashSet<string> _unassignedRoomNames = new(); // tracks renamed "Unassigned" entries

        public MainWindow(UIDocument uiDoc)
        {
            InitializeComponent();
            _uiDoc = uiDoc;
            _doc = uiDoc.Document;
            _settings = SettingsService.Load();
            _outputFolder = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

            txtSliceLength.Text = _settings.DefaultSliceLength.ToString("F0", CultureInfo.InvariantCulture);
            txtCouplingGap.Text = _settings.CouplingGap.ToString("F1", CultureInfo.InvariantCulture);
            txtGSVPercentage.Text = _settings.GSV34FTPercentage.ToString("F0", CultureInfo.InvariantCulture);
            txtOutputPath.Text = _outputFolder;
        }

        // ═══════════════════════════════════════════════════════════
        // CHECK ALL / CHECK NONE
        // ═══════════════════════════════════════════════════════════

        private void BtnCheckAll_Click(object sender, RoutedEventArgs e)
        {
            SetAllElementChecks(true);
        }

        private void BtnCheckNone_Click(object sender, RoutedEventArgs e)
        {
            SetAllElementChecks(false);
        }

        private void SetAllElementChecks(bool value)
        {
            chkMeshTray.IsChecked = value;
            chkPerforatedTray.IsChecked = value;
            chkNonPerforatedTray.IsChecked = value;
            chkCableLadder.IsChecked = value;
            chkFiberTray.IsChecked = value;
            chkLighting.IsChecked = value;
            chkEmergency.IsChecked = value;
            chkSockets.IsChecked = value;
            chkJunctionBoxes.IsChecked = value;
        }

        // ═══════════════════════════════════════════════════════════
        // SCAN MODEL
        // ═══════════════════════════════════════════════════════════

        private void BtnScan_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                txtStatus.Text = "Detecting rooms...";
                progressBar.IsIndeterminate = true;

                // Quick room detection - just get room list without full element scan
                var collector = new RevitElementCollector(_doc);
                var availableRooms = collector.GetAvailableRooms();

                List<string>? selectedRoomIds = null;

                if (availableRooms.Count > 0)
                {
                    var roomDlg = new ScanRoomSelectionWindow(
                        availableRooms.Select(r => r.display).ToList());

                    if (roomDlg.ShowDialog() != true)
                    {
                        progressBar.IsIndeterminate = false;
                        txtStatus.Text = "Scan cancelled.";
                        return;
                    }

                    // null = scan all, non-empty = filter by these rooms
                    selectedRoomIds = roomDlg.SelectedRooms;
                }

                txtStatus.Text = "Scanning model...";

                _cableTraySegments = collector.CollectCableTrays(_settings);

                try
                {
                    string logPath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                        "CableTrayBOM_Diagnostic.txt");
                    File.WriteAllText(logPath, collector.GenerateDiagnosticLog(_cableTraySegments));
                }
                catch { }

                _lightingFixtures = collector.CollectLightingFixtures();
                _electricalFixtures = collector.CollectElectricalFixtures();
                FilterCollectedElements();

                // Filter by selected rooms if user chose specific rooms
                if (selectedRoomIds != null && selectedRoomIds.Count > 0)
                {
                    bool IncludesUnassigned = selectedRoomIds.Any(r =>
                        _unassignedRoomNames.Contains(r) || r == "Unassigned");

                    _cableTraySegments = _cableTraySegments.Where(s =>
                    {
                        string roomDisp = BuildRoomDisplayStatic(s.RoomNumber, s.RoomName);
                        if (string.IsNullOrEmpty(roomDisp))
                            return IncludesUnassigned;
                        return selectedRoomIds.Any(r => roomDisp.Contains(r) || r.Contains(roomDisp));
                    }).ToList();

                    _lightingFixtures = _lightingFixtures.Where(f =>
                    {
                        string roomDisp = BuildRoomDisplayStatic(f.RoomNumber, f.RoomName);
                        if (string.IsNullOrEmpty(roomDisp))
                            return IncludesUnassigned;
                        return selectedRoomIds.Any(r => roomDisp.Contains(r) || r.Contains(roomDisp));
                    }).ToList();

                    _electricalFixtures = _electricalFixtures.Where(f =>
                    {
                        string roomDisp = BuildRoomDisplayStatic(f.RoomNumber, f.RoomName);
                        if (string.IsNullOrEmpty(roomDisp))
                            return IncludesUnassigned;
                        return selectedRoomIds.Any(r => roomDisp.Contains(r) || r.Contains(roomDisp));
                    }).ToList();
                }

                // Room count: use display name format
                var roomSet = new HashSet<string>();
                foreach (var seg in _cableTraySegments)
                {
                    string rn = seg.RoomName, rnum = seg.RoomNumber;
                    if (!string.IsNullOrEmpty(rn) || !string.IsNullOrEmpty(rnum))
                        roomSet.Add(!string.IsNullOrEmpty(rnum) && !string.IsNullOrEmpty(rn)
                            ? $"{rnum} - {rn}" : !string.IsNullOrEmpty(rnum) ? rnum : rn);
                }
                foreach (var f in _lightingFixtures.Cast<FixtureElement>().Concat(_electricalFixtures))
                {
                    if (!string.IsNullOrEmpty(f.RoomName) || !string.IsNullOrEmpty(f.RoomNumber))
                        roomSet.Add(!string.IsNullOrEmpty(f.RoomNumber) && !string.IsNullOrEmpty(f.RoomName)
                            ? $"{f.RoomNumber} - {f.RoomName}" : !string.IsNullOrEmpty(f.RoomNumber) ? f.RoomNumber : f.RoomName);
                }
                int rooms = roomSet.Count;

                int meshTrays = _cableTraySegments.Count(s => s.TrayType == TrayCategory.MeshCableTray && !s.IsFitting);
                int ladders = _cableTraySegments.Count(s => s.TrayType == TrayCategory.CableLadder && !s.IsFitting);
                int fittings = _cableTraySegments.Count(s => s.IsFitting);
                double totalLen = _cableTraySegments.Where(s => !s.IsFitting).Sum(s => s.OriginalLength) / 1000.0;
                int totalSupports = _cableTraySegments.Sum(s => s.SupportCount);
                int inGroups = _cableTraySegments.Count(s => s.IsInGroup);
                var groupNames = _cableTraySegments.Where(s => s.IsInGroup)
                    .Select(s => s.GroupName).Distinct().ToList();

                txtSummary.Text = $"Found in model:\n" +
                    $"  • Mesh Cable Trays: {meshTrays} segments\n" +
                    $"  • Perforated Trays: {_cableTraySegments.Count(s => s.TrayType == TrayCategory.PerforatedCableTray && !s.IsFitting)} segments\n" +
                    $"  • Non-Perforated Trays: {_cableTraySegments.Count(s => s.TrayType == TrayCategory.NonPerforatedCableTray && !s.IsFitting)} segments\n" +
                    $"  • Cable Ladders: {ladders} segments\n" +
                    $"  • Fiber Trays: {_cableTraySegments.Count(s => s.TrayType == TrayCategory.FiberTray && !s.IsFitting)} segments\n" +
                    $"  • Fittings (bends, tees, etc.): {fittings}\n" +
                    $"  • Lighting Fixtures: {_lightingFixtures.Count}\n" +
                    $"  • Electrical Fixtures: {_electricalFixtures.Count}\n" +
                    $"  • Supports detected: {totalSupports}\n" +
                    $"  • Rooms identified: {rooms}\n" +
                    (inGroups > 0 ? $"  • Elements in groups: {inGroups} ({groupNames.Count} groups)\n" : "") +
                    $"  • Total tray/ladder length: {totalLen:F1}m\n" +
                    $"  • Estimated pieces to order @ {_settings.DefaultSliceLength:F0}mm: " +
                    $"{(int)Math.Ceiling(totalLen * 1000.0 / _settings.DefaultSliceLength)}";

                txtStatus.Text = $"Scan complete - {_cableTraySegments.Count + _lightingFixtures.Count + _electricalFixtures.Count} elements found.";
                progressBar.IsIndeterminate = false;
                progressBar.Value = 100;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error scanning model:\n{ex.Message}", "Scan Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                txtStatus.Text = "Scan failed.";
                progressBar.IsIndeterminate = false;
            }
        }

        private void FilterCollectedElements()
        {
            if (!(chkMeshTray.IsChecked ?? false))
                _cableTraySegments.RemoveAll(s => s.TrayType == TrayCategory.MeshCableTray);
            if (!(chkPerforatedTray.IsChecked ?? false))
                _cableTraySegments.RemoveAll(s => s.TrayType == TrayCategory.PerforatedCableTray);
            if (!(chkNonPerforatedTray.IsChecked ?? false))
                _cableTraySegments.RemoveAll(s => s.TrayType == TrayCategory.NonPerforatedCableTray);
            if (!(chkCableLadder.IsChecked ?? false))
                _cableTraySegments.RemoveAll(s => s.TrayType == TrayCategory.CableLadder);
            if (!(chkFiberTray.IsChecked ?? false))
                _cableTraySegments.RemoveAll(s => s.TrayType == TrayCategory.FiberTray);
            if (!(chkLighting.IsChecked ?? false))
                _lightingFixtures.RemoveAll(f => f.Category == FixtureCategory.LightingFixture);
            if (!(chkEmergency.IsChecked ?? false))
                _lightingFixtures.RemoveAll(f =>
                    f.Category == FixtureCategory.EmergencyLight || f.Category == FixtureCategory.PanicLight);
            if (!(chkSockets.IsChecked ?? false))
                _electricalFixtures.RemoveAll(f =>
                    f.Category == FixtureCategory.Socket || f.Category == FixtureCategory.FusedSpur ||
                    f.Category == FixtureCategory.PresenceDetector);
            if (!(chkJunctionBoxes.IsChecked ?? false))
                _electricalFixtures.RemoveAll(f => f.Category.ToString().StartsWith("JunctionBox"));
        }

        // ═══════════════════════════════════════════════════════════
        // UPDATE COUNTS (writes parameters to Revit elements)
        // ═══════════════════════════════════════════════════════════

        private void BtnUpdateCounts_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_cableTraySegments.Count == 0 && _lightingFixtures.Count == 0 && _electricalFixtures.Count == 0)
                {
                    MessageBox.Show("No elements found. Please scan the model first.",
                        "No Data", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                SaveSettingsFromUI();
                progressBar.IsIndeterminate = false;
                int step = 0, totalSteps = 5;

                var slicingService = new SlicingService(_settings);

                // Step 1: Slice data
                txtStatus.Text = "Slicing cable trays...";
                progressBar.Value = (++step / (double)totalSteps) * 100;
                foreach (var seg in _cableTraySegments) slicingService.SliceSegment(seg);

                // Step 2: Cut trays (optional)
                if (chkCutTrays.IsChecked ?? false)
                {
                    txtStatus.Text = "Cutting cable trays...";
                    progressBar.Value = (++step / (double)totalSteps) * 100;

                    var confirm = MessageBox.Show(
                        $"Cut trays into {_settings.DefaultSliceLength:F0}mm pieces with " +
                        $"{_settings.CouplingGap}mm coupling gaps?\n\nThis modifies the model.",
                        "Cut Cable Trays", MessageBoxButton.YesNo, MessageBoxImage.Warning);

                    if (confirm == MessageBoxResult.Yes)
                    {
                        using var cutTx = new Transaction(_doc, "BOM - Cut Trays");
                        var cutOpts = cutTx.GetFailureHandlingOptions();
                        cutOpts.SetFailuresPreprocessor(new GroupWarningSwallower());
                        cutTx.SetFailureHandlingOptions(cutOpts);
                        cutTx.Start();
                        var cutResult = new TrayCuttingService(_doc, _settings.DefaultSliceLength, _settings.CouplingGap).CutAllTrays(_cableTraySegments);
                        cutTx.Commit();

                        // Write parameter-inheritance diagnostics to the Desktop so we can
                        // see exactly what transferred to the new pieces (source value,
                        // whether the target param existed, and the value after writing).
                        try
                        {
                            if (cutResult.ParamCopyLog.Count > 0)
                            {
                                string copyLogPath = Path.Combine(
                                    Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                                    "CableTrayBOM_ParamCopy.txt");
                                File.WriteAllText(copyLogPath,
                                    "=== PARAMETER INHERITANCE (cut pieces) ===\r\n" +
                                    string.Join("\r\n", cutResult.ParamCopyLog));
                            }
                        }
                        catch { }

                        // Re-scan
                        var reCollector = new RevitElementCollector(_doc);
                        _cableTraySegments = reCollector.CollectCableTrays(_settings);
                        FilterCollectedElements();
                        foreach (var seg in _cableTraySegments) slicingService.SliceSegment(seg);
                    }
                }

                // Step 3: Write parameters
                txtStatus.Text = "Writing joint material values...";
                progressBar.Value = (++step / (double)totalSteps) * 100;

                int ctUpdated = 0, fxUpdated = 0;
                using (var tx = new Transaction(_doc, "BOM - Write Joint Material Parameters"))
                {
                    var opts = tx.GetFailureHandlingOptions();
                    opts.SetFailuresPreprocessor(new GroupWarningSwallower());
                    tx.SetFailureHandlingOptions(opts);
                    tx.Start();
                    var writer = new ParameterWriterService(_doc, _settings);
                    ctUpdated = writer.WriteCableTrayParameters(_cableTraySegments);
                    fxUpdated = writer.WriteFixtureParameters(_lightingFixtures);
                    fxUpdated += writer.WriteFixtureParameters(_electricalFixtures);
                    tx.Commit();
                }

                progressBar.Value = 100;
                txtStatus.Text = $"Counts updated: {ctUpdated} trays, {fxUpdated} fixtures.";

                MessageBox.Show(
                    $"Joint material counts updated!\n\n" +
                    $"Cable Trays/Ladders: {ctUpdated} elements\n" +
                    $"Fixtures: {fxUpdated} elements\n\n" +
                    $"V_Count, V_Count_Connections, and all joint material\n" +
                    $"parameters are now set on each element.",
                    "Update Complete", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error updating counts:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                txtStatus.Text = "Update failed.";
            }
        }

        // ═══════════════════════════════════════════════════════════
        // EXPORT SCHEDULE (Excel export only — no model modification)
        // ═══════════════════════════════════════════════════════════

        private void BtnExportSchedule_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_cableTraySegments.Count == 0 && _lightingFixtures.Count == 0 && _electricalFixtures.Count == 0)
                {
                    MessageBox.Show("No elements found. Please scan the model first.",
                        "No Data", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                SaveSettingsFromUI();
                progressBar.IsIndeterminate = false;
                int step = 0, totalSteps = 3;

                var slicingService = new SlicingService(_settings);
                var jmService = new JointMaterialService(_settings);
                var excelService = new ExcelExportService(_settings);

                // Slice data for schedule
                foreach (var seg in _cableTraySegments) slicingService.SliceSegment(seg);

                // Export Cable Tray BOM
                if (chkExportCableTrayBOM.IsChecked ?? false)
                {
                    txtStatus.Text = "Exporting Cable Tray BOM...";
                    progressBar.Value = (++step / (double)totalSteps) * 100;

                    // Consistent naming: BOM Cable Trays and Ladders - Overall
                    string file = Path.Combine(_outputFolder, "BOM CABLE TRAYS AND LADDERS - OVERALL.xlsx");
                    excelService.ExportCableTrayBOM(file, _cableTraySegments, slicingService, jmService);
                }

                // Export Fixture BOM
                if (chkExportFixtureBOM.IsChecked ?? false)
                {
                    txtStatus.Text = "Exporting Fixture BOM...";
                    progressBar.Value = (++step / (double)totalSteps) * 100;

                    string file = Path.Combine(_outputFolder, "BOM SMALL POWER AND LIGHTING - OVERALL.xlsx");
                    excelService.ExportFixtureBOM(file, _lightingFixtures, _electricalFixtures, jmService);
                }

                progressBar.Value = 100;
                txtStatus.Text = "Schedule export complete.";

                MessageBox.Show(
                    $"Schedule export complete!\n\nFiles saved to:\n{_outputFolder}",
                    "Export Complete", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error exporting schedule:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                txtStatus.Text = "Export failed.";
            }
        }

        // ═══════════════════════════════════════════════════════════
        // GENERATE REVIT SCHEDULES (separate button)
        // ═══════════════════════════════════════════════════════════

        private void BtnRevitSchedules_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_cableTraySegments.Count == 0)
                {
                    MessageBox.Show("No elements found. Please scan the model first.",
                        "No Data", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Collect all detected rooms as display strings
                var roomDisplayNames = new List<string>();
                bool hasUnassigned = false;

                foreach (var seg in _cableTraySegments.Concat(
                    _lightingFixtures.Select(f => new CableTraySegment { RoomName = f.RoomName, RoomNumber = f.RoomNumber }))
                    .Concat(_electricalFixtures.Select(f => new CableTraySegment { RoomName = f.RoomName, RoomNumber = f.RoomNumber })))
                {
                    if (string.IsNullOrEmpty(seg.RoomName) && string.IsNullOrEmpty(seg.RoomNumber))
                    {
                        hasUnassigned = true;
                        continue;
                    }
                    string display = !string.IsNullOrEmpty(seg.RoomNumber) && !string.IsNullOrEmpty(seg.RoomName)
                        ? $"{seg.RoomNumber} - {seg.RoomName}"
                        : !string.IsNullOrEmpty(seg.RoomNumber) ? seg.RoomNumber : seg.RoomName;
                    if (!roomDisplayNames.Contains(display))
                        roomDisplayNames.Add(display);
                }

                roomDisplayNames.Sort();
                if (hasUnassigned)
                    roomDisplayNames.Insert(0, "Unassigned");

                // Show room selection dialog (always — even if only Unassigned exists)
                List<string>? selectedRooms = null;
                if (roomDisplayNames.Count > 0)
                {
                    var roomDlg = new RoomSelectionWindow(roomDisplayNames);
                    if (roomDlg.ShowDialog() != true) return;
                    selectedRooms = roomDlg.SelectedRooms;
                    _unassignedRoomNames = roomDlg.UnassignedNames;
                }

                SaveSettingsFromUI();
                txtStatus.Text = "Creating Revit schedules...";
                progressBar.IsIndeterminate = true;

                CreateRevitSchedules(selectedRooms);

                progressBar.IsIndeterminate = false;
                progressBar.Value = 100;
                txtStatus.Text = "Revit schedules created.";

                MessageBox.Show("Revit schedules created successfully!\n\n" +
                    "Check the Project Browser under Schedules/Quantities.",
                    "Schedules Created", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error creating schedules:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                progressBar.IsIndeterminate = false;
                txtStatus.Text = "Schedule creation failed.";
            }
        }

        // ═══════════════════════════════════════════════════════════
        // REVIT SCHEDULE CREATION
        // ═══════════════════════════════════════════════════════════

        private void CreateRevitSchedules(List<string>? selectedRooms)
        {
            using var tx = new Transaction(_doc, "BOM - Create Revit Schedules");
            tx.Start();

            try
            {
                // Filter out unions from segments for schedule creation
                var nonUnionSegments = _cableTraySegments
                    .Where(s => !IsUnionFitting(s)).ToList();

                // Cable tray schedules by type × mounting
                var schedDefs = GetCableTrayScheduleDefinitions(nonUnionSegments);
                foreach (var sd in schedDefs)
                    CreateScheduleWithFields(sd.Name, BuiltInCategory.OST_CableTray, sd.Fields,
                        typeFilter: sd.TypeFilterKeyword);

                // Fitting schedules (excluding unions)
                var fittingGroups = nonUnionSegments.Where(s => s.IsFitting)
                    .GroupBy(s => new { s.TrayType, s.Mounting }).ToList();
                foreach (var grp in fittingGroups)
                {
                    string typeName = GetTrayTypeName(grp.Key.TrayType);
                    string mountName = GetMountingName(grp.Key.Mounting);
                    string name = $"BOM {typeName} FITTINGS TO {mountName}";
                    string typeKw = GetTypeFilterKeyword(grp.Key.TrayType);
                    var parentDef = schedDefs.FirstOrDefault(d =>
                        d.Name.Contains(typeName) && d.Name.Contains(mountName));
                    CreateScheduleWithFields(name, BuiltInCategory.OST_CableTrayFitting, 
                        parentDef?.Fields ?? GetDefaultCableTrayFields(), typeFilter: typeKw);
                }

                // Per-room schedules: same type×mounting pattern as overall, with room filter
                if (selectedRooms != null && selectedRooms.Count > 0)
                {
                    foreach (var room in selectedRooms)
                    {
                        // Check if this is the "Unassigned" room (originally had no room)
                        bool isUnassigned = _unassignedRoomNames.Contains(room);
                        string roomFilterValue = room; // always use the display name as filter

                        if (isUnassigned)
                        {
                            // Single schedule for all unassigned elements (no type split)
                            CreateScheduleWithFields($"BOM CABLE TRAYS - {room}",
                                BuiltInCategory.OST_CableTray, GetDefaultCableTrayFields(),
                                roomFilter: roomFilterValue);
                            CreateScheduleWithFields($"BOM FITTINGS - {room}",
                                BuiltInCategory.OST_CableTrayFitting, GetDefaultCableTrayFields(),
                                roomFilter: roomFilterValue);
                        }
                        else
                        {
                            foreach (var sd in schedDefs)
                            {
                                CreateScheduleWithFields($"{sd.Name} - {room}",
                                    BuiltInCategory.OST_CableTray, sd.Fields,
                                    roomFilter: roomFilterValue, typeFilter: sd.TypeFilterKeyword);
                            }

                            foreach (var grp in fittingGroups)
                            {
                                string typeName = GetTrayTypeName(grp.Key.TrayType);
                                string mountName = GetMountingName(grp.Key.Mounting);
                                string typeKw = GetTypeFilterKeyword(grp.Key.TrayType);
                                var parentDef = schedDefs.FirstOrDefault(d =>
                                    d.Name.Contains(typeName) && d.Name.Contains(mountName));
                                CreateScheduleWithFields($"BOM {typeName} FITTINGS TO {mountName} - {room}",
                                    BuiltInCategory.OST_CableTrayFitting,
                                    parentDef?.Fields ?? GetDefaultCableTrayFields(),
                                    roomFilter: roomFilterValue, typeFilter: typeKw);
                            }
                        }
                    }
                }

                tx.Commit();
            }
            catch { if (tx.HasStarted()) tx.RollBack(); }
        }

        private static bool IsUnionFitting(CableTraySegment seg)
        {
            if (!seg.IsFitting) return false;
            string desc = (seg.Description ?? "").ToLowerInvariant();
            return desc.Contains("union") || seg.Fitting == FittingType.Straight;
        }

        /// <summary>
        /// Build the list of schedule definitions based on what exists in the model.
        /// Each definition has a name and ordered field list matching the Excel reference.
        /// </summary>
        private List<ScheduleDef> GetCableTrayScheduleDefinitions(List<CableTraySegment> segments)
        {
            var defs = new List<ScheduleDef>();
            var trayGroups = segments.Where(s => !s.IsFitting)
                .GroupBy(s => new { s.TrayType, s.Mounting }).ToList();

            foreach (var grp in trayGroups)
            {
                string typeName = GetTrayTypeName(grp.Key.TrayType);
                string mountName = GetMountingName(grp.Key.Mounting);
                string name = $"BOM {typeName} TO {mountName}";
                string typeKw = GetTypeFilterKeyword(grp.Key.TrayType);

                var fields = GetFieldsForTrayType(grp.Key.TrayType, grp.Key.Mounting);
                defs.Add(new ScheduleDef { Name = name, Fields = fields, TypeFilterKeyword = typeKw });
            }
            return defs;
        }

        private List<(string revitParam, string header)> GetFieldsForTrayType(
            TrayCategory trayType, Models.MountingType mounting)
        {
            // Column order matching Excel export exactly:
            // Number (not a param - handled by Revit row numbering), 
            // Part Number, SAP Code, Manufacturer, Type, Size, Description, Quantity, Length, [JM], Comment, Room
            var fields = new List<(string, string)>
            {
                ("V_Part_Number", "Part Number"),
                ("V_SAP_Code", "SAP Code"),
                ("Manufacturer", "Manufacturer"),
                ("Family and Type", "Type"),
                ("Size", "Size"),
                ("V_BOM_Description", "Description"),
                ("V_Order_Quantity", "Quantity [pcs]"),
                ("Length", "Length [mm]"),
            };

            // Joint material columns per tray type × mounting (matching Excel)
            if (trayType == TrayCategory.MeshCableTray && mounting == Models.MountingType.SupportChannel)
            {
                fields.Add(("V_(137) Bolt DIN 7985 M6x20 ZN", "(137) Bolt DIN 7985 M6x20 ZN"));
                fields.Add(("V_(119) Threaded plate M6 ZN", "(119) Threaded plate M6 ZN"));
                fields.Add(("V_(100) Clamp for screw M6", "(100) Clamp for screw M6"));
                fields.Add(("V_(99) Clamping element GKS34FT", "(99) Clamping element GKS34FT"));
                fields.Add(("V_(124) Clamping element GSV34FT", "(124) Clamping element GSV34FT"));
            }
            else if (trayType == TrayCategory.CableLadder && mounting == Models.MountingType.SupportChannel)
            {
                fields.Add(("V_(137) Bolt DIN 7985 M6x20 ZN", "(137) Bolt DIN 7985 M6x20 ZN"));
                fields.Add(("V_(23) Washer DIN 9021 M6 ZN", "(23) Washer DIN 9021 M6 ZN"));
                fields.Add(("V_(119) Threaded plate M6 ZN", "(119) Threaded plate M6 ZN"));
                fields.Add(("V_Containment_Straight_Connector", "(N/A) Straight connector"));
                fields.Add(("V_Containment_Truss-head Bolt with Nut 6x12", "(N/A) Truss-head bolt with nut 6x12"));
            }
            else if (trayType == TrayCategory.PerforatedCableTray && mounting == Models.MountingType.SupportChannel)
            {
                fields.Add(("V_(137) Bolt DIN 7985 M6x20 ZN", "(137) Bolt DIN 7985 M6x20 ZN"));
                fields.Add(("V_(23) Washer DIN 9021 M6 ZN", "(23) Washer DIN 9021 M6 ZN"));
                fields.Add(("V_(119) Threaded plate M6 ZN", "(119) Threaded plate M6 ZN"));
                fields.Add(("V_Containment_Straight_Connector", "(N/A) Straight connector"));
                fields.Add(("V_JointPlate", "(N/A) Joint plate"));
                fields.Add(("V_Containment_Truss-head Bolt with Nut 6x12", "(N/A) Truss-head bolt with nut 6x12"));
            }
            else if (trayType == TrayCategory.NonPerforatedCableTray && mounting == Models.MountingType.SupportChannel)
            {
                fields.Add(("V_(137) Bolt DIN 7985 M6x20 ZN", "(137) Bolt DIN 7985 M6x20 ZN"));
                fields.Add(("V_(23) Washer DIN 9021 M6 ZN", "(23) Washer DIN 9021 M6 ZN"));
                fields.Add(("V_(119) Threaded plate M6 ZN", "(119) Threaded plate M6 ZN"));
                fields.Add(("V_Containment_Truss-head bolt with flange nut 6x16", "Truss-head bolt with flange nut 6x16"));
            }
            else if (trayType == TrayCategory.MeshCableTray && mounting == Models.MountingType.Console)
            {
                fields.Add(("V_(21) Bolt DIN 7985 M6x25 ZN", "(21) Bolt DIN 7985 M6x25 ZN"));
                fields.Add(("V_(100) Clamp for screw M6", "(100) Clamp for screw M6"));
                fields.Add(("V_(33) Nut DIN 934 M6 ZN", "(33) Nut DIN 934 M6 ZN"));
                fields.Add(("V_(99) Clamping element GKS34FT", "(99) Clamping element GKS34FT"));
                fields.Add(("V_(124) Clamping element GSV34FT", "(124) Clamping element GSV34FT"));
            }
            else if (trayType == TrayCategory.FiberTray && mounting == Models.MountingType.Console)
            {
                fields.Add(("V_(66) Bolt DIN 933 M6x12 8.8 ZN", "(66) Bolt DIN 933 M6x12 8.8 ZN"));
                fields.Add(("V_(101) Rail nut M6 18X18", "(101) Rail nut M6 18X18"));
                fields.Add(("V_(4) Washer DIN 125 M6 ZN", "(4) Washer DIN 125 M6 ZN"));
                fields.Add(("V_(N/A) Fibre Runner Coupler", "(N/A) Fibre Runner Coupler"));
            }
            else
            {
                fields.AddRange(GetDefaultJointMaterialFields());
            }

            // Comment then Room Name (Room after Comment as requested)
            fields.Add(("Comments", "Comment"));
            fields.Add(("V_Room_Name", "Room"));

            return fields;
        }

        private List<(string, string)> GetDefaultJointMaterialFields()
        {
            return new List<(string, string)>
            {
                ("V_(137) Bolt DIN 7985 M6x20 ZN", "(137) Bolt DIN 7985 M6x20 ZN"),
                ("V_(119) Threaded plate M6 ZN", "(119) Threaded plate M6 ZN"),
                ("V_(23) Washer DIN 9021 M6 ZN", "(23) Washer DIN 9021 M6 ZN"),
                ("V_(100) Clamp for screw M6", "(100) Clamp for screw M6"),
                ("V_(99) Clamping element GKS34FT", "(99) Clamping element GKS34FT"),
                ("V_(124) Clamping element GSV34FT", "(124) Clamping element GSV34FT"),
            };
        }

        private List<(string, string)> GetDefaultCableTrayFields()
        {
            var fields = new List<(string, string)>
            {
                ("V_Part_Number", "Part Number"),
                ("V_SAP_Code", "SAP Code"),
                ("Manufacturer", "Manufacturer"),
                ("Family and Type", "Type"),
                ("Size", "Size"),
                ("V_BOM_Description", "Description"),
                ("V_Order_Quantity", "Quantity [pcs]"),
                ("Length", "Length [mm]"),
            };
            fields.AddRange(GetDefaultJointMaterialFields());
            fields.Add(("Comments", "Comment"));
            fields.Add(("V_Room_Name", "Room"));
            return fields;
        }

        private void CreateScheduleWithFields(string scheduleName, BuiltInCategory category,
            List<(string revitParam, string header)> fieldDefs,
            string? roomFilter = null, string? typeFilter = null)
        {
            scheduleName = scheduleName.ToUpperInvariant();

            foreach (var old in new FilteredElementCollector(_doc)
                .OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>()
                .Where(v => v.Name == scheduleName).ToList())
                _doc.Delete(old.Id);

            var schedule = ViewSchedule.CreateSchedule(_doc, new ElementId(category));
            schedule.Name = scheduleName;

            // Set V_View_Category for project browser organization
            try
            {
                var viewCatParam = schedule.LookupParameter("V_View Category");
                if (viewCatParam != null && !viewCatParam.IsReadOnly && viewCatParam.StorageType == StorageType.String)
                    viewCatParam.Set("99_BOM_SCHEDULES");
            }
            catch { }

            var def = schedule.Definition;
            var availableFields = def.GetSchedulableFields();
            ScheduleFieldId? roomFieldId = null;
            ScheduleFieldId? sizeFieldId = null;
            ScheduleFieldId? typeFieldId = null;

            // Parameters that should show totals
            var totalParams = new HashSet<string>
            {
                "V_Count", "V_Count_Connections", "V_Order_Quantity", "Length",
                "(137)", "(119)", "(100)", "(99)", "(124)",
                "(23)", "(21)", "(33)", "(66)", "(101)", "(4)",
                "(85)", "(132)", "(133)", "(134)", "(139)",
                "(38)", "(92)",
                "Straight_Connector", "Straight connector", "Truss-head", "Truss_head",
                "JointPlate", "Joint plate", "Fibre Runner", "Fibre",
                "Flexible", "Steel Conduit"
            };

            // Add visible fields
            ScheduleFieldId? lengthFieldId = null;

            foreach (var (revitParam, header) in fieldDefs)
            {
                var field = availableFields.FirstOrDefault(f =>
                {
                    string fn = f.GetName(_doc);
                    if (fn == revitParam) return true;
                    if (fn.Contains(revitParam)) return true;
                    if (revitParam.Contains(fn)) return true;
                    // Handle V_ prefix mismatch: try without V_ prefix
                    string rp = revitParam.StartsWith("V_") ? revitParam.Substring(2) : revitParam;
                    string fnClean = fn.StartsWith("V_") ? fn.Substring(2) : fn;
                    return fnClean == rp || fn.Contains(rp) || fn == "V_" + rp;
                });
                if (field != null)
                {
                    try
                    {
                        var addedField = def.AddField(field);
                        if (addedField == null) continue;

                        if (!string.IsNullOrEmpty(header))
                            addedField.ColumnHeading = header;

                        if (totalParams.Any(tp => revitParam.Contains(tp)))
                            try { addedField.DisplayType = ScheduleFieldDisplayType.Totals; } catch { }

                        if (revitParam == "V_Room_Name")
                            roomFieldId = addedField.FieldId;
                        if (revitParam == "Length")
                            lengthFieldId = addedField.FieldId;
                    }
                    catch { }
                }
            }

            // Add Size as hidden field for sorting
            var sizeSchField = availableFields.FirstOrDefault(f =>
                f.GetName(_doc) == "Size" || f.GetName(_doc) == "size");
            if (sizeSchField != null)
            {
                try
                {
                    var addedSize = def.AddField(sizeSchField);
                    if (addedSize != null)
                    {
                        addedSize.IsHidden = true;
                        sizeFieldId = addedSize.FieldId;
                    }
                }
                catch { }
            }

            // Add Service Type as hidden field for sorting
            ScheduleFieldId? serviceTypeFieldId = null;
            var serviceTypeSchField = availableFields.FirstOrDefault(f =>
                f.GetName(_doc) == "Service Type" || f.GetName(_doc) == "V_Service_Type");
            if (serviceTypeSchField != null)
            {
                try
                {
                    var addedServiceType = def.AddField(serviceTypeSchField);
                    if (addedServiceType != null)
                    {
                        addedServiceType.IsHidden = true;
                        serviceTypeFieldId = addedServiceType.FieldId;
                    }
                }
                catch { }
            }

            // Add Type as hidden field for filtering
            var typeSchField = availableFields.FirstOrDefault(f =>
                f.GetName(_doc) == "Type" || f.GetName(_doc) == "Family and Type");
            if (typeSchField != null)
            {
                try
                {
                    var addedType = def.AddField(typeSchField);
                    if (addedType != null)
                    {
                        addedType.IsHidden = true;
                        typeFieldId = addedType.FieldId;
                    }
                }
                catch { }
            }

            // Sort order: Service Type first, then Size
            // Service Type groups elements by service, Size sub-groups within each service
            if (serviceTypeFieldId != null)
            {
                try
                {
                    var stSort = new ScheduleSortGroupField(serviceTypeFieldId);
                    stSort.ShowFooter = true;
                    stSort.ShowFooterTitle = true;
                    stSort.ShowBlankLine = true;
                    def.AddSortGroupField(stSort);
                }
                catch { }
            }

            if (sizeFieldId != null)
            {
                try
                {
                    var sizeSort = new ScheduleSortGroupField(sizeFieldId);
                    sizeSort.ShowFooter = true;
                    sizeSort.ShowFooterTitle = true;
                    sizeSort.ShowBlankLine = true;
                    def.AddSortGroupField(sizeSort);
                }
                catch { }
            }

            // Type filter: filter by type name containing the keyword
            if (!string.IsNullOrEmpty(typeFilter) && typeFieldId != null)
            {
                try
                {
                    def.AddFilter(new ScheduleFilter(typeFieldId,
                        ScheduleFilterType.Contains, typeFilter));
                }
                catch { }
            }

            // Room filter
            if (!string.IsNullOrEmpty(roomFilter) && roomFieldId != null)
            {
                try
                {
                    def.AddFilter(new ScheduleFilter(roomFieldId,
                        ScheduleFilterType.Equal, roomFilter));
                }
                catch { }
            }

            // Grand total via last sort group footer (already handled by Size sort footer)

            // Highlight lengths above standard slice length in red
            // Uses TableSectionData to color individual cells after schedule is built
            if (lengthFieldId != null)
            {
                try
                {
                    // Force schedule to regenerate so we can access body data
                    _doc.Regenerate();

                    var tableData = schedule.GetTableData();
                    var bodySection = tableData.GetSectionData(SectionType.Body);
                    int numRows = bodySection.NumberOfRows;
                    int numCols = bodySection.NumberOfColumns;

                    // Find which column index corresponds to the Length field
                    var headerSection = tableData.GetSectionData(SectionType.Header);
                    int lengthColIdx = -1;

                    // Check field order to find Length column
                    int fieldCount = def.GetFieldCount();
                    int visibleCol = 0;
                    for (int fi = 0; fi < fieldCount; fi++)
                    {
                        var f = def.GetField(fi);
                        if (f.IsHidden) continue;
                        if (f.FieldId == lengthFieldId) { lengthColIdx = visibleCol; break; }
                        visibleCol++;
                    }

                    if (lengthColIdx >= 0 && lengthColIdx < numCols)
                    {
                        double sliceLenFt = UnitUtils.ConvertToInternalUnits(
                            _settings.DefaultSliceLength, UnitTypeId.Millimeters);

                        for (int row = 0; row < numRows; row++)
                        {
                            try
                            {
                                string cellVal = bodySection.GetCellText(row, lengthColIdx);
                                if (double.TryParse(cellVal.Replace(" ", "").Replace("mm", ""),
                                    System.Globalization.NumberStyles.Any,
                                    System.Globalization.CultureInfo.InvariantCulture, out double lenVal))
                                {
                                    // Cell text is in display units (mm), compare directly
                                    if (lenVal > _settings.DefaultSliceLength)
                                    {
                                        var style = new TableCellStyle();
                                        style.BackgroundColor = new Color(255, 120, 120);
                                        bodySection.SetCellStyle(row, lengthColIdx, style);
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch
                {
                    // Cell styling may fail if schedule has no data yet
                }
            }
        }

        private static string GetTrayTypeName(TrayCategory cat) => cat switch
        {
            TrayCategory.MeshCableTray => "MESH CABLE TRAY",
            TrayCategory.CableLadder => "CABLE LADDER",
            TrayCategory.PerforatedCableTray => "PERFORATED CABLE TRAY",
            TrayCategory.NonPerforatedCableTray => "NON-PERFORATED CABLE TRAY",
            TrayCategory.FiberTray => "FIBER TRAY",
            _ => "CABLE TRAY"
        };

        /// <summary>
        /// Get a keyword that appears in the Revit Type name for filtering.
        /// This is used in ScheduleFilter with Contains to match the right elements.
        /// </summary>
        private static string GetTypeFilterKeyword(TrayCategory cat) => cat switch
        {
            TrayCategory.MeshCableTray => "Mesh",
            TrayCategory.CableLadder => "Ladder",
            TrayCategory.PerforatedCableTray => "Perforated",
            TrayCategory.NonPerforatedCableTray => "Non-Perforated",
            TrayCategory.FiberTray => "Fiber",
            _ => ""
        };

        private static string GetMountingName(Models.MountingType mount) => mount switch
        {
            Models.MountingType.SupportChannel => "SUPPORT CHANNEL",
            Models.MountingType.Console => "CONSOLE",
            Models.MountingType.Panel => "PANEL",
            Models.MountingType.Steel => "STEEL",
            _ => "SUPPORT CHANNEL"
        };

        private class ScheduleDef
        {
            public string Name { get; set; } = "";
            public List<(string revitParam, string header)> Fields { get; set; } = new();
            public string TypeFilterKeyword { get; set; } = ""; // filter Type name contains this
        }

        // ═══════════════════════════════════════════════════════════
        // CHECK & IMPORT PARAMETERS (combined button)
        // ═══════════════════════════════════════════════════════════

        private void BtnCheckImportParams_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var (checkMsg, missingCount, totalCount) = RunParameterCheck();

                if (missingCount == 0)
                {
                    ScrollableResultWindow.Show(checkMsg, "All Parameters OK");
                    return;
                }

                string prompt = checkMsg +
                    $"\n────────────────────────────────\n" +
                    $"{missingCount} parameter(s) missing.\n\n" +
                    "Import missing parameters from the shared parameter file?";

                var answer = MessageBox.Show(prompt,
                    $"Missing Parameters ({missingCount}/{totalCount})",
                    MessageBoxButton.YesNo, MessageBoxImage.Question);

                if (answer != MessageBoxResult.Yes) return;

                BtnImportParams_Click(sender, e);

                // Re-check
                var (recheckMsg, recheckMissing, _) = RunParameterCheck();
                ScrollableResultWindow.Show(recheckMsg,
                    recheckMissing == 0 ? "All Parameters OK" : $"Still Missing: {recheckMissing}",
                    recheckMissing > 0);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private (string msg, int missing, int total) RunParameterCheck()
        {
            var commonParams = new[] {
                "V_Count", "V_Count_Connections", "V_Mounting_Type",
                "V_Room_Name", "V_Room Name Manual",
                "V_BOM_Description", "V_Part_Number", "V_SAP_Code",
                "V_Order_Quantity", "V_Service Type",
            };
            var cableTrayParams = new[] {
                "V_(137) Bolt DIN 7985 M6x20 ZN", "V_(119) Threaded plate M6 ZN",
                "V_(100) Clamp for screw M6", "V_(99) Clamping element GKS34FT",
                "V_(124) Clamping element GSV34FT", "V_(23) Washer DIN 9021 M6 ZN",
                "V_(21) Bolt DIN 7985 M6x25 ZN", "V_(33) Nut DIN 934 M6 ZN",
                "V_(66) Bolt DIN 933 M6x12 8.8 ZN", "V_(101) Rail nut M6 18X18",
                "V_(4) Washer DIN 125 M6 ZN", "V_(N/A) Fibre Runner Coupler",
                "V_Containment_Straight_Connector",
                "V_Containment_Truss-head Bolt with Nut 6x12",
                "V_Containment_Truss-head bolt with flange nut 6x16",
                "V_JointPlate",
            };
            var fixtureParams = new[] {
                "V_(85) ISO 7380 MF M6x20 10.9 ZN",
                "V_(132) SIDE HOLDER FOR CABLE GLAND 25mm",
                "V_(133) LOCKNUT 25mm FOR FLEXIBLE CONDUIT GLAND",
                "V_(134) MALE FIXED FITTING + RING 25mm",
                "V_(139) RUBBER END GZ16",
                "V_Flexible Conduit", "V_Steel Conduit",
            };
            var paramGuids = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase)
            {
                ["V_Count"] = new("7883dc6a-dd5c-4c65-bbb3-7680c9802ac3"),
                ["V_Count_Connections"] = new("6fb1e3bf-cae5-4a91-8711-25edc7a98ad2"),
                ["V_Mounting_Type"] = new("2c34789a-487d-411f-840d-d203b91a2ba6"),
                ["V_Room_Name"] = new("3cc5d8d1-f441-4c65-ae3a-528a9092a7cf"),
                ["V_Room Name Manual"] = new("3b3d6ba9-6720-4007-a0ff-9d824010ea8f"),
                ["V_BOM_Description"] = new("69093450-edcd-4ef3-a642-dd5ad54f8e7a"),
                ["V_Part_Number"] = new("2eeca9ad-ed38-4a9e-ba54-11579469476b"),
                ["V_SAP_Code"] = new("e630049b-c64c-4afb-8bc4-039232ba43be"),
                ["V_Order_Quantity"] = new("2493ecab-5797-470d-ad5b-389c42781bbf"),
                ["V_Service Type"] = new("3f2bdad0-fcc1-4067-afe4-8539d461f868"),
                ["V_(137) Bolt DIN 7985 M6x20 ZN"] = new("5e6cb0e2-06ef-402f-9df8-865fa9f32d34"),
                ["V_(119) Threaded plate M6 ZN"] = new("f471f316-4f93-4f14-8136-b614268df6d9"),
                ["V_(100) Clamp for screw M6"] = new("f98a3c0f-c06e-4e77-9546-8998cda03d9d"),
                ["V_(99) Clamping element GKS34FT"] = new("0b834f68-b8b5-4aa5-85ce-537c761eafcc"),
                ["V_(124) Clamping element GSV34FT"] = new("f1a45694-d7f7-4114-b22f-c4432b5edcc7"),
                ["V_(23) Washer DIN 9021 M6 ZN"] = new("664f1764-aded-4744-96d9-0379e2ac4e2e"),
                ["V_(21) Bolt DIN 7985 M6x25 ZN"] = new("a7be6015-591d-4a0d-bb95-8f5d9a7edbbb"),
                ["V_(33) Nut DIN 934 M6 ZN"] = new("2b984768-c3ab-4778-8727-491648eb48cc"),
                ["V_(66) Bolt DIN 933 M6x12 8.8 ZN"] = new("cbd9a081-f898-4fb7-842e-933182f50000"),
                ["V_(101) Rail nut M6 18X18"] = new("44748e83-cbe5-408c-808a-2fde8d27890e"),
                ["V_(4) Washer DIN 125 M6 ZN"] = new("335df1c1-8f8d-4394-9e6f-cdcdd797de05"),
                ["V_(N/A) Fibre Runner Coupler"] = new("f0db8947-024a-4bc4-9a89-f048815f5bb7"),
                ["V_Containment_Straight_Connector"] = new("ff6d7f9b-f9e0-4cfa-b875-786580d2d321"),
                ["V_Containment_Truss-head Bolt with Nut 6x12"] = new("973dd2e2-899c-4a58-98c7-65da6fd5a9bc"),
                ["V_Containment_Truss-head bolt with flange nut 6x16"] = new("b38a7209-4613-4b5e-a147-9bbb78a59c45"),
                ["V_JointPlate"] = new("5988ad5a-ba99-4aa5-b127-eb553907fab3"),
                ["V_(85) ISO 7380 MF M6x20 10.9 ZN"] = new("acec007a-6bd0-4ffb-ab5a-46f118ff5544"),
                ["V_(132) SIDE HOLDER FOR CABLE GLAND 25mm"] = new("99706329-c9d2-45e1-a18d-25b0c65e1489"),
                ["V_(133) LOCKNUT 25mm FOR FLEXIBLE CONDUIT GLAND"] = new("84a4a96f-662a-491f-8d74-b0e187186dab"),
                ["V_(134) MALE FIXED FITTING + RING 25mm"] = new("2ef3a787-4fcc-4e2f-a0fc-70c3f66e00de"),
                ["V_(139) RUBBER END GZ16"] = new("70e08a16-767c-441e-a3a3-c168d9c79295"),
                ["V_Flexible Conduit"] = new("769c5543-5a29-475b-af0b-e900f6422c18"),
                ["V_Steel Conduit"] = new("e04ad3ad-dd8d-445a-87f9-7fe8bb0be9c3"),
            };

            var ctElem = new FilteredElementCollector(_doc)
                .OfClass(typeof(Autodesk.Revit.DB.Electrical.CableTray))
                .WhereElementIsNotElementType().FirstOrDefault()
                ?? new FilteredElementCollector(_doc)
                .OfCategory(BuiltInCategory.OST_CableTrayFitting)
                .WhereElementIsNotElementType().FirstOrDefault();
            var lfElem = new FilteredElementCollector(_doc)
                .OfCategory(BuiltInCategory.OST_LightingFixtures)
                .WhereElementIsNotElementType().FirstOrDefault()
                ?? new FilteredElementCollector(_doc)
                .OfCategory(BuiltInCategory.OST_ElectricalFixtures)
                .WhereElementIsNotElementType().FirstOrDefault();

            bool Chk(Element? elem, string name)
            {
                if (elem == null) return false;
                if (paramGuids.TryGetValue(name, out Guid g) && elem.get_Parameter(g) != null) return true;
                return elem.LookupParameter(name) != null;
            }

            var missing = new List<string>(); var found = new List<string>();
            var results = new List<(string n, string s, string c)>();

            foreach (var p in commonParams)
            { bool ok = Chk(ctElem, p) || Chk(lfElem, p); results.Add((p, ok?"OK":"MISSING", "Common")); if (ok) found.Add(p); else missing.Add(p); }
            foreach (var p in cableTrayParams)
            { bool ok = Chk(ctElem, p); results.Add((p, ok?"OK":"MISSING", "Cable Tray")); if (ok) found.Add(p); else missing.Add(p); }
            foreach (var p in fixtureParams)
            { bool ok = Chk(lfElem, p); results.Add((p, ok?"OK":"MISSING", lfElem!=null?"Fixture":"Fixture (no element)")); if (ok) found.Add(p); else missing.Add(p); }

            int total = commonParams.Length + cableTrayParams.Length + fixtureParams.Length;
            string msg = $"Parameter Check Results:\n" +
                $"  Cable Tray test: {(ctElem != null ? $"ID {ctElem.Id.Value}" : "NONE")}\n" +
                $"  Fixture test: {(lfElem != null ? $"ID {lfElem.Id.Value}" : "NONE")}\n\n" +
                $"Found: {found.Count} / {total}\nMissing: {missing.Count}\n";
            string lastCat = "";
            foreach (var (n, s, c) in results)
            { if (c != lastCat) { msg += $"\n  --- {c} ---\n"; lastCat = c; } msg += $"  [{s}]  {n}\n"; }

            return (msg, missing.Count, total);
        }

        // ═══════════════════════════════════════════════════════════
        // IMPORT SHARED PARAMETERS
        // ═══════════════════════════════════════════════════════════

        private void BtnImportParams_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Select Shared Parameter File",
                    Filter = "Shared Parameter Files (*.txt)|*.txt|All Files (*.*)|*.*",
                    DefaultExt = ".txt"
                };
                if (dlg.ShowDialog() != true) return;

                string filePath = dlg.FileName;

                // Build category sets
                var catCableTray = _doc.Application.Create.NewCategorySet();
                var catFixture = _doc.Application.Create.NewCategorySet();
                var catAll = _doc.Application.Create.NewCategorySet();

                var ctCat = Category.GetCategory(_doc, BuiltInCategory.OST_CableTray);
                var ctfCat = Category.GetCategory(_doc, BuiltInCategory.OST_CableTrayFitting);
                var lfCat = Category.GetCategory(_doc, BuiltInCategory.OST_LightingFixtures);
                var efCat = Category.GetCategory(_doc, BuiltInCategory.OST_ElectricalFixtures);

                if (ctCat != null) { catCableTray.Insert(ctCat); catAll.Insert(ctCat); }
                if (ctfCat != null) { catCableTray.Insert(ctfCat); catAll.Insert(ctfCat); }
                if (lfCat != null) { catFixture.Insert(lfCat); catAll.Insert(lfCat); }
                if (efCat != null) { catFixture.Insert(efCat); catAll.Insert(efCat); }

                // Save and temporarily set shared parameter file
                string? originalFile = _doc.Application.SharedParametersFilename;
                _doc.Application.SharedParametersFilename = filePath;

                DefinitionFile? defFile = null;
                try { defFile = _doc.Application.OpenSharedParameterFile(); }
                catch { }

                if (defFile == null)
                {
                    _doc.Application.SharedParametersFilename = originalFile ?? "";
                    MessageBox.Show("Could not open the shared parameter file.",
                        "Invalid File", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // Fixture-specific parameter name patterns
                var fixtureOnlyPatterns = new[] {
                    "(85)", "(132)", "(133)", "(134)", "(139)",
                    "(38)", "(92)",
                    "Flexible Conduit", "Steel Conduit",
                    "ISO 7380", "SIDE HOLDER", "LOCKNUT", "MALE FIXED",
                    "RUBBER END", "MOUNTING PLATE", "JF2"
                };

                // Cable tray-specific parameter name patterns
                var cableTrayOnlyPatterns = new[] {
                    "(137)", "(119)", "(100)", "(99)", "(124)",
                    "(23)", "(21)", "(33)", "(66)", "(101)", "(4)",
                    "(N/A) Fibre",
                    "GKS34FT", "GSV34FT", "Straight_Connector", "Truss-head",
                    "JointPlate", "Fibre Runner", "flange nut",
                    "V_Count", "V_Count_Connections", "V_Order_Quantity"
                };

                int imported = 0, skipped = 0, failed = 0, ignored = 0;
                var results = new List<string>();

                // Only import parameters the addin actually uses
                // Names must EXACTLY match VERTIV-Shared_Parameters.txt
                var requiredNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    // Common parameters (All categories)
                    "V_Count", "V_Count_Connections", "V_Mounting_Type",
                    "V_Room_Name", "V_Room Name Manual",
                    "V_BOM_Description", "V_Part_Number", "V_SAP_Code",
                    "V_Order_Quantity", "V_Service Type",
                    // Cable tray joint materials (GROUP 25 G-Suspension)
                    "V_(137) Bolt DIN 7985 M6x20 ZN",
                    "V_(119) Threaded plate M6 ZN",
                    "V_(100) Clamp for screw M6",
                    "V_(99) Clamping element GKS34FT",
                    "V_(124) Clamping element GSV34FT",
                    "V_(23) Washer DIN 9021 M6 ZN",
                    "V_(21) Bolt DIN 7985 M6x25 ZN",
                    "V_(33) Nut DIN 934 M6 ZN",
                    "V_(66) Bolt DIN 933 M6x12 8.8 ZN",
                    "V_(101) Rail nut M6 18X18",
                    "V_(4) Washer DIN 125 M6 ZN",
                    "V_(N/A) Fibre Runner Coupler",
                    "V_Containment_Straight_Connector",
                    "V_Containment_Truss-head Bolt with Nut 6x12",
                    "V_Containment_Truss-head bolt with flange nut 6x16",
                    "V_JointPlate",
                    // Fixture joint materials
                    "V_(85) ISO 7380 MF M6x20 10.9 ZN",
                    "V_(132) SIDE HOLDER FOR CABLE GLAND 25mm",
                    "V_(133) LOCKNUT 25mm FOR FLEXIBLE CONDUIT GLAND",
                    "V_(134) MALE FIXED FITTING + RING 25mm",
                    "V_(139) RUBBER END GZ16",
                    "V_(38) JF2-2-5.5X25-V16ZN",
                    "V_(38) JF2-2-5.5x25-V16",
                    "V_(92) MOUNTING PLATE FOR MESH CABLE TRAY MPG 90 FT",
                    "V_Flexible Conduit",
                    "V_Steel Conduit",
                };

                using (var tx = new Transaction(_doc, "Import BOM Shared Parameters"))
                {
                    tx.Start();

                    foreach (DefinitionGroup grp in defFile.Groups)
                    {
                        foreach (ExternalDefinition extDef in grp.Definitions)
                        {
                            // Skip parameters the addin doesn't use
                            if (!requiredNames.Contains(extDef.Name))
                            {
                                ignored++;
                                continue;
                            }

                            // Check current binding state
                            string pName = extDef.Name;
                            CategorySet targetCats;
                            string catLabel;

                            if (fixtureOnlyPatterns.Any(p => pName.Contains(p)))
                            { targetCats = catFixture; catLabel = "Fixtures"; }
                            else if (cableTrayOnlyPatterns.Any(p => pName.Contains(p)))
                            { targetCats = catCableTray; catLabel = "Cable Trays"; }
                            else
                            { targetCats = catAll; catLabel = "All"; }

                            // Test if parameter is ACTUALLY accessible on target category elements
                            // (don't trust binding map — it can say "bound" but element can't see it)
                            bool actuallyWorking = true;
                            var testCategories = new[] {
                                BuiltInCategory.OST_CableTray,
                                BuiltInCategory.OST_CableTrayFitting,
                                BuiltInCategory.OST_LightingFixtures,
                                BuiltInCategory.OST_ElectricalFixtures
                            };
                            foreach (Category cat in targetCats)
                            {
                                var bic = testCategories.FirstOrDefault(tc =>
                                    Category.GetCategory(_doc, tc)?.Id == cat.Id);
                                if (bic == default) continue;

                                var testElem = new FilteredElementCollector(_doc)
                                    .OfCategory(bic).WhereElementIsNotElementType().FirstOrDefault();
                                if (testElem != null)
                                {
                                    var p = testElem.get_Parameter(extDef.GUID);
                                    if (p == null) { actuallyWorking = false; break; }
                                }
                            }

                            if (actuallyWorking)
                            {
                                results.Add($"[SKIP] {extDef.Name} (working on {catLabel})");
                                skipped++;

                                // Still ensure VaryByGroup
                                try
                                {
                                    var varyIter = _doc.ParameterBindings.ForwardIterator();
                                    while (varyIter.MoveNext())
                                    {
                                        if (varyIter.Key.Name == extDef.Name &&
                                            varyIter.Key is InternalDefinition vid && !vid.VariesAcrossGroups)
                                        { try { vid.SetAllowVaryBetweenGroups(_doc, true); } catch { } break; }
                                    }
                                }
                                catch { }
                                continue;
                            }

                            // Parameter not working — fix binding
                            // Strategy: try Insert, if fails try ReInsert, if fails try Remove+Insert
                            try
                            {
                                var binding = _doc.Application.Create.NewInstanceBinding(targetCats);
                                bool added = false;
                                string method = "";

                                // Attempt 1: straight Insert
                                try
                                {
                                    added = _doc.ParameterBindings.Insert(extDef, binding, RevitCompat.DataGroup);
                                    if (added) method = "Insert";
                                }
                                catch { }

                                // Attempt 2: ReInsert (binding exists but wrong categories)
                                if (!added)
                                {
                                    try
                                    {
                                        var reIter = _doc.ParameterBindings.ForwardIterator();
                                        while (reIter.MoveNext())
                                        {
                                            if (reIter.Key.Name == extDef.Name)
                                            {
                                                added = _doc.ParameterBindings.ReInsert(
                                                    reIter.Key, binding, RevitCompat.DataGroup);
                                                if (added) method = "ReInsert";
                                                break;
                                            }
                                        }
                                    }
                                    catch { }
                                }

                                // Attempt 3: Remove then Insert
                                if (!added)
                                {
                                    try
                                    {
                                        var rmIter = _doc.ParameterBindings.ForwardIterator();
                                        Definition? existDef = null;
                                        while (rmIter.MoveNext())
                                        {
                                            if (rmIter.Key.Name == extDef.Name)
                                            { existDef = rmIter.Key; break; }
                                        }
                                        if (existDef != null)
                                            _doc.ParameterBindings.Remove(existDef);

                                        added = _doc.ParameterBindings.Insert(
                                            extDef, binding, RevitCompat.DataGroup);
                                        if (added) method = "Remove+Insert";
                                    }
                                    catch { }
                                }

                                if (added)
                                {
                                    // Set VaryByGroup
                                    try
                                    {
                                        var iter2 = _doc.ParameterBindings.ForwardIterator();
                                        while (iter2.MoveNext())
                                        {
                                            if (iter2.Key.Name == extDef.Name &&
                                                iter2.Key is InternalDefinition intDef)
                                            {
                                                try { intDef.SetAllowVaryBetweenGroups(_doc, true); }
                                                catch { }
                                                break;
                                            }
                                        }
                                    }
                                    catch { }

                                    results.Add($"[OK] {extDef.Name} -> {catLabel} ({method})");
                                    imported++;
                                }
                                else
                                {
                                    results.Add($"[FAIL] {extDef.Name} (all binding methods failed)");
                                    failed++;
                                }
                            }
                            catch (Exception ex)
                            {
                                results.Add($"[FAIL] {extDef.Name} ({ex.Message})");
                                failed++;
                            }
                        }
                    }

                    tx.Commit();
                }

                _doc.Application.SharedParametersFilename = originalFile ?? "";

                string summary = $"Import Complete!\n\n" +
                    $"Imported: {imported}\nSkipped (already exist): {skipped}\n" +
                    $"Ignored (not needed): {ignored}\nFailed: {failed}\n\n" +
                    $"Category Assignment:\n" +
                    $"  Cable Tray params -> Cable Trays + Cable Tray Fittings\n" +
                    $"  Fixture params -> Lighting Fixtures + Electrical Fixtures\n" +
                    $"  Common params -> All 4 categories\n\n" +
                    $"Settings: Instance, Data group, Vary by group = Yes\n\n" +
                    "DETAILS:\n" + string.Join("\n", results);

                ScrollableResultWindow.Show(summary,
                    failed > 0 ? "Import Complete (with errors)" : "Import Complete",
                    failed > 0);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error importing parameters:\n{ex.Message}", "Import Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ═══════════════════════════════════════════════════════════
        // CHECK / CREATE CABLE TRAY TYPES
        // ═══════════════════════════════════════════════════════════

        private void BtnCheckTypes_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var svc = new CableTrayTypeService(_doc);
                var check = svc.CheckTypes();

                bool hasIssues = check.Missing.Count > 0 || check.HasRoutingIssues;

                if (!hasIssues)
                {
                    MessageBox.Show(
                        $"All cable tray types and routing preferences are correct!\n\n{check.Summary}",
                        "Cable Tray Types OK", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // Show issues and offer to fix
                string msg = check.Summary + "\n\n";

                if (check.Missing.Count > 0)
                    msg += $"Missing types will be created by duplicating an existing Channel Cable Tray type.\n";
                if (check.HasRoutingIssues)
                    msg += $"Routing preferences will be fixed by creating correctly named fitting types\n" +
                           $"and updating each slot (Union fittings are skipped).\n";

                msg += "\nProceed with fixes?";

                var answer = MessageBox.Show(msg,
                    "Fix Cable Tray Types & Routing", MessageBoxButton.YesNo, MessageBoxImage.Question);

                if (answer != MessageBoxResult.Yes) return;

                CreateTypeResult result;
                using (var tx = new Transaction(_doc, "BOM - Fix Cable Tray Types & Routing"))
                {
                    tx.Start();
                    result = svc.CreateAndFixTypes();
                    tx.Commit();
                }

                ScrollableResultWindow.Show(result.Summary,
                    result.Errors.Count > 0 ? "Complete (with errors)" : "Complete",
                    result.Errors.Count > 0);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error checking types:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ═══════════════════════════════════════════════════════════
        // CHECK PARAMETERS
        // ═══════════════════════════════════════════════════════════

        private void BtnCheckParams_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Parameters grouped by which category they should be on
                var commonParams = new[] {
                    "V_Count", "V_Count_Connections", "V_Mounting_Type",
                    "V_Room_Name", "V_Room Name Manual",
                    "V_BOM_Description", "V_Part_Number", "V_SAP_Code",
                    "V_Order_Quantity", "V_Service Type",
                };

                var cableTrayParams = new[] {
                    "V_(137) Bolt DIN 7985 M6x20 ZN", "V_(119) Threaded plate M6 ZN",
                    "V_(100) Clamp for screw M6", "V_(99) Clamping element GKS34FT",
                    "V_(124) Clamping element GSV34FT", "V_(23) Washer DIN 9021 M6 ZN",
                    "V_(21) Bolt DIN 7985 M6x25 ZN", "V_(33) Nut DIN 934 M6 ZN",
                    "V_(66) Bolt DIN 933 M6x12 8.8 ZN", "V_(101) Rail nut M6 18X18",
                    "V_(4) Washer DIN 125 M6 ZN",
                    "V_(N/A) Fibre Runner Coupler",
                    "V_Containment_Straight_Connector",
                    "V_Containment_Truss-head Bolt with Nut 6x12",
                    "V_Containment_Truss-head bolt with flange nut 6x16",
                };

                var fixtureParams = new[] {
                    "V_(85) ISO 7380 MF M6x20 10.9 ZN",
                    "V_(132) SIDE HOLDER FOR CABLE GLAND 25mm",
                    "V_(133) LOCKNUT 25mm FOR FLEXIBLE CONDUIT GLAND",
                    "V_(134) MALE FIXED FITTING + RING 25mm",
                    "V_(139) RUBBER END GZ16",
                    "V_Flexible Conduit", "V_Steel Conduit",
                };

                // Get test elements from each category
                var ctElem = new FilteredElementCollector(_doc)
                    .OfClass(typeof(Autodesk.Revit.DB.Electrical.CableTray))
                    .WhereElementIsNotElementType().FirstOrDefault()
                    ?? new FilteredElementCollector(_doc)
                    .OfCategory(BuiltInCategory.OST_CableTrayFitting)
                    .WhereElementIsNotElementType().FirstOrDefault();

                var lfElem = new FilteredElementCollector(_doc)
                    .OfCategory(BuiltInCategory.OST_LightingFixtures)
                    .WhereElementIsNotElementType().FirstOrDefault()
                    ?? new FilteredElementCollector(_doc)
                    .OfCategory(BuiltInCategory.OST_ElectricalFixtures)
                    .WhereElementIsNotElementType().FirstOrDefault();

                var missing = new List<string>();
                var found = new List<string>();
                var results = new List<(string name, string status, string category)>();

                // GUID lookup for reliable parameter detection
                // (LookupParameter fails with special characters like parentheses)
                var paramGuids = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase)
                {
                    ["V_Count"] = new Guid("7883dc6a-dd5c-4c65-bbb3-7680c9802ac3"),
                    ["V_Count_Connections"] = new Guid("6fb1e3bf-cae5-4a91-8711-25edc7a98ad2"),
                    ["V_Mounting_Type"] = new Guid("2c34789a-487d-411f-840d-d203b91a2ba6"),
                    ["V_Room_Name"] = new Guid("3cc5d8d1-f441-4c65-ae3a-528a9092a7cf"),
                    ["V_Room Name Manual"] = new Guid("3b3d6ba9-6720-4007-a0ff-9d824010ea8f"),
                    ["V_BOM_Description"] = new Guid("69093450-edcd-4ef3-a642-dd5ad54f8e7a"),
                    ["V_Part_Number"] = new Guid("2eeca9ad-ed38-4a9e-ba54-11579469476b"),
                    ["V_SAP_Code"] = new Guid("e630049b-c64c-4afb-8bc4-039232ba43be"),
                    ["V_Order_Quantity"] = new Guid("2493ecab-5797-470d-ad5b-389c42781bbf"),
                    ["V_Service Type"] = new Guid("3f2bdad0-fcc1-4067-afe4-8539d461f868"),
                    ["V_(137) Bolt DIN 7985 M6x20 ZN"] = new Guid("5e6cb0e2-06ef-402f-9df8-865fa9f32d34"),
                    ["V_(119) Threaded plate M6 ZN"] = new Guid("f471f316-4f93-4f14-8136-b614268df6d9"),
                    ["V_(100) Clamp for screw M6"] = new Guid("f98a3c0f-c06e-4e77-9546-8998cda03d9d"),
                    ["V_(99) Clamping element GKS34FT"] = new Guid("0b834f68-b8b5-4aa5-85ce-537c761eafcc"),
                    ["V_(124) Clamping element GSV34FT"] = new Guid("f1a45694-d7f7-4114-b22f-c4432b5edcc7"),
                    ["V_(23) Washer DIN 9021 M6 ZN"] = new Guid("664f1764-aded-4744-96d9-0379e2ac4e2e"),
                    ["V_(21) Bolt DIN 7985 M6x25 ZN"] = new Guid("a7be6015-591d-4a0d-bb95-8f5d9a7edbbb"),
                    ["V_(33) Nut DIN 934 M6 ZN"] = new Guid("2b984768-c3ab-4778-8727-491648eb48cc"),
                    ["V_(66) Bolt DIN 933 M6x12 8.8 ZN"] = new Guid("cbd9a081-f898-4fb7-842e-933182f50000"),
                    ["V_(101) Rail nut M6 18X18"] = new Guid("44748e83-cbe5-408c-808a-2fde8d27890e"),
                    ["V_(4) Washer DIN 125 M6 ZN"] = new Guid("335df1c1-8f8d-4394-9e6f-cdcdd797de05"),
                    ["V_(N/A) Fibre Runner Coupler"] = new Guid("f0db8947-024a-4bc4-9a89-f048815f5bb7"),
                    ["V_Containment_Straight_Connector"] = new Guid("ff6d7f9b-f9e0-4cfa-b875-786580d2d321"),
                    ["V_Containment_Truss-head Bolt with Nut 6x12"] = new Guid("973dd2e2-899c-4a58-98c7-65da6fd5a9bc"),
                    ["V_Containment_Truss-head bolt with flange nut 6x16"] = new Guid("b38a7209-4613-4b5e-a147-9bbb78a59c45"),
                    ["V_JointPlate"] = new Guid("5988ad5a-ba99-4aa5-b127-eb553907fab3"),
                    ["V_(85) ISO 7380 MF M6x20 10.9 ZN"] = new Guid("acec007a-6bd0-4ffb-ab5a-46f118ff5544"),
                    ["V_(132) SIDE HOLDER FOR CABLE GLAND 25mm"] = new Guid("99706329-c9d2-45e1-a18d-25b0c65e1489"),
                    ["V_(133) LOCKNUT 25mm FOR FLEXIBLE CONDUIT GLAND"] = new Guid("84a4a96f-662a-491f-8d74-b0e187186dab"),
                    ["V_(134) MALE FIXED FITTING + RING 25mm"] = new Guid("2ef3a787-4fcc-4e2f-a0fc-70c3f66e00de"),
                    ["V_(139) RUBBER END GZ16"] = new Guid("70e08a16-767c-441e-a3a3-c168d9c79295"),
                    ["V_Flexible Conduit"] = new Guid("769c5543-5a29-475b-af0b-e900f6422c18"),
                    ["V_Steel Conduit"] = new Guid("e04ad3ad-dd8d-445a-87f9-7fe8bb0be9c3"),
                };

                // Helper: check param by GUID (reliable) with name fallback
                bool Check(Element? elem, string paramName)
                {
                    if (elem == null) return false;
                    // Primary: GUID lookup (works with special characters)
                    if (paramGuids.TryGetValue(paramName, out Guid guid))
                    {
                        if (elem.get_Parameter(guid) != null) return true;
                    }
                    // Fallback: LookupParameter by name
                    if (elem.LookupParameter(paramName) != null) return true;
                    return false;
                }

                // Check common params on cable trays (they should be on all categories)
                foreach (var p in commonParams)
                {
                    bool ok = Check(ctElem, p) || Check(lfElem, p);
                    results.Add((p, ok ? "OK" : "MISSING", "Common"));
                    if (ok) found.Add(p); else missing.Add(p);
                }

                // Check cable tray params on cable tray element
                foreach (var p in cableTrayParams)
                {
                    bool ok = Check(ctElem, p);
                    string cat = "Cable Tray";
                    if (!ok && ctElem == null) cat += " (no element found!)";
                    results.Add((p, ok ? "OK" : "MISSING", cat));
                    if (ok) found.Add(p); else missing.Add(p);
                }

                // Check fixture params on fixture element
                foreach (var p in fixtureParams)
                {
                    bool ok = Check(lfElem, p);
                    string cat = "Fixture";
                    if (!ok && lfElem == null) cat += " (no element found!)";
                    results.Add((p, ok ? "OK" : "MISSING", cat));
                    if (ok) found.Add(p); else missing.Add(p);
                }

                int total = commonParams.Length + cableTrayParams.Length + fixtureParams.Length;

                string msg = $"Parameter Check Results:\n" +
                    $"  Cable Tray test element: {(ctElem != null ? $"ID {ctElem.Id.Value}" : "NONE FOUND")}\n" +
                    $"  Fixture test element: {(lfElem != null ? $"ID {lfElem.Id.Value}" : "NONE FOUND")}\n\n" +
                    $"Found: {found.Count} / {total}\n" +
                    $"Missing: {missing.Count}\n\n";

                string lastCat = "";
                foreach (var (name, status, category) in results)
                {
                    if (category != lastCat)
                    {
                        msg += $"\n  --- {category} Parameters ---\n";
                        lastCat = category;
                    }
                    msg += $"  [{status}]  {name}\n";
                }

                if (missing.Count > 0)
                {
                    msg += "\nPlease add the missing shared parameters to the Cable Tray\n" +
                           "and Cable Tray Fitting categories using the shared\n" +
                           "parameter file (VERTIV-Shared_Parameters.txt).";
                }
                else
                {
                    msg += "\nAll required parameters are present. Ready to generate BOM.";
                }

                ScrollableResultWindow.Show(msg,
                    missing.Count > 0 ? "Missing Parameters" : "All Parameters OK",
                    missing.Count > 0);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error checking parameters:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ═══════════════════════════════════════════════════════════
        // HELPERS
        // ═══════════════════════════════════════════════════════════

        private void SaveSettingsFromUI()
        {
            if (double.TryParse(txtSliceLength.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out double sl))
                _settings.DefaultSliceLength = sl;
            if (double.TryParse(txtCouplingGap.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out double cg))
                _settings.CouplingGap = cg;
            if (double.TryParse(txtGSVPercentage.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out double gp))
                _settings.GSV34FTPercentage = gp;
            _settings.IncludeEarthingBridge = chkIncludeEarthing.IsChecked ?? false;
            _settings.RoundUpOrderQuantity = chkRoundUp.IsChecked ?? false;
            SettingsService.Save(_settings);
        }

        private static string BuildRoomDisplayStatic(string? roomNumber, string? roomName)
        {
            bool hasNum = !string.IsNullOrEmpty(roomNumber);
            bool hasName = !string.IsNullOrEmpty(roomName);
            if (hasNum && hasName) return $"{roomNumber} - {roomName}";
            if (hasNum) return roomNumber!;
            if (hasName) return roomName!;
            return "";
        }

        private void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            var win = new SettingsWindow(_settings);
            if (win.ShowDialog() == true)
            {
                _settings = win.Settings;
                SettingsService.Save(_settings);
            }
        }

        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            using var dlg = new System.Windows.Forms.FolderBrowserDialog();
            dlg.Description = "Select output folder for BOM Excel files";
            dlg.SelectedPath = _outputFolder;
            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                _outputFolder = dlg.SelectedPath;
                txtOutputPath.Text = _outputFolder;
            }
        }
    }
}
