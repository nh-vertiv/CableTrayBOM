using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClosedXML.Excel;
using CableTrayBOM.Models;
using CableTrayBOM.Services;

namespace CableTrayBOM.Services
{
    /// <summary>
    /// Exports BOM data to Excel files matching the schedule formats
    /// defined in the reference Excel files (ESI standard).
    /// 
    /// Creates two workbooks:
    /// 1. Cable Trays & Ladders BOM Schedule
    /// 2. Small Power & Lighting Fixtures BOM Schedule
    /// </summary>
    public class ExcelExportService
    {
        private readonly BOMSettings _settings;

        // Styling constants - Vertiv brand colors
        private const string HeaderBgColor = "333333";     // Vertiv Dark Charcoal
        private const string SubHeaderBgColor = "4E8CEF";  // Vertiv Blue
        private const string JointMatBgColor = "DB9C3F";   // Vertiv Gold
        private const string TotalRowBgColor = "E8E8E8";   // Light gray for totals
        private const string BorderColor = "333333";
        private const string Copyright = "Created by Infrastructure Solutions BIM Program Department";

        public ExcelExportService(BOMSettings settings)
        {
            _settings = settings;
        }

        // ═══════════════════════════════════════════════════════════════════
        // CABLE TRAYS & LADDERS BOM
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Create the Cable Trays & Ladders BOM workbook.
        /// Each tray type gets its own section (matching Schedule_for_BOM_of_cable_trays_and_ladders.xlsx).
        /// </summary>
        public void ExportCableTrayBOM(
            string filePath,
            List<CableTraySegment> segments,
            SlicingService slicingService,
            JointMaterialService jointMaterialService)
        {
            using var wb = new XLWorkbook();

            // ── Sheet 1: Full BOM by Type ──
            var ws = wb.AddWorksheet("BOM Cable Trays & Ladders");

            int row = 1;

            // Group segments by tray type, but order the sections by Service Type first,
            // then Tray Type (so the main BOM reads grouped by usage). Each tray type maps
            // to one service in this project's naming, so the group's representative
            // ServiceType is well-defined; Min keeps it deterministic if a type ever mixes.
            var typeGroups = segments
                .GroupBy(s => s.TrayType)
                .OrderBy(g => g.Min(s => s.ServiceType ?? ""), StringComparer.OrdinalIgnoreCase)
                .ThenBy(g => g.Key.ToString(), StringComparer.OrdinalIgnoreCase);

            foreach (var typeGroup in typeGroups)
            {
                row = WriteCableTrayTypeSection(ws, row, typeGroup.Key, typeGroup.ToList(),
                    slicingService, jointMaterialService);
                row += 2; // Gap between sections
            }

            // Auto-fit columns
            ws.Columns().AdjustToContents();

            // ── Sheet 2: Summary by Room ──
            var wsRoom = wb.AddWorksheet("BOM by Room");
            WriteRoomSummary(wsRoom, segments, slicingService, jointMaterialService);
            wsRoom.Columns().AdjustToContents();

            // ── Sheet 3: Order Summary ──
            var wsOrder = wb.AddWorksheet("Order Summary");
            WriteOrderSummary(wsOrder, segments, slicingService);
            wsOrder.Columns().AdjustToContents();

            // Add copyright to all sheets
            foreach (var sheet in wb.Worksheets)
            {
                int lastRow = sheet.LastRowUsed()?.RowNumber() ?? 1;
                lastRow += 2;
                sheet.Cell(lastRow, 1).Value = Copyright;
                sheet.Cell(lastRow, 1).Style.Font.Italic = true;
                sheet.Cell(lastRow, 1).Style.Font.FontSize = 9;
                sheet.Cell(lastRow, 1).Style.Font.FontColor = XLColor.FromHtml("#6D6E71");
            }

            wb.SaveAs(filePath);
        }

        private int WriteCableTrayTypeSection(
            IXLWorksheet ws, int startRow, TrayCategory trayType,
            List<CableTraySegment> segments,
            SlicingService slicingService,
            JointMaterialService jointMaterialService)
        {
            int row = startRow;

            // Get mounting type
            var mounting = segments.FirstOrDefault()?.Mounting ?? MountingType.SupportChannel;
            string mountingText = mounting == MountingType.Console ? "TO CONSOLE" : "TO SUPPORT CHANNEL";

            // Section Title
            string title = trayType switch
            {
                TrayCategory.MeshCableTray => $"BOM MESH CABLE TRAY {mountingText}",
                TrayCategory.CableLadder => $"BOM CABLE LADDER {mountingText}",
                TrayCategory.PerforatedCableTray => $"BOM PERFORATED CABLE TRAY {mountingText}",
                TrayCategory.NonPerforatedCableTray => $"BOM NON-PERFORATED CABLE TRAY {mountingText}",
                TrayCategory.FiberTray => $"BOM FIBER TRAY {mountingText}",
                _ => $"BOM CABLE TRAY {mountingText}"
            };

            // Title row
            ws.Cell(row, 1).Value = title;
            ws.Cell(row, 1).Style.Font.Bold = true;
            ws.Cell(row, 1).Style.Font.FontSize = 12;
            ws.Range(row, 1, row, 9).Merge().Style.Fill.BackgroundColor = XLColor.FromHtml("#" + HeaderBgColor);
            ws.Range(row, 1, row, 9).Style.Font.FontColor = XLColor.White;
            row++;

            // Joint Material header row
            ws.Cell(row, 10).Value = "Joint Material";
            ws.Cell(row, 10).Style.Font.Bold = true;
            ws.Cell(row, 10).Style.Fill.BackgroundColor = XLColor.FromHtml("#" + JointMatBgColor);
            ws.Cell(row, 10).Style.Font.FontColor = XLColor.White;
            row++;

            // Determine joint material columns based on tray type
            var jointMaterialHeaders = GetJointMaterialHeaders(trayType, mounting);

            // Column headers - includes Type and Size per requirement
            string[] baseHeaders = { "Number", "Part Number", "SAP Code", "Manufacturer",
                                     "Type", "Size", "Description", "Quantity [pcs]", "Length [m]" };

            int col = 1;
            foreach (var h in baseHeaders)
            {
                ws.Cell(row, col).Value = h;
                ws.Cell(row, col).Style.Font.Bold = true;
                ws.Cell(row, col).Style.Fill.BackgroundColor = XLColor.FromHtml("#" + SubHeaderBgColor);
                ws.Cell(row, col).Style.Font.FontColor = XLColor.White;
                ws.Cell(row, col).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                col++;
            }

            int jointStartCol = col;
            foreach (var jm in jointMaterialHeaders)
            {
                ws.Cell(row, col).Value = jm;
                ws.Cell(row, col).Style.Font.Bold = true;
                ws.Cell(row, col).Style.Fill.BackgroundColor = XLColor.FromHtml("#" + JointMatBgColor);
                ws.Cell(row, col).Style.Font.FontColor = XLColor.White;
                ws.Cell(row, col).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                ws.Cell(row, col).Style.Alignment.WrapText = true;
                col++;
            }

            // Comment column
            ws.Cell(row, col).Value = "Comment";
            ws.Cell(row, col).Style.Font.Bold = true;
            ws.Cell(row, col).Style.Fill.BackgroundColor = XLColor.FromHtml("#" + SubHeaderBgColor);
            ws.Cell(row, col).Style.Font.FontColor = XLColor.White;
            ws.Cell(row, col).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            int commentCol = col;
            row++;

            // Group by size/type for data rows - SORTED BY SERVICE TYPE then SIZE
            var sizeGroups = segments
                .Where(s => !s.IsFitting)
                .GroupBy(s => new { s.TrayType, s.Size, s.PartNumber, s.ServiceType })
                .OrderBy(g => g.Key.ServiceType)
                .ThenBy(g => g.Key.Size)
                .ThenBy(g => g.Key.TrayType.ToString());

            // Also include fittings as separate group
            var fittingGroups = segments
                .Where(s => s.IsFitting)
                .GroupBy(s => new { s.TrayType, s.Size, s.Fitting, s.ServiceType })
                .OrderBy(g => g.Key.ServiceType)
                .ThenBy(g => g.Key.Size);

            int itemNumber = 1;
            int totalQuantity = 0;
            double totalLength = 0;
            var totalJointMaterials = new Dictionary<string, int>();

            foreach (var sizeGroup in sizeGroups)
            {
                var representative = sizeGroup.First();
                int quantity = sizeGroup.Count();
                double lengthM = sizeGroup.Sum(s => s.OriginalLength) / 1000.0;

                foreach (var seg in sizeGroup)
                    slicingService.SliceSegment(seg);

                var groupJointMaterials = new Dictionary<string, int>();
                foreach (var seg in sizeGroup)
                {
                    var segJM = jointMaterialService.CalculateSegmentJointMaterials(seg);
                    foreach (var jm in segJM)
                    {
                        if (!groupJointMaterials.ContainsKey(jm.Key))
                            groupJointMaterials[jm.Key] = 0;
                        groupJointMaterials[jm.Key] += jm.Value;
                    }
                }

                // Calculate order pieces from aggregate total length
                double totalLengthMm = sizeGroup.Sum(s => s.OriginalLength);
                double sliceLen = _settings.DefaultSliceLength;
                int fullPcs = (int)Math.Floor(totalLengthMm / sliceLen);
                int orderPieces = fullPcs + (totalLengthMm - fullPcs * sliceLen > 0 ? 1 : 0);

                // Format type name for display
                string typeDisplay = representative.TrayType switch
                {
                    TrayCategory.MeshCableTray => "Mesh Cable Tray",
                    TrayCategory.CableLadder => "Cable Ladder",
                    TrayCategory.PerforatedCableTray => "Perforated Cable Tray",
                    TrayCategory.NonPerforatedCableTray => "Non-Perforated Cable Tray",
                    TrayCategory.FiberTray => "Fiber Tray",
                    _ => "Cable Tray"
                };

                col = 1;
                ws.Cell(row, col++).Value = itemNumber++;
                ws.Cell(row, col++).Value = representative.PartNumber;
                ws.Cell(row, col++).Value = ""; // SAP Code
                ws.Cell(row, col++).Value = representative.Manufacturer;
                ws.Cell(row, col++).Value = typeDisplay;                                    // Type column
                ws.Cell(row, col++).Value = representative.Size;                            // Size column (WxH)
                ws.Cell(row, col++).Value = $"{typeDisplay} {representative.Size}";         // Description = [Type] [Size]
                ws.Cell(row, col++).Value = orderPieces;
                ws.Cell(row, col++).Value = Math.Round(lengthM, 2);

                // Joint material quantities
                foreach (var jmHeader in jointMaterialHeaders)
                {
                    int jmQty = 0;
                    // Match header to calculated materials
                    foreach (var calc in groupJointMaterials)
                    {
                        if (jmHeader.Contains(calc.Key) || calc.Key.Contains(
                            jmHeader.Replace("(", "").Replace(")", "").Split(' ')[0]))
                        {
                            jmQty += calc.Value;
                        }
                    }
                    // Also try exact match
                    if (groupJointMaterials.ContainsKey(jmHeader))
                        jmQty = groupJointMaterials[jmHeader];

                    ws.Cell(row, col).Value = jmQty > 0 ? jmQty : 0;
                    ws.Cell(row, col).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                    col++;

                    // Accumulate totals
                    if (!totalJointMaterials.ContainsKey(jmHeader))
                        totalJointMaterials[jmHeader] = 0;
                    totalJointMaterials[jmHeader] += jmQty;
                }

                // Apply border to data cells
                for (int c = 1; c <= commentCol; c++)
                    ws.Cell(row, c).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;

                totalQuantity += orderPieces;
                totalLength += lengthM;
                row++;
            }

            // Formula/note row (how joint materials are counted)
            col = 10;
            var formulaNotes = GetJointMaterialFormulas(trayType, mounting);
            foreach (var note in formulaNotes)
            {
                ws.Cell(row, col).Value = note;
                ws.Cell(row, col).Style.Font.Italic = true;
                ws.Cell(row, col).Style.Font.FontSize = 9;
                ws.Cell(row, col).Style.Font.FontColor = XLColor.Gray;
                col++;
            }

            // Earthing bridge note
            ws.Cell(row, commentCol).Value = "Earthing bridge at each connection";
            ws.Cell(row, commentCol).Style.Font.Italic = true;
            ws.Cell(row, commentCol).Style.Font.FontSize = 9;
            row++;

            // Totals row
            ws.Cell(row, 1).Value = "TOTAL";
            ws.Cell(row, 1).Style.Font.Bold = true;
            ws.Cell(row, 8).Value = totalQuantity;
            ws.Cell(row, 9).Value = Math.Round(totalLength, 2);
            for (int c = 1; c <= commentCol; c++)
            {
                ws.Cell(row, c).Style.Fill.BackgroundColor = XLColor.FromHtml("#" + TotalRowBgColor);
                ws.Cell(row, c).Style.Font.Bold = true;
            }

            col = 10;
            foreach (var jmHeader in jointMaterialHeaders)
            {
                ws.Cell(row, col).Value = totalJointMaterials.TryGetValue(jmHeader, out var jmTotal) ? jmTotal : 0;
                col++;
            }
            row++;

            return row;
        }

        // ═══════════════════════════════════════════════════════════════════
        // SMALL POWER & LIGHTING FIXTURES BOM
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Create the Small Power & Lighting Fixtures BOM workbook.
        /// Matches format of Schedule_for_Small_power___ligthing_fixtures.xlsx.
        /// </summary>
        public void ExportFixtureBOM(
            string filePath,
            List<FixtureElement> lightingFixtures,
            List<FixtureElement> electricalFixtures,
            JointMaterialService jointMaterialService)
        {
            using var wb = new XLWorkbook();

            // ── Sheet 1: Equipment List ──
            var ws1 = wb.AddWorksheet("Equipment List");
            int row = 1;

            // Lighting Fixtures section
            row = WriteFixtureSection(ws1, row, "BOM LIGHTING FIXTURE WITH FLEXIBLE CONDUIT",
                lightingFixtures.Where(f =>
                    f.Category == FixtureCategory.LightingFixture ||
                    f.Category == FixtureCategory.EmergencyLight ||
                    f.Category == FixtureCategory.PanicLight).ToList(),
                jointMaterialService);
            row += 2;

            // Sockets & Sensors section
            row = WriteFixtureSection(ws1, row, "BOM SOCKET, SPUR & SENSOR SCHEDULE",
                electricalFixtures.Where(f =>
                    f.Category == FixtureCategory.Socket ||
                    f.Category == FixtureCategory.FusedSpur ||
                    f.Category == FixtureCategory.PresenceDetector).ToList(),
                jointMaterialService);
            row += 2;

            // Junction Boxes sections
            var junctionBoxes = electricalFixtures.Where(f =>
                f.Category.ToString().StartsWith("JunctionBox")).ToList();

            var jbGroups = junctionBoxes.GroupBy(jb => jb.Category);
            foreach (var jbGroup in jbGroups)
            {
                string sectionTitle = jbGroup.Key switch
                {
                    FixtureCategory.JunctionBoxAC => "BOM AC JUNCTION BOXES WIRING SCHEDULE",
                    FixtureCategory.JunctionBoxEmergency => "BOM AC JUNCTION BOXES FOR EMERGENCY LIGHTING WIRING SCHEDULE",
                    FixtureCategory.JunctionBoxFDP => "BOM AC JUNCTION BOXES FOR FDP WIRING SCHEDULE",
                    FixtureCategory.JunctionBoxSignal => "BOM SIGNAL JUNCTION BOXES FOR FDP WIRING SCHEDULE",
                    FixtureCategory.JunctionBoxSignalDC => "BOM SIGNAL AND DC JUNCTION BOXES FOR FDP WIRING SCHEDULE",
                    FixtureCategory.JunctionBoxDoorBox => "BOM AC JUNCTION BOXES FOR DOOR BOXES WIRING SCHEDULE",
                    _ => "BOM JUNCTION BOXES"
                };

                // Sub-group by mounting type
                var mountGroups = jbGroup.GroupBy(jb => jb.Mounting);
                foreach (var mountGroup in mountGroups)
                {
                    string mountSuffix = mountGroup.Key switch
                    {
                        MountingType.SupportChannel => " - MESH TRAY MOUNTING",
                        MountingType.Panel => " - PANEL MOUNTING",
                        MountingType.Steel => " - STEEL MOUNTING",
                        _ => ""
                    };

                    row = WriteFixtureSection(ws1, row, sectionTitle + mountSuffix,
                        mountGroup.ToList(), jointMaterialService);
                    row += 2;
                }
            }

            ws1.Columns().AdjustToContents();

            // ── Sheet 2: Schedule by Mounting Type ──
            var ws2 = wb.AddWorksheet("Schedule for Small power");
            row = 1;
            var allFixtures = lightingFixtures.Concat(electricalFixtures).ToList();
            var mountingGroups = allFixtures.GroupBy(f => new { f.Category, f.Mounting });

            foreach (var mg in mountingGroups.OrderBy(g => g.Key.Category).ThenBy(g => g.Key.Mounting))
            {
                string categoryName = mg.Key.Category.ToString();
                string mountName = mg.Key.Mounting.ToString();
                row = WriteDetailedFixtureSection(ws2, row,
                    $"BOM {categoryName} - {mountName} MOUNTING",
                    mg.ToList(), jointMaterialService);
                row += 2;
            }

            ws2.Columns().AdjustToContents();

            // ── Sheet 3: Room Summary ──
            var ws3 = wb.AddWorksheet("BOM by Room");
            WriteFixtureRoomSummary(ws3, allFixtures, jointMaterialService);
            ws3.Columns().AdjustToContents();

            // Add copyright to all sheets
            foreach (var sheet in wb.Worksheets)
            {
                int lastRow = sheet.LastRowUsed()?.RowNumber() ?? 1;
                lastRow += 2;
                sheet.Cell(lastRow, 1).Value = Copyright;
                sheet.Cell(lastRow, 1).Style.Font.Italic = true;
                sheet.Cell(lastRow, 1).Style.Font.FontSize = 9;
                sheet.Cell(lastRow, 1).Style.Font.FontColor = XLColor.FromHtml("#6D6E71");
            }

            wb.SaveAs(filePath);
        }

        private int WriteFixtureSection(
            IXLWorksheet ws, int startRow, string title,
            List<FixtureElement> fixtures,
            JointMaterialService jointMaterialService)
        {
            int row = startRow;

            // Title
            ws.Cell(row, 1).Value = title;
            ws.Cell(row, 1).Style.Font.Bold = true;
            ws.Cell(row, 1).Style.Font.FontSize = 11;
            ws.Range(row, 1, row, 10).Merge().Style.Fill.BackgroundColor = XLColor.FromHtml("#" + HeaderBgColor);
            ws.Range(row, 1, row, 10).Style.Font.FontColor = XLColor.White;
            row++;

            // Joint Material label
            ws.Cell(row, 11).Value = "Joint Material";
            ws.Cell(row, 11).Style.Font.Bold = true;
            ws.Cell(row, 11).Style.Fill.BackgroundColor = XLColor.FromHtml("#" + JointMatBgColor);
            ws.Cell(row, 11).Style.Font.FontColor = XLColor.White;
            row++;

            // Headers
            string[] baseHeaders = { "Number", "Part Number", "SAP Code", "Manufacturer",
                                     "Equipment", "Description", "Dimensions [mm]",
                                     "Length [m]", "Quantity", "Unit" };

            int col = 1;
            foreach (var h in baseHeaders)
            {
                ws.Cell(row, col).Value = h;
                ws.Cell(row, col).Style.Font.Bold = true;
                ws.Cell(row, col).Style.Fill.BackgroundColor = XLColor.FromHtml("#" + SubHeaderBgColor);
                ws.Cell(row, col).Style.Font.FontColor = XLColor.White;
                ws.Cell(row, col).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                col++;
            }

            // Determine joint material columns from first fixture
            var jmHeaders = new List<string>();
            if (fixtures.Count > 0)
            {
                var sampleJM = jointMaterialService.CalculateFixtureJointMaterials(fixtures.First());
                jmHeaders = sampleJM.Keys.ToList();
            }

            int jmStartCol = col;
            foreach (var jm in jmHeaders)
            {
                ws.Cell(row, col).Value = jm;
                ws.Cell(row, col).Style.Font.Bold = true;
                ws.Cell(row, col).Style.Fill.BackgroundColor = XLColor.FromHtml("#" + JointMatBgColor);
                ws.Cell(row, col).Style.Font.FontColor = XLColor.White;
                ws.Cell(row, col).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                ws.Cell(row, col).Style.Alignment.WrapText = true;
                col++;
            }

            // Comment column
            ws.Cell(row, col).Value = "Comment";
            ws.Cell(row, col).Style.Font.Bold = true;
            int commentCol = col;
            row++;

            // Data rows - group by equipment type
            var groups = fixtures
                .GroupBy(f => new { f.PartNumber, f.Equipment, f.Mounting })
                .OrderBy(g => g.Key.Equipment);

            int itemNum = 1;
            foreach (var group in groups)
            {
                var rep = group.First();
                int totalQty = group.Sum(f => f.Quantity);

                col = 1;
                ws.Cell(row, col++).Value = itemNum++;
                ws.Cell(row, col++).Value = rep.PartNumber;
                ws.Cell(row, col++).Value = rep.SAPCode;
                ws.Cell(row, col++).Value = rep.Manufacturer;
                ws.Cell(row, col++).Value = rep.Equipment;
                ws.Cell(row, col++).Value = rep.Description;
                ws.Cell(row, col++).Value = rep.Dimensions;
                if (rep.Length > 0)
                    ws.Cell(row, col).Value = rep.Length;
                else
                    ws.Cell(row, col).Value = "N/A";
                col++;
                ws.Cell(row, col++).Value = totalQty;
                ws.Cell(row, col++).Value = rep.Unit;

                // Calculate joint materials for total quantity
                var tempFixture = new FixtureElement
                {
                    Category = rep.Category,
                    Mounting = rep.Mounting,
                    Quantity = totalQty
                };
                var jmQtys = jointMaterialService.CalculateFixtureJointMaterials(tempFixture);

                foreach (var jmHeader in jmHeaders)
                {
                    int qty = jmQtys.TryGetValue(jmHeader, out var jmVal) ? jmVal : 0;
                    if (qty > 0)
                        ws.Cell(row, col).Value = qty;
                    else
                        ws.Cell(row, col).Value = "N/A";
                    ws.Cell(row, col).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                    col++;
                }

                // Apply borders
                for (int c = 1; c <= commentCol; c++)
                    ws.Cell(row, c).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;

                row++;
            }

            return row;
        }

        private int WriteDetailedFixtureSection(
            IXLWorksheet ws, int startRow, string title,
            List<FixtureElement> fixtures,
            JointMaterialService jointMaterialService)
        {
            // Simplified version for the "Schedule" sheet - includes formula notes
            return WriteFixtureSection(ws, startRow, title, fixtures, jointMaterialService);
        }

        // ═══════════════════════════════════════════════════════════════════
        // ROOM SUMMARIES
        // ═══════════════════════════════════════════════════════════════════

        private void WriteRoomSummary(
            IXLWorksheet ws,
            List<CableTraySegment> segments,
            SlicingService slicingService,
            JointMaterialService jointMaterialService)
        {
            int row = 1;
            ws.Cell(row, 1).Value = "CABLE TRAY & LADDER BOM - SUMMARY BY ROOM";
            ws.Cell(row, 1).Style.Font.Bold = true;
            ws.Cell(row, 1).Style.Font.FontSize = 14;
            row += 2;

            var roomGroups = segments
                .GroupBy(s => string.IsNullOrEmpty(s.RoomNumber)
                    ? (string.IsNullOrEmpty(s.RoomName) ? "Unassigned" : s.RoomName)
                    : $"{s.RoomNumber} - {s.RoomName}")
                .OrderBy(g => g.Key);

            foreach (var roomGroup in roomGroups)
            {
                ws.Cell(row, 1).Value = $"Room: {roomGroup.Key}";
                ws.Cell(row, 1).Style.Font.Bold = true;
                ws.Cell(row, 1).Style.Font.FontSize = 12;
                ws.Range(row, 1, row, 8).Style.Fill.BackgroundColor = XLColor.FromHtml("#" + HeaderBgColor);
                ws.Range(row, 1, row, 8).Style.Font.FontColor = XLColor.White;
                row++;

                // Headers
                ws.Cell(row, 1).Value = "Type";
                ws.Cell(row, 2).Value = "Size";
                ws.Cell(row, 3).Value = "Part Number";
                ws.Cell(row, 4).Value = "Qty (segments)";
                ws.Cell(row, 5).Value = "Total Length [m]";
                ws.Cell(row, 6).Value = "Pieces to Order";
                ws.Cell(row, 7).Value = "Connections";
                ws.Cell(row, 8).Value = "Supports";
                for (int c = 1; c <= 8; c++)
                {
                    ws.Cell(row, c).Style.Font.Bold = true;
                    ws.Cell(row, c).Style.Fill.BackgroundColor = XLColor.FromHtml("#" + SubHeaderBgColor);
                    ws.Cell(row, c).Style.Font.FontColor = XLColor.White;
                }
                row++;

                var sizeGroups = roomGroup
                    .GroupBy(s => new { s.TrayType, s.Size, s.PartNumber, s.ServiceType })
                    .OrderBy(g => g.Key.ServiceType)
                    .ThenBy(g => g.Key.TrayType);

                foreach (var sg in sizeGroups)
                {
                    var segs = sg.ToList();
                    foreach (var s in segs)
                        slicingService.SliceSegment(s);

                    ws.Cell(row, 1).Value = sg.Key.TrayType.ToString();
                    ws.Cell(row, 2).Value = sg.Key.Size;
                    ws.Cell(row, 3).Value = sg.Key.PartNumber;
                    ws.Cell(row, 4).Value = segs.Count;
                    double roomTotalLen = segs.Sum(s => s.OriginalLength);
                    ws.Cell(row, 5).Value = Math.Round(roomTotalLen / 1000.0, 2);
                    int roomFullPcs = (int)Math.Floor(roomTotalLen / _settings.DefaultSliceLength);
                    int roomOrderPcs = roomFullPcs + (roomTotalLen - roomFullPcs * _settings.DefaultSliceLength > 0 ? 1 : 0);
                    ws.Cell(row, 6).Value = roomOrderPcs;
                    ws.Cell(row, 7).Value = segs.Sum(s => s.ConnectionCount);
                    ws.Cell(row, 8).Value = segs.Sum(s => s.SupportCount);
                    row++;
                }

                // Joint materials for room
                var roomJM = jointMaterialService.CalculateTotal(roomGroup);
                if (roomJM.Count > 0)
                {
                    row++;
                    ws.Cell(row, 1).Value = "Joint Materials:";
                    ws.Cell(row, 1).Style.Font.Bold = true;
                    ws.Cell(row, 1).Style.Font.Italic = true;
                    row++;
                    foreach (var jm in roomJM.OrderBy(j => j.Key))
                    {
                        ws.Cell(row, 2).Value = jm.Key;
                        ws.Cell(row, 3).Value = jm.Value;
                        row++;
                    }
                }

                row += 2;
            }
        }

        private void WriteFixtureRoomSummary(
            IXLWorksheet ws,
            List<FixtureElement> fixtures,
            JointMaterialService jointMaterialService)
        {
            int row = 1;
            ws.Cell(row, 1).Value = "FIXTURES BOM - SUMMARY BY ROOM";
            ws.Cell(row, 1).Style.Font.Bold = true;
            ws.Cell(row, 1).Style.Font.FontSize = 14;
            row += 2;

            var roomGroups = fixtures
                .GroupBy(f => string.IsNullOrEmpty(f.RoomNumber)
                    ? (string.IsNullOrEmpty(f.RoomName) ? "Unassigned" : f.RoomName)
                    : $"{f.RoomNumber} - {f.RoomName}")
                .OrderBy(g => g.Key);

            foreach (var roomGroup in roomGroups)
            {
                ws.Cell(row, 1).Value = $"Room: {roomGroup.Key}";
                ws.Cell(row, 1).Style.Font.Bold = true;
                ws.Cell(row, 1).Style.Font.FontSize = 12;
                ws.Range(row, 1, row, 6).Style.Fill.BackgroundColor = XLColor.FromHtml("#" + HeaderBgColor);
                ws.Range(row, 1, row, 6).Style.Font.FontColor = XLColor.White;
                row++;

                ws.Cell(row, 1).Value = "Category";
                ws.Cell(row, 2).Value = "Equipment";
                ws.Cell(row, 3).Value = "Mounting";
                ws.Cell(row, 4).Value = "Quantity";
                ws.Cell(row, 5).Value = "Part Number";
                ws.Cell(row, 6).Value = "Manufacturer";
                for (int c = 1; c <= 6; c++)
                {
                    ws.Cell(row, c).Style.Font.Bold = true;
                    ws.Cell(row, c).Style.Fill.BackgroundColor = XLColor.FromHtml("#" + SubHeaderBgColor);
                    ws.Cell(row, c).Style.Font.FontColor = XLColor.White;
                }
                row++;

                var catGroups = roomGroup
                    .GroupBy(f => new { f.Category, f.Equipment, f.Mounting, f.PartNumber })
                    .OrderBy(g => g.Key.Category);

                foreach (var cg in catGroups)
                {
                    ws.Cell(row, 1).Value = cg.Key.Category.ToString();
                    ws.Cell(row, 2).Value = cg.Key.Equipment;
                    ws.Cell(row, 3).Value = cg.Key.Mounting.ToString();
                    ws.Cell(row, 4).Value = cg.Sum(f => f.Quantity);
                    ws.Cell(row, 5).Value = cg.Key.PartNumber;
                    ws.Cell(row, 6).Value = cg.First().Manufacturer;
                    row++;
                }

                // Joint materials
                var roomJM = jointMaterialService.CalculateFixturesByRoom(roomGroup);
                if (roomJM.Count > 0)
                {
                    var firstRoom = roomJM.Values.FirstOrDefault();
                    if (firstRoom != null && firstRoom.Count > 0)
                    {
                        row++;
                        ws.Cell(row, 1).Value = "Joint Materials:";
                        ws.Cell(row, 1).Style.Font.Bold = true;
                        ws.Cell(row, 1).Style.Font.Italic = true;
                        row++;
                        foreach (var jm in firstRoom.OrderBy(j => j.Key))
                        {
                            ws.Cell(row, 2).Value = jm.Key;
                            ws.Cell(row, 3).Value = jm.Value;
                            row++;
                        }
                    }
                }

                row += 2;
            }
        }

        private void WriteOrderSummary(
            IXLWorksheet ws,
            List<CableTraySegment> segments,
            SlicingService slicingService)
        {
            int row = 1;
            ws.Cell(row, 1).Value = "ORDER SUMMARY - CABLE TRAY PIECES";
            ws.Cell(row, 1).Style.Font.Bold = true;
            ws.Cell(row, 1).Style.Font.FontSize = 14;
            row += 2;

            var orderSummary = slicingService.CalculateOrderQuantities(segments);

            // Column headers (written once).
            ws.Cell(row, 1).Value = "Service Type";
            ws.Cell(row, 2).Value = "Type";
            ws.Cell(row, 3).Value = "Size";
            ws.Cell(row, 4).Value = "Part Number";
            ws.Cell(row, 5).Value = "Manufacturer";
            ws.Cell(row, 6).Value = $"Standard Length [{_settings.DefaultSliceLength / 1000}m]";
            ws.Cell(row, 7).Value = "Total Length [m]";
            ws.Cell(row, 8).Value = "Full Pieces";
            ws.Cell(row, 9).Value = "Partial Pieces";
            ws.Cell(row, 10).Value = "TOTAL PIECES TO ORDER";
            ws.Cell(row, 11).Value = "Connections";
            ws.Cell(row, 12).Value = "Supports";
            for (int c = 1; c <= 12; c++)
            {
                ws.Cell(row, c).Style.Font.Bold = true;
                ws.Cell(row, c).Style.Fill.BackgroundColor = XLColor.FromHtml("#" + SubHeaderBgColor);
                ws.Cell(row, c).Style.Font.FontColor = XLColor.White;
                ws.Cell(row, c).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            }
            row++;

            // Sectioned by Service Type: each section lists its items then a TOTAL row.
            // The section totals use Excel's SUBTOTAL(9, …) so they recompute correctly
            // when rows are filtered. There is intentionally NO overall grand total.
            var serviceGroups = orderSummary.Values
                .GroupBy(o => o.ServiceType ?? "")
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

            foreach (var serviceGroup in serviceGroups)
            {
                int sectionFirstDataRow = row;

                foreach (var item in serviceGroup.OrderBy(o => o.TrayType.ToString(), StringComparer.OrdinalIgnoreCase)
                                                 .ThenBy(o => o.Size, StringComparer.OrdinalIgnoreCase))
                {
                    ws.Cell(row, 1).Value = item.ServiceType;
                    ws.Cell(row, 2).Value = item.TrayType.ToString();
                    ws.Cell(row, 3).Value = item.Size;
                    ws.Cell(row, 4).Value = item.PartNumber;
                    ws.Cell(row, 5).Value = item.Manufacturer;
                    ws.Cell(row, 6).Value = item.StandardLengthMm / 1000.0;
                    ws.Cell(row, 7).Value = Math.Round(item.TotalLengthMeters, 2);
                    ws.Cell(row, 8).Value = item.FullPieces;
                    ws.Cell(row, 9).Value = item.PartialPieces;
                    ws.Cell(row, 10).Value = item.TotalOrderPieces;
                    ws.Cell(row, 11).Value = item.TotalConnections;
                    ws.Cell(row, 12).Value = item.TotalSupports;

                    ws.Cell(row, 10).Style.Font.Bold = true;
                    ws.Cell(row, 10).Style.Fill.BackgroundColor = XLColor.FromHtml("#" + TotalRowBgColor);

                    for (int c = 1; c <= 12; c++)
                        ws.Cell(row, c).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;

                    row++;
                }

                int sectionLastDataRow = row - 1;

                // Per-service subtotal row (SUBTOTAL ignores rows hidden by a filter).
                ws.Cell(row, 1).Value = $"{serviceGroup.Key} TOTAL";
                ws.Cell(row, 1).Style.Font.Bold = true;
                foreach (int col in new[] { 7, 8, 9, 10, 11, 12 })
                {
                    string colLetter = ws.Cell(sectionFirstDataRow, col).Address.ColumnLetter;
                    ws.Cell(row, col).FormulaA1 =
                        $"SUBTOTAL(9,{colLetter}{sectionFirstDataRow}:{colLetter}{sectionLastDataRow})";
                }
                ws.Cell(row, 7).Style.NumberFormat.Format = "0.00";
                for (int c = 1; c <= 12; c++)
                {
                    ws.Cell(row, c).Style.Fill.BackgroundColor = XLColor.FromHtml("#" + TotalRowBgColor);
                    ws.Cell(row, c).Style.Font.Bold = true;
                    ws.Cell(row, c).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                }
                row++;

                // Blank spacer row between sections.
                row++;
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        // HELPERS
        // ═══════════════════════════════════════════════════════════════════

        private List<string> GetJointMaterialHeaders(TrayCategory type, MountingType mounting)
        {
            return (type, mounting) switch
            {
                (TrayCategory.MeshCableTray, MountingType.SupportChannel) => new List<string>
                {
                    "(137) Bolt DIN 7985 M6x20 ZN",
                    "(119) Threaded plate M6 ZN",
                    "(100) Clamp for screw M6",
                    "(99) Clamping element GKS34FT",
                    "(124) Clamping element GSV34FT"
                },
                (TrayCategory.MeshCableTray, MountingType.Console) => new List<string>
                {
                    "(21) Bolt DIN 7985 M6x25 ZN",
                    "(100) Clamp for screw M6",
                    "(33) Nut DIN 934 M6 ZN",
                    "(99) Clamping element GKS34FT",
                    "(124) Clamping element GSV34FT"
                },
                (TrayCategory.CableLadder, _) => new List<string>
                {
                    "(137) Bolt DIN 7985 M6x20 ZN",
                    "(23) Washer DIN 9021 M6 ZN",
                    "(119) Threaded plate M6 ZN",
                    "(N/A) Straight connector",
                    "(N/A) Truss-head bolt with nut 6x12"
                },
                (TrayCategory.PerforatedCableTray, _) => new List<string>
                {
                    "(137) Bolt DIN 7985 M6x20 ZN",
                    "(23) Washer DIN 9021 M6 ZN",
                    "(119) Threaded plate M6 ZN",
                    "(N/A) Straight connector",
                    "(N/A) Joint plate",
                    "(N/A) Truss-head bolt with nut 6x12"
                },
                (TrayCategory.NonPerforatedCableTray, _) => new List<string>
                {
                    "(137) Bolt DIN 7985 M6x20 ZN",
                    "(23) Washer DIN 9021 M6 ZN",
                    "(119) Threaded plate M6 ZN",
                    "Truss-head bolt with flange nut 6x16"
                },
                (TrayCategory.FiberTray, _) => new List<string>
                {
                    "(66) Bolt DIN 933 M6x12 8.8 ZN",
                    "(101) Rail nut M6 18X18",
                    "(4) Washer DIN 125 M6 ZN",
                    "(N/A) Fibre Runner Coupler"
                },
                _ => new List<string>
                {
                    "(137) Bolt DIN 7985 M6x20 ZN",
                    "(119) Threaded plate M6 ZN"
                }
            };
        }

        private List<string> GetJointMaterialFormulas(TrayCategory type, MountingType mounting)
        {
            return (type, mounting) switch
            {
                (TrayCategory.MeshCableTray, MountingType.SupportChannel) => new List<string>
                {
                    "2 per support", "2 per support", "2 per support",
                    "3 per connection", "10-15% of GKS34FT"
                },
                (TrayCategory.MeshCableTray, MountingType.Console) => new List<string>
                {
                    "2 per support", "2 per support", "2 per support",
                    "3 per connection", "10-15% of GKS34FT"
                },
                (TrayCategory.CableLadder, _) => new List<string>
                {
                    "2 per support", "2 per support", "2 per support",
                    "2 per connection", "4 per connector (8 per connection)"
                },
                (TrayCategory.PerforatedCableTray, _) => new List<string>
                {
                    "2 per support", "2 per support", "2 per support",
                    "2 per connection", "1 per connection",
                    "4 per connector (8 per connection) + 8 per joint plate"
                },
                (TrayCategory.NonPerforatedCableTray, _) => new List<string>
                {
                    "2 per support", "2 per support", "2 per support",
                    "2 per connection"
                },
                (TrayCategory.FiberTray, _) => new List<string>
                {
                    "2 per support", "2 per support", "2 per support",
                    "1 per connection"
                },
                _ => new List<string>()
            };
        }
    }
}
