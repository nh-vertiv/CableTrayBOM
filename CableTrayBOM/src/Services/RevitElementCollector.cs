using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Electrical;
using CableTrayBOM.Models;

namespace CableTrayBOM.Services
{
    /// <summary>
    /// Collects cable trays, fittings, and fixtures.
    /// Uses SupportDetectionEngine (rectangular prism) for support counting.
    /// Uses V1's proven room assignment via linked model IsPointInRoom with Z offsets.
    /// </summary>
    public class RevitElementCollector
    {
        private readonly Document _doc;
        private SupportDetectionEngine? _detectionEngine;
        private List<LinkedRoomInfo>? _linkedRoomCache;

        public RevitElementCollector(Document doc)
        {
            _doc = doc;
        }

        /// <summary>
        /// Quick room detection - returns all rooms from host and linked models
        /// without performing the full element scan. Used for room pre-selection.
        /// </summary>
        public List<(string id, string display)> GetAvailableRooms()
        {
            var rooms = new List<(string, string)>();
            var seen = new HashSet<string>();

            // Host model rooms
            foreach (var room in new FilteredElementCollector(_doc)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType()
                .ToElements())
            {
                string? name = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString();
                string? number = room.get_Parameter(BuiltInParameter.ROOM_NUMBER)?.AsString();
                string display = !string.IsNullOrEmpty(number) && !string.IsNullOrEmpty(name)
                    ? $"{number} - {name}"
                    : !string.IsNullOrEmpty(number) ? number : (name ?? "");
                if (!string.IsNullOrEmpty(display) && seen.Add(display))
                    rooms.Add((room.Id.ToString(), display));
            }

            // Linked model rooms
            foreach (var link in new FilteredElementCollector(_doc)
                .OfClass(typeof(RevitLinkInstance)).ToElements().OfType<RevitLinkInstance>())
            {
                var linkDoc = link.GetLinkDocument();
                if (linkDoc == null) continue;
                foreach (var room in new FilteredElementCollector(linkDoc)
                    .OfCategory(BuiltInCategory.OST_Rooms)
                    .WhereElementIsNotElementType()
                    .ToElements())
                {
                    string? name = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString();
                    string? number = room.get_Parameter(BuiltInParameter.ROOM_NUMBER)?.AsString();
                    string display = !string.IsNullOrEmpty(number) && !string.IsNullOrEmpty(name)
                        ? $"{number} - {name}"
                        : !string.IsNullOrEmpty(number) ? number : (name ?? "");
                    if (!string.IsNullOrEmpty(display) && seen.Add(display))
                        rooms.Add((room.Id.ToString(), display));
                }
            }

            rooms.Sort((a, b) => string.Compare(a.Item2, b.Item2, StringComparison.Ordinal));
            return rooms;
        }

        // ═══════════════════════════════════════════════════════════
        // CABLE TRAYS
        // ═══════════════════════════════════════════════════════════

        public List<CableTraySegment> CollectCableTrays(BOMSettings settings)
        {
            _detectionEngine = new SupportDetectionEngine(_doc);
            _detectionEngine.CollectSupports();
            EnsureLinkedRoomCache();

            var segments = new List<CableTraySegment>();

            foreach (var ct in new FilteredElementCollector(_doc)
                .OfClass(typeof(CableTray)).WhereElementIsNotElementType().Cast<CableTray>())
            {
                var typeElem = _doc.GetElement(ct.GetTypeId());
                string check = (typeElem?.Name ?? ct.Name ?? "").ToLowerInvariant();
                if (check.Contains("busbar") || check.Contains("bus bar") || check.Contains("busway"))
                    continue;
                var seg = ConvertCableTray(ct);
                if (seg != null) segments.Add(seg);
            }

            foreach (var fi in new FilteredElementCollector(_doc)
                .OfCategory(BuiltInCategory.OST_CableTrayFitting)
                .WhereElementIsNotElementType().Cast<FamilyInstance>())
            {
                string fitCheck = ((fi.Symbol?.Family?.Name ?? "") + (fi.Symbol?.Name ?? "")).ToLowerInvariant();
                if (fitCheck.Contains("busbar") || fitCheck.Contains("bus bar")) continue;
                var seg = ConvertFitting(fi);
                if (seg != null) segments.Add(seg);
            }

            // Detect supports via bounding box clash (no transaction needed)
            _detectionEngine.DetectAllSupports(segments);

            // Count connections via connectors
            CalculateConnections(segments);

            return segments;
        }

        private CableTraySegment? ConvertCableTray(CableTray ct)
        {
            try
            {
                var typeElem = _doc.GetElement(ct.GetTypeId());
                string typeName = typeElem?.Name ?? ct.Name ?? "";
                string familyName = typeElem is FamilySymbol fs ? (fs.Family?.Name ?? "") : "";

                double wMm = UnitUtils.ConvertFromInternalUnits(ct.Width, UnitTypeId.Millimeters);
                double hMm = UnitUtils.ConvertFromInternalUnits(ct.Height, UnitTypeId.Millimeters);
                var lp = ct.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH);
                double lenMm = lp != null && lp.HasValue
                    ? UnitUtils.ConvertFromInternalUnits(lp.AsDouble(), UnitTypeId.Millimeters) : 0;

                // Detect mesh channel: type name contains "_Channel" (case-insensitive)
                bool isMeshChannel = typeName.IndexOf("_Channel", StringComparison.OrdinalIgnoreCase) >= 0;

                var seg = new CableTraySegment
                {
                    ElementId = ct.Id.ToString(),
                    RevitUniqueId = ct.UniqueId,
                    TrayType = ClassifyTrayType(familyName, typeName),
                    Size = $"{wMm:F0}x{hMm:F0}",
                    Width = wMm, Height = hMm, OriginalLength = lenMm,
                    IsFitting = false,
                    IsMeshChannel = isMeshChannel,
                    Orientation = DetermineOrientation(ct),
                    Level = GetLevel(ct),
                    PartNumber = GetParam(ct, "Part Number") ?? GetParam(ct, "Mark") ?? "",
                    Manufacturer = GetParam(ct, "Manufacturer") ?? "OBO",
                    Description = typeName,
                    Mounting = DetermineMounting(ct),
                    ServiceType = GetParam(ct, "Service Type") ?? "",
                    IsInGroup = ct.GroupId != ElementId.InvalidElementId,
                    GroupName = ct.GroupId != ElementId.InvalidElementId
                        ? (_doc.GetElement(ct.GroupId)?.Name ?? "Unknown Group") : ""
                };
                AssignRoom(seg, ct);
                return seg;
            }
            catch { return null; }
        }

        private CableTraySegment? ConvertFitting(FamilyInstance fi)
        {
            try
            {
                string familyName = fi.Symbol?.Family?.Name ?? "";
                string typeName = fi.Symbol?.Name ?? fi.Name ?? "";
                double wMm = 0, hMm = 0;
                var cm = fi.MEPModel?.ConnectorManager;
                if (cm != null) foreach (Connector c in cm.Connectors)
                {
                    wMm = UnitUtils.ConvertFromInternalUnits(c.Width, UnitTypeId.Millimeters);
                    hMm = UnitUtils.ConvertFromInternalUnits(c.Height, UnitTypeId.Millimeters);
                    break;
                }
                double lenMm = 0;
                var bb = fi.get_BoundingBox(null);
                if (bb != null)
                {
                    var diag = bb.Max - bb.Min;
                    lenMm = UnitUtils.ConvertFromInternalUnits(
                        Math.Max(Math.Abs(diag.X), Math.Max(Math.Abs(diag.Y), Math.Abs(diag.Z))),
                        UnitTypeId.Millimeters);
                }

                // Detect mesh channel: type name contains "_Channel" (case-insensitive)
                bool isMeshChannel = typeName.IndexOf("_Channel", StringComparison.OrdinalIgnoreCase) >= 0;

                var seg = new CableTraySegment
                {
                    ElementId = fi.Id.ToString(), RevitUniqueId = fi.UniqueId,
                    TrayType = ClassifyTrayType(familyName, typeName),
                    Size = $"{wMm:F0}x{hMm:F0}", Width = wMm, Height = hMm,
                    OriginalLength = lenMm, IsFitting = true,
                    IsMeshChannel = isMeshChannel,
                    Fitting = ClassifyFitting(familyName, typeName),
                    Orientation = Orientation.Horizontal, Level = GetLevel(fi),
                    Description = $"{ClassifyFitting(familyName, typeName)} - {typeName}",
                    Mounting = DetermineMounting(fi),
                    ServiceType = GetParam(fi, "Service Type") ?? "",
                    IsInGroup = fi.GroupId != ElementId.InvalidElementId,
                    GroupName = fi.GroupId != ElementId.InvalidElementId
                        ? (_doc.GetElement(fi.GroupId)?.Name ?? "Unknown Group") : ""
                };
                AssignRoom(seg, fi);
                return seg;
            }
            catch { return null; }
        }

        // ═══════════════════════════════════════════════════════════
        // CONNECTIONS
        // ═══════════════════════════════════════════════════════════

        private void CalculateConnections(List<CableTraySegment> segments)
        {
            var segIds = new HashSet<string>(segments.Select(s => s.ElementId));

            foreach (var seg in segments)
            {
                var element = _doc.GetElement(RevitCompat.ToElementId(seg.ElementId));
                if (element == null) continue;

                ConnectorManager? connMgr = element is CableTray ct2 ? ct2.ConnectorManager
                    : (element as FamilyInstance)?.MEPModel?.ConnectorManager;
                if (connMgr == null) continue;

                // Count how many OTHER cable tray/fitting elements THIS element connects to
                var connectedTo = new HashSet<ElementId>();
                foreach (Connector conn in connMgr.Connectors)
                {
                    if (!conn.IsConnected) continue;
                    foreach (Connector other in conn.AllRefs)
                    {
                        if (other.Owner.Id == element.Id) continue;
                        // Only count connections to other cable trays/fittings in our list
                        if (!segIds.Contains(other.Owner.Id.ToString())) continue;
                        connectedTo.Add(other.Owner.Id);
                    }
                }

                seg.ConnectionCount = connectedTo.Count;
            }
        }

        // ═══════════════════════════════════════════════════════════
        // ROOM ASSIGNMENT (V1 proven logic — exact copy)
        // ═══════════════════════════════════════════════════════════

        private void EnsureLinkedRoomCache()
        {
            if (_linkedRoomCache != null) return;
            _linkedRoomCache = new List<LinkedRoomInfo>();

            var linkInstances = new FilteredElementCollector(_doc)
                .OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>().ToList();

            foreach (var linkInstance in linkInstances)
            {
                var linkDoc = linkInstance.GetLinkDocument();
                if (linkDoc == null) continue;
                var transform = linkInstance.GetTotalTransform();
                var rooms = new FilteredElementCollector(linkDoc)
                    .OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType()
                    .Cast<Room>().Where(r => r.Area > 0).ToList();

                foreach (var room in rooms)
                {
                    string levelName = "";
                    if (room.LevelId != ElementId.InvalidElementId)
                    {
                        var level = linkDoc.GetElement(room.LevelId) as Level;
                        levelName = level?.Name ?? "";
                    }
                    var bb = room.get_BoundingBox(null);
                    _linkedRoomCache.Add(new LinkedRoomInfo
                    {
                        RoomName = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? "",
                        RoomNumber = room.get_Parameter(BuiltInParameter.ROOM_NUMBER)?.AsString() ?? "",
                        LevelName = levelName, Room = room,
                        LinkTransform = transform, LinkDocument = linkDoc,
                        BbMin = bb?.Min, BbMax = bb?.Max
                    });
                }
            }

            // Host document rooms as fallback
            foreach (var room in new FilteredElementCollector(_doc)
                .OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType()
                .Cast<Room>().Where(r => r.Area > 0))
            {
                var bb = room.get_BoundingBox(null);
                _linkedRoomCache.Add(new LinkedRoomInfo
                {
                    RoomName = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? "",
                    RoomNumber = room.get_Parameter(BuiltInParameter.ROOM_NUMBER)?.AsString() ?? "",
                    LevelName = "", Room = room,
                    LinkTransform = Transform.Identity, LinkDocument = _doc,
                    BbMin = bb?.Min, BbMax = bb?.Max
                });
            }
        }

        private (string name, string number) GetRoomFromLinkedModels(XYZ point)
        {
            EnsureLinkedRoomCache();
            if (_linkedRoomCache == null || _linkedRoomCache.Count == 0) return ("", "");

            double[] zOffsets = { 0, -0.5, -1.0, -1.5, -2.0, -3.0, 0.5, 1.0 };

            foreach (double zOff in zOffsets)
            {
                foreach (var ri in _linkedRoomCache)
                {
                    if (ri.BbMin != null && ri.BbMax != null)
                    {
                        XYZ tp = ri.LinkDocument != _doc
                            ? ri.LinkTransform.Inverse.OfPoint(point) : point;
                        if (tp.X < ri.BbMin.X - 1 || tp.X > ri.BbMax.X + 1 ||
                            tp.Y < ri.BbMin.Y - 1 || tp.Y > ri.BbMax.Y + 1)
                            continue;
                    }

                    try
                    {
                        XYZ tp = ri.LinkDocument != _doc
                            ? ri.LinkTransform.Inverse.OfPoint(point) : point;
                        var op = zOff == 0 ? tp : new XYZ(tp.X, tp.Y, tp.Z + zOff);
                        if (ri.Room.IsPointInRoom(op)) return (ri.RoomName, ri.RoomNumber);
                    }
                    catch { }
                }
            }
            return ("", "");
        }

        private void AssignRoom(CableTraySegment segment, Element element)
        {
            try
            {
                XYZ? midPoint = null;
                if (element.Location is LocationCurve lc && lc.Curve != null)
                    midPoint = lc.Curve.Evaluate(0.5, true);
                else if (element.Location is LocationPoint lp)
                    midPoint = lp.Point;

                if (midPoint != null)
                {
                    var (rn, rnum) = GetRoomFromLinkedModels(midPoint);
                    if (!string.IsNullOrEmpty(rn) || !string.IsNullOrEmpty(rnum))
                    {
                        segment.RoomName = rn; segment.RoomNumber = rnum; return;
                    }
                }

                if (element.Location is LocationCurve lc2 && lc2.Curve != null)
                {
                    foreach (var pt in new[] { lc2.Curve.GetEndPoint(0), lc2.Curve.GetEndPoint(1) })
                    {
                        var (rn, rnum) = GetRoomFromLinkedModels(pt);
                        if (!string.IsNullOrEmpty(rn) || !string.IsNullOrEmpty(rnum))
                        {
                            segment.RoomName = rn; segment.RoomNumber = rnum; return;
                        }
                    }
                }

                segment.RoomName = GetParam(element, "Room Name") ?? GetParam(element, "Room") ?? "";
                segment.RoomNumber = GetParam(element, "Room Number") ?? "";
            }
            catch { }
        }

        private void AssignFixtureRoom(FixtureElement fixture, FamilyInstance fi)
        {
            try
            {
                XYZ? point = (fi.Location is LocationPoint lp) ? lp.Point : null;
                if (point != null)
                {
                    var (name, number) = GetRoomFromLinkedModels(point);
                    if (!string.IsNullOrEmpty(name))
                    {
                        fixture.RoomName = name; fixture.RoomNumber = number; return;
                    }
                }
                if (fi.Room != null)
                {
                    fixture.RoomName = fi.Room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? "";
                    fixture.RoomNumber = fi.Room.get_Parameter(BuiltInParameter.ROOM_NUMBER)?.AsString() ?? "";
                }
                else if (fi.Space != null)
                {
                    fixture.RoomName = fi.Space.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? "";
                    fixture.RoomNumber = fi.Space.get_Parameter(BuiltInParameter.ROOM_NUMBER)?.AsString() ?? "";
                }
            }
            catch { }
        }

        // ═══════════════════════════════════════════════════════════
        // FIXTURES
        // ═══════════════════════════════════════════════════════════

        public List<FixtureElement> CollectLightingFixtures()
        {
            EnsureLinkedRoomCache();
            var result = new List<FixtureElement>();
            foreach (var fi in new FilteredElementCollector(_doc)
                .OfCategory(BuiltInCategory.OST_LightingFixtures)
                .WhereElementIsNotElementType().Cast<FamilyInstance>())
            {
                var f = ConvertFixture(fi);
                if (f != null) { AssignFixtureRoom(f, fi); result.Add(f); }
            }
            return result;
        }

        public List<FixtureElement> CollectElectricalFixtures()
        {
            EnsureLinkedRoomCache();
            var result = new List<FixtureElement>();
            foreach (var fi in new FilteredElementCollector(_doc)
                .OfCategory(BuiltInCategory.OST_ElectricalFixtures).WhereElementIsNotElementType()
                .Cast<FamilyInstance>()
                .Concat(new FilteredElementCollector(_doc)
                    .OfCategory(BuiltInCategory.OST_ElectricalEquipment).WhereElementIsNotElementType()
                    .Cast<FamilyInstance>()))
            {
                var f = ConvertFixture(fi);
                if (f != null) { AssignFixtureRoom(f, fi); result.Add(f); }
            }
            return result;
        }

        private FixtureElement? ConvertFixture(FamilyInstance fi)
        {
            try
            {
                string fam = fi.Symbol?.Family?.Name ?? "";
                string typ = fi.Symbol?.Name ?? fi.Name ?? "";
                return new FixtureElement
                {
                    ElementId = fi.Id.ToString(), RevitUniqueId = fi.UniqueId,
                    Category = ClassifyFixture(fam, typ),
                    Mounting = DetermineFixtureMounting(fi),
                    PartNumber = GetParam(fi, "Part Number") ?? GetParam(fi, "Type Mark") ?? "",
                    SAPCode = GetParam(fi, "SAP Code") ?? "", Manufacturer = GetParam(fi, "Manufacturer") ?? "",
                    Equipment = typ, Description = GetParam(fi, "Description") ?? typ,
                    Dimensions = GetParam(fi, "Dimensions") ?? "",
                    Length = GetParamDbl(fi, "Length", 0), Quantity = 1, Unit = "pcs",
                    Level = GetLevel(fi),
                    ServiceType = GetParam(fi, "Service Type") ?? ""
                };
            }
            catch { return null; }
        }

        // ═══════════════════════════════════════════════════════════
        // CLASSIFICATION & HELPERS
        // ═══════════════════════════════════════════════════════════

        private static TrayCategory ClassifyTrayType(string fam, string typ)
        {
            string s = (fam + " " + typ).ToLowerInvariant();
            if (s.Contains("ladder")) return TrayCategory.CableLadder;
            if (s.Contains("fiber") || s.Contains("fibre")) return TrayCategory.FiberTray;
            if (s.Contains("mesh") || s.Contains("wire")) return TrayCategory.MeshCableTray;
            if (s.Contains("non-perforated") || s.Contains("solid")) return TrayCategory.NonPerforatedCableTray;
            if (s.Contains("perforated")) return TrayCategory.PerforatedCableTray;
            return TrayCategory.MeshCableTray;
        }

        private static FittingType ClassifyFitting(string fam, string typ)
        {
            string s = (fam + " " + typ).ToLowerInvariant();
            if (s.Contains("tee")) return FittingType.Tee;
            if (s.Contains("cross")) return FittingType.Cross;
            if (s.Contains("reducer") || s.Contains("transition")) return FittingType.Reducer;
            if (s.Contains("riser up")) return FittingType.RiserUp;
            if (s.Contains("riser down")) return FittingType.RiserDown;
            if (s.Contains("45")) return FittingType.Elbow45;
            if (s.Contains("elbow") || s.Contains("bend") || s.Contains("90")) return FittingType.Elbow90;
            return FittingType.Straight;
        }

        private static FixtureCategory ClassifyFixture(string fam, string typ)
        {
            string s = (fam + " " + typ).ToLowerInvariant();
            if (s.Contains("junction") || s.Contains("jb"))
            {
                if (s.Contains("emergency")) return FixtureCategory.JunctionBoxEmergency;
                if (s.Contains("fdp")) return FixtureCategory.JunctionBoxFDP;
                if (s.Contains("signal") && s.Contains("dc")) return FixtureCategory.JunctionBoxSignalDC;
                if (s.Contains("signal")) return FixtureCategory.JunctionBoxSignal;
                if (s.Contains("door")) return FixtureCategory.JunctionBoxDoorBox;
                return FixtureCategory.JunctionBoxAC;
            }
            if (s.Contains("socket") || s.Contains("outlet")) return FixtureCategory.Socket;
            if (s.Contains("spur") || s.Contains("fused")) return FixtureCategory.FusedSpur;
            if (s.Contains("sensor") || s.Contains("detector") || s.Contains("presence")) return FixtureCategory.PresenceDetector;
            if (s.Contains("panic") || s.Contains("exit")) return FixtureCategory.PanicLight;
            if (s.Contains("emergency") || s.Contains(" em")) return FixtureCategory.EmergencyLight;
            return FixtureCategory.LightingFixture;
        }

        private Orientation DetermineOrientation(Element el)
        {
            if (el is CableTray ct)
            {
                try
                {
                    var curve = (ct.Location as LocationCurve)?.Curve;
                    if (curve != null)
                    {
                        double vert = Math.Abs((curve.GetEndPoint(1) - curve.GetEndPoint(0)).Normalize().Z);
                        if (vert > 0.95) return Orientation.Vertical;
                        if (vert > 0.1) return Orientation.Sloped;
                    }
                }
                catch { }
            }
            return Orientation.Horizontal;
        }

        private MountingType DetermineMounting(Element el)
        {
            string? m = GetParam(el, "Mounting Type") ?? GetParam(el, "MountingType");
            if (!string.IsNullOrEmpty(m))
            {
                string ml = m.ToLowerInvariant();
                if (ml.Contains("console")) return MountingType.Console;
                if (ml.Contains("panel")) return MountingType.Panel;
                if (ml.Contains("steel")) return MountingType.Steel;
            }
            return MountingType.SupportChannel;
        }

        private MountingType DetermineFixtureMounting(FamilyInstance fi)
        {
            string? m = GetParam(fi, "Mounting Type") ?? GetParam(fi, "MountingType");
            if (!string.IsNullOrEmpty(m))
            {
                string ml = m.ToLowerInvariant();
                if (ml.Contains("panel")) return MountingType.Panel;
                if (ml.Contains("steel")) return MountingType.Steel;
                if (ml.Contains("channel") || ml.Contains("mesh")) return MountingType.SupportChannel;
                if (ml.Contains("console")) return MountingType.Console;
            }
            return MountingType.SupportChannel;
        }

        private string GetLevel(Element el)
        {
            try
            {
                if (el.LevelId != ElementId.InvalidElementId)
                    return (_doc.GetElement(el.LevelId) as Level)?.Name ?? "";
                var p = el.get_Parameter(BuiltInParameter.RBS_START_LEVEL_PARAM);
                if (p != null) return (_doc.GetElement(p.AsElementId()) as Level)?.Name ?? "";
            }
            catch { }
            return "";
        }

        private static string? GetParam(Element e, string name)
        {
            var p = e.LookupParameter(name);
            return p is { HasValue: true, StorageType: StorageType.String } ? p.AsString() : null;
        }

        private static double GetParamDbl(Element e, string name, double def)
        {
            var p = e.LookupParameter(name);
            return p is { HasValue: true, StorageType: StorageType.Double } ? p.AsDouble() : def;
        }

        public string GenerateDiagnosticLog(List<CableTraySegment> segments)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== CABLE TRAY BOM V2 — DIAGNOSTIC LOG ===");
            sb.AppendLine($"Generated: {DateTime.Now}");
            sb.AppendLine($"Detection: Tray-Driven Rectangular Prism (BB-based Z)");
            sb.AppendLine($"Supports found: {_detectionEngine?.SupportCount ?? 0}");
            sb.AppendLine($"Rooms cached: {_linkedRoomCache?.Count ?? 0}");
            sb.AppendLine($"Segments: {segments.Count}");
            sb.AppendLine();

            if (_detectionEngine != null)
            {
                sb.AppendLine("--- DETECTION ENGINE DIAGNOSTICS ---");
                sb.AppendLine(_detectionEngine.GetDiagnosticInfo(segments));
            }

            sb.AppendLine("--- ALL SEGMENTS ---");
            foreach (var s in segments.Take(200))
                sb.AppendLine($"  ID={s.ElementId} {s.TrayType} {s.Size} L={s.OriginalLength:F0}mm " +
                    $"Sup={s.SupportCount} Conn={s.ConnectionCount} Fit={s.IsFitting} " +
                    $"MeshCh={s.IsMeshChannel} " +
                    $"Room={s.RoomNumber}-{s.RoomName} Mount={s.Mounting}");

            sb.AppendLine("\n--- ROOM CACHE ---");
            if (_linkedRoomCache != null)
            {
                var byDoc = _linkedRoomCache.GroupBy(r => r.LinkDocument.Title);
                foreach (var grp in byDoc)
                {
                    sb.AppendLine($"  Document: \"{grp.Key}\" ({grp.Count()} rooms)");
                    foreach (var ri in grp.Take(10))
                        sb.AppendLine($"    Room \"{ri.RoomNumber} - {ri.RoomName}\"");
                    if (grp.Count() > 10) sb.AppendLine($"    ... and {grp.Count() - 10} more");
                }
            }

            sb.AppendLine("\n=== END ===");
            return sb.ToString();
        }
    }

    internal class LinkedRoomInfo
    {
        public string RoomName { get; set; } = "";
        public string RoomNumber { get; set; } = "";
        public string LevelName { get; set; } = "";
        public Room Room { get; set; } = null!;
        public Transform LinkTransform { get; set; } = Transform.Identity;
        public Document LinkDocument { get; set; } = null!;
        public XYZ? BbMin { get; set; }
        public XYZ? BbMax { get; set; }
    }
}
