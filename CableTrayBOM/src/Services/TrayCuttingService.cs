using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using CableTrayBOM.Models;

namespace CableTrayBOM.Services
{
    /// <summary>
    /// Splits cable tray elements into standard-length pieces.
    ///
    /// For elements inside groups:
    ///   1. Temporarily ungroups the group
    ///   2. Cuts the cable trays normally
    ///   3. Re-groups the original members + new pieces
    ///
    /// Pieces are connected directly at endpoints (no union fittings).
    /// Fittings (elbows, tees) are never cut.
    ///
    /// MESH CHANNEL (_Channel suffix, IsMeshChannel = true):
    ///   These are factory-bent pieces; they must NOT be physically cut in the
    ///   model at direction changes. QuickSlice skips ALL IsMeshChannel elements
    ///   (both straight CableTray and CableTrayFitting instances). Their piece
    ///   counts are handled purely in the BOM by SlicingService.
    /// </summary>
    public class TrayCuttingService
    {
        // Diagnostic: records what the most recent parameter-inheritance pass saw and did.
        // Surfaced into CuttingResult so the cut summary can show whether Comments /
        // Service Type / Mark actually transferred. Cleared at the start of each cut run.
        internal static readonly List<string> LastCopyLog = new();

        private readonly Document _doc;
        private readonly double _segLenFt;
        private readonly double _gapFt;

        public TrayCuttingService(Document doc, double segmentLengthMm, double couplingGapMm)
        {
            _doc = doc;
            _segLenFt = UnitUtils.ConvertToInternalUnits(segmentLengthMm, UnitTypeId.Millimeters);
            _gapFt = UnitUtils.ConvertToInternalUnits(couplingGapMm, UnitTypeId.Millimeters);
        }

        public CuttingResult CutAllTrays(List<CableTraySegment> segments)
        {
            var result = new CuttingResult();
            LastCopyLog.Clear();
            var processed = new HashSet<ElementId>();

            // ── Phase 1: Identify groups containing trays to cut ──
            var groupedTrays = new Dictionary<ElementId, List<CableTraySegment>>(); // GroupId -> segments
            var ungroupedTrays = new List<CableTraySegment>();

            foreach (var seg in segments)
            {
                // Skip non-straight fittings (ladder bends, tees etc.)
                if (seg.IsFitting && !seg.IsMeshChannel) { result.SkippedFittings++; continue; }

                // Skip ALL mesh channel elements — they are factory-bent and must not be
                // physically cut in the model. BOM piece counts are handled by SlicingService.
                if (seg.IsMeshChannel) { result.SkippedMeshChannel++; continue; }

                var elemId = RevitCompat.ToElementId(seg.ElementId);
                if (processed.Contains(elemId)) continue;

                var elem = _doc.GetElement(elemId);
                if (elem is not CableTray ct) continue;

                var locCurve = ct.Location as LocationCurve;
                if (locCurve?.Curve is not Line line) { processed.Add(elemId); continue; }

                if (line.Length <= _segLenFt + _gapFt)
                {
                    result.AlreadyShortEnough++;
                    processed.Add(elemId);
                    continue;
                }

                if (ct.GroupId != ElementId.InvalidElementId)
                {
                    if (!groupedTrays.ContainsKey(ct.GroupId))
                        groupedTrays[ct.GroupId] = new List<CableTraySegment>();
                    groupedTrays[ct.GroupId].Add(seg);
                }
                else
                {
                    ungroupedTrays.Add(seg);
                }
            }

            // ── Phase 2: Handle grouped elements ──
            foreach (var kvp in groupedTrays)
            {
                var groupId = kvp.Key;
                var groupSegs = kvp.Value;

                var group = _doc.GetElement(groupId) as Group;

                if (group == null)
                {
                    // Group edit mode or already dissolved — cut directly
                    foreach (var seg in groupSegs)
                    {
                        var eid = RevitCompat.ToElementId(seg.ElementId);
                        if (processed.Contains(eid)) continue;
                        var el = _doc.GetElement(eid) as CableTray;
                        if (el == null) continue;
                        var lc = el.Location as LocationCurve;
                        if (lc?.Curve is not Line ln) continue;
                        if (ln.Length <= _segLenFt + _gapFt) { result.AlreadyShortEnough++; processed.Add(eid); continue; }
                        try { result.TotalCuts += SplitTray(el, ln, processed, null); result.TraysProcessed++; processed.Add(eid); }
                        catch (Exception ex) { result.Errors.Add($"ID {seg.ElementId}: {ex.Message}"); }
                    }
                    continue;
                }

                // Try ungroup → cut → regroup
                string groupName = group.Name;
                try
                {
                    var memberIds = group.GetMemberIds().ToList();
                    var ungroupedIds = group.UngroupMembers();
                    _doc.Regenerate();

                    result.UngroupedGroups.Add($"'{groupName}' ({memberIds.Count} members)");
                    var regroupElements = new HashSet<ElementId>(ungroupedIds);

                    foreach (var seg in groupSegs)
                    {
                        var eid = RevitCompat.ToElementId(seg.ElementId);
                        if (processed.Contains(eid)) continue;
                        var el = _doc.GetElement(eid) as CableTray;
                        if (el == null) continue;
                        var lc = el.Location as LocationCurve;
                        if (lc?.Curve is not Line ln) continue;
                        if (ln.Length <= _segLenFt + _gapFt) { result.AlreadyShortEnough++; processed.Add(eid); continue; }
                        try
                        {
                            var newIds = new HashSet<ElementId>();
                            result.TotalCuts += SplitTray(el, ln, processed, newIds);
                            result.TraysProcessed++; processed.Add(eid);
                            foreach (var nid in newIds) regroupElements.Add(nid);
                        }
                        catch (Exception ex) { result.Errors.Add($"ID {seg.ElementId} (group '{groupName}'): {ex.Message}"); }
                    }

                    // Re-group
                    try
                    {
                        var validIds = regroupElements
                            .Where(id => { var el = _doc.GetElement(id); return el != null && el.GroupId == ElementId.InvalidElementId; })
                            .ToList();
                        if (validIds.Count > 0)
                        {
                            var newGroup = _doc.Create.NewGroup(validIds);
                            if (newGroup != null)
                            {
                                try { newGroup.GroupType.Name = groupName + " - Cut"; }
                                catch { try { newGroup.GroupType.Name = groupName + " - Cut " + DateTime.Now.ToString("HHmmss"); } catch { } }
                                result.RegroupedGroups.Add($"'{newGroup.GroupType.Name}' ({validIds.Count} members)");
                            }
                        }
                    }
                    catch (Exception ex) { result.Errors.Add($"Re-group '{groupName}': {ex.Message}"); }
                }
                catch
                {
                    // Ungroup failed (group edit mode?) — cut directly
                    foreach (var seg in groupSegs)
                    {
                        var eid = RevitCompat.ToElementId(seg.ElementId);
                        if (processed.Contains(eid)) continue;
                        var el = _doc.GetElement(eid) as CableTray;
                        if (el == null) continue;
                        var lc = el.Location as LocationCurve;
                        if (lc?.Curve is not Line ln) continue;
                        try { result.TotalCuts += SplitTray(el, ln, processed, null); result.TraysProcessed++; processed.Add(eid); }
                        catch (Exception ex) { result.Errors.Add($"ID {seg.ElementId}: {ex.Message}"); }
                    }
                }
            }

            // ── Phase 3: Handle ungrouped elements normally ──
            foreach (var seg in ungroupedTrays)
            {
                var elemId = RevitCompat.ToElementId(seg.ElementId);
                if (processed.Contains(elemId)) continue;

                var elem = _doc.GetElement(elemId);
                if (elem is not CableTray ct) continue;

                var locCurve = ct.Location as LocationCurve;
                if (locCurve?.Curve is not Line line) continue;

                try
                {
                    int cuts = SplitTray(ct, line, processed, null);
                    result.TotalCuts += cuts;
                    result.TraysProcessed++;
                    processed.Add(elemId);
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"ID {seg.ElementId}: {ex.Message}");
                }
            }

            // Attach parameter-inheritance diagnostics (first ~12 lines) so the cut
            // summary shows whether Comments / Service Type / Mark transferred.
            if (LastCopyLog.Count > 0)
                result.ParamCopyLog = LastCopyLog.Take(12).ToList();

            return result;
        }

        private int SplitTray(CableTray original, Line origLine,
            HashSet<ElementId> processed, HashSet<ElementId>? newPieceIds)
        {
            XYZ start = origLine.GetEndPoint(0);
            XYZ end = origLine.GetEndPoint(1);
            XYZ dir = (end - start).Normalize();
            double totalLen = origLine.Length;

            // Save and disconnect downstream connection (end of tray)
            Connector? downstream = null;
            var cm = original.ConnectorManager;
            if (cm != null)
            {
                foreach (Connector c in cm.Connectors)
                {
                    if (c.Origin.DistanceTo(end) < c.Origin.DistanceTo(start) && c.IsConnected)
                    {
                        foreach (Connector o in c.AllRefs)
                        {
                            if (o.Owner.Id != original.Id)
                            {
                                downstream = o;
                                try { c.DisconnectFrom(o); } catch { }
                                break;
                            }
                        }
                        break;
                    }
                }
            }

            // Build piece geometry: remainder at START (near bends/fittings),
            // full-length pieces toward END.
            // Layout:  [remainder][segLen][segLen]...[segLen]   (pieces ABUT end-to-end)
            //
            // Pieces are placed directly end-to-end with NO physical gap between solids.
            // A physical gap would force Revit's coupling/union geometry to shove the
            // neighbouring tray sideways onto a parallel axis (the lateral-offset bug).
            // The coupling gap remains a logical allowance for ordering only and is
            // applied in the BOM length math (SlicingService), not in the model.
            // Every full piece is EXACTLY segLen; any leftover lives only in the remainder.
            var pieces = new List<(XYZ s, XYZ e)>();

            double minPieceFt = UnitUtils.ConvertToInternalUnits(100, UnitTypeId.Millimeters); // 100mm minimum

            // Largest N full pieces of segLen that fit within the total run.
            int numFull = (int)Math.Floor((totalLen + 1e-9) / _segLenFt);
            if (numFull < 1) return 0;

            // Leftover after the full pieces — this is the remainder piece's true length.
            double remainderLen = totalLen - numFull * _segLenFt;

            // If the remainder is a tiny sliver (below the minimum usable piece),
            // drop one full piece so the remainder grows to a usable length. Keeps
            // every full piece exactly segLen while avoiding an unusable offcut.
            bool hasRemainder = remainderLen > 0.005;
            if (hasRemainder && remainderLen < minPieceFt && numFull > 1)
            {
                numFull--;
                remainderLen = totalLen - numFull * _segLenFt;
            }
            hasRemainder = remainderLen > 0.005;

            double cursor = 0;

            // Remainder piece at the start (nearest the fittings), if usable.
            if (hasRemainder)
            {
                pieces.Add((start, start + dir * remainderLen));
                cursor = remainderLen; // next piece begins exactly where this one ends
            }

            // Full-length pieces — each EXACTLY _segLenFt, abutting end-to-end.
            for (int i = 0; i < numFull; i++)
            {
                XYZ pStart = start + dir * cursor;
                XYZ pEnd = start + dir * (cursor + _segLenFt);
                pieces.Add((pStart, pEnd));
                cursor += _segLenFt;
            }

            if (pieces.Count < 2) return 0;

            // Snapshot properties from original BEFORE shortening
            ElementId typeId = original.GetTypeId();
            ElementId levelId = original.LevelId;
            double width = original.Width;
            double height = original.Height;

            // Shorten original to first piece
            ((LocationCurve)original.Location).Curve = Line.CreateBound(pieces[0].s, pieces[0].e);

            // Create subsequent pieces using CableTray.Create with full property copy
            // (CopyElement carries stale group metadata and causes misalignment in group context)
            var allTrays = new List<CableTray> { original };
            for (int i = 1; i < pieces.Count; i++)
            {
                var (pStart, pEnd) = pieces[i];
                if (pStart.DistanceTo(pEnd) < 0.005) continue;

                try
                {
                    var newTray = CableTray.Create(_doc, typeId, pStart, pEnd, levelId);
                    if (newTray != null)
                    {
                        // Copy dimensions only (XYZ points already encode correct position)
                        try { newTray.get_Parameter(BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM)?.Set(width); } catch { }
                        try { newTray.get_Parameter(BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM)?.Set(height); } catch { }

                        processed.Add(newTray.Id);
                        newPieceIds?.Add(newTray.Id);
                        allTrays.Add(newTray);
                    }
                }
                catch { }
            }

            // Regenerate so connector positions are updated after LocationCurve changes
            _doc.Regenerate();

            // Inherit user-authored parameters (Comments, Service Type, Mark, SAP code,
            // part number, etc.) from the original onto every new piece. Done AFTER
            // Regenerate: parameters on a freshly CableTray.Create'd element are not
            // reliably writable until the document has regenerated, so writing earlier
            // silently no-ops (which is why nothing transferred before). Auto-computed
            // BOM parameters and geometry/position parameters are skipped (see
            // CopyInstanceParameters).
            for (int i = 1; i < allTrays.Count; i++)
                CopyInstanceParameters(original, allTrays[i]);
            _doc.Regenerate();

            // Connect pieces directly end-to-end (NO union fittings).
            // Union/coupling fittings have a physical body that Revit seats by pushing
            // the connected tray sideways, which threw the pieces onto parallel axes.
            // Since the pieces already abut exactly on the original centerline, a direct
            // connector-to-connector link keeps them collinear.
            for (int i = 0; i < allTrays.Count - 1; i++)
            {
                try
                {
                    var endConn = GetEndConnector(allTrays[i], dir);
                    var startConn = GetStartConnector(allTrays[i + 1], dir);

                    if (endConn != null && startConn != null)
                    {
                        DisconnectAll(endConn);
                        DisconnectAll(startConn);
                        endConn.ConnectTo(startConn);
                    }
                }
                catch
                {
                    // Leave the pieces unconnected if linking fails — they are still
                    // geometrically correct and collinear; connectivity is recomputed
                    // on the next scan.
                }
            }

            // Reconnect last piece to downstream element
            if (downstream != null && allTrays.Count > 1)
            {
                try
                {
                    var lastEnd = GetEndConnector(allTrays.Last(), dir);
                    if (lastEnd != null)
                        lastEnd.ConnectTo(downstream);
                }
                catch { }
            }

            return allTrays.Count - 1;
        }

        /// <summary>Disconnect all connections on a connector.</summary>
        private static void DisconnectAll(Connector conn)
        {
            if (!conn.IsConnected) return;
            try
            {
                var refs = new List<Connector>();
                foreach (Connector c in conn.AllRefs)
                    if (c.Owner.Id != conn.Owner.Id) refs.Add(c);
                foreach (var r in refs)
                    try { conn.DisconnectFrom(r); } catch { }
            }
            catch { }
        }

        /// <summary>Get the connector at the END of a tray (in the direction of travel).</summary>
        private static Connector? GetEndConnector(CableTray tray, XYZ direction)
        {
            var cm = tray.ConnectorManager;
            if (cm == null) return null;
            Connector? best = null; double maxProj = double.MinValue;
            foreach (Connector c in cm.Connectors)
            {
                double proj = c.Origin.DotProduct(direction);
                if (proj > maxProj) { maxProj = proj; best = c; }
            }
            return best;
        }

        /// <summary>Get the connector at the START of a tray (opposite direction of travel).</summary>
        private static Connector? GetStartConnector(CableTray tray, XYZ direction)
        {
            var cm = tray.ConnectorManager;
            if (cm == null) return null;
            Connector? best = null; double minProj = double.MaxValue;
            foreach (Connector c in cm.Connectors)
            {
                double proj = c.Origin.DotProduct(direction);
                if (proj < minProj) { minProj = proj; best = c; }
            }
            return best;
        }

        // ── Parameter copy ──

        private Dictionary<string, (StorageType type, object? val)> SnapshotParameters(CableTray ct)
        {
            var snap = new Dictionary<string, (StorageType, object?)>();
            BuiltInParameter[] bips = {
                BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM,
                BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM,
                BuiltInParameter.RBS_OFFSET_PARAM,
                BuiltInParameter.RBS_CTC_BOTTOM_ELEVATION,
                BuiltInParameter.RBS_CTC_TOP_ELEVATION,
            };
            foreach (var bip in bips)
            {
                var p = ct.get_Parameter(bip);
                if (p is { HasValue: true, IsReadOnly: false })
                    snap[$"B{(int)bip}"] = (p.StorageType, Read(p));
            }
            foreach (Parameter p in ct.Parameters)
            {
                if (p.IsReadOnly || !p.HasValue || p.Definition == null) continue;
                if (p.Definition.Name == "Length") continue;
                string k = $"D{p.Definition.Name}";
                if (!snap.ContainsKey(k)) snap[k] = (p.StorageType, Read(p));
            }
            return snap;
        }

        private void ApplyParameterSnapshot(CableTray t, Dictionary<string, (StorageType type, object? val)> snap)
        {
            foreach (var kvp in snap.Where(k => k.Key.StartsWith("B")))
            {
                int i = int.Parse(kvp.Key.Substring(1));
                var p = t.get_Parameter((BuiltInParameter)i);
                if (p is { IsReadOnly: false }) Write(p, kvp.Value.type, kvp.Value.val);
            }
            foreach (var kvp in snap.Where(k => k.Key.StartsWith("D")))
            {
                var p = t.LookupParameter(kvp.Key.Substring(1));
                if (p is { IsReadOnly: false }) Write(p, kvp.Value.type, kvp.Value.val);
            }
        }

        // Parameters the scan recomputes per piece — never inherited from the original,
        // otherwise every cut piece would carry the original's aggregate values.
        // Built once from SharedParameterMap so the names exactly match what the scan
        // writes: every joint-material parameter is "V_" + its map key, plus the
        // explicit metadata/quantity parameters below.
        private static readonly HashSet<string> _noInheritParams = BuildNoInheritSet();

        private static HashSet<string> BuildNoInheritSet()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                // Computed counts / order quantity / derived descriptive fields.
                "V_Count", "V_Count_Connections", "V_Order_Quantity",
                "V_BOM_Description", "V_Room_Name",
                "V_L1_Count", "V_L2_Count", "V_L3_Count",
            };
            // All joint-material quantity parameters (written per piece by the scan).
            // Their Revit display names are the map key prefixed with "V_".
            foreach (var key in Models.SharedParameterMap.NameToGuid.Keys)
                set.Add(key.StartsWith("V_") ? key : "V_" + key);
            return set;
        }

        /// <summary>
        /// Copies user-authored writable instance parameters from <paramref name="source"/>
        /// to <paramref name="target"/> so cut pieces inherit data like Comments, Service
        /// Type, SAP code, and part number. Skips:
        ///   - read-only / empty / unnamed parameters,
        ///   - geometry, position and dimension parameters (set from the cut geometry),
        ///   - auto-computed BOM parameters in <see cref="_noInheritParams"/>.
        /// Matching target parameters by name keeps shared/project/built-in params aligned.
        /// </summary>
        private void CopyInstanceParameters(Element source, Element target)
        {
            // Identity built-in parameters are copied directly by BuiltInParameter so the
            // match is unambiguous regardless of Revit's UI language or name collisions.
            // (LookupParameter matches on localized display name, which can miss or pick
            // the wrong parameter — this is why Comments was not transferring.)
            foreach (var bip in _identityBips)
            {
                try
                {
                    var sp = source.get_Parameter(bip);
                    var tp = target.get_Parameter(bip);
                    string srcVal = sp == null ? "<null param>" : (sp.HasValue ? (sp.AsString() ?? sp.AsValueString() ?? "<non-string>") : "<no value>");
                    if (sp == null || tp == null) { LastCopyLog.Add($"{bip}: src={srcVal} tgt={(tp==null?"<null>":"ok")} -> skip"); continue; }
                    if (!sp.HasValue || tp.IsReadOnly) { LastCopyLog.Add($"{bip}: src={srcVal} tgtReadOnly={tp.IsReadOnly} hasVal={sp.HasValue} -> skip"); continue; }
                    if (tp.StorageType != sp.StorageType) { LastCopyLog.Add($"{bip}: storage mismatch {sp.StorageType}->{tp.StorageType} -> skip"); continue; }
                    Write(tp, sp.StorageType, Read(sp));
                    string after = tp.AsString() ?? tp.AsValueString() ?? "<non-string>";
                    LastCopyLog.Add($"{bip}: src='{srcVal}' wrote -> after='{after}'");
                }
                catch (Exception ex) { LastCopyLog.Add($"{bip}: EXCEPTION {ex.Message}"); }
            }

            // Identity parameters copied by name (Service Type and its shared variants).
            foreach (var pname in _identityParamNames)
            {
                try
                {
                    var sp = source.LookupParameter(pname);
                    var tp = target.LookupParameter(pname);
                    if (sp == null || tp == null) continue;
                    if (!sp.HasValue || tp.IsReadOnly) { LastCopyLog.Add($"{pname}: tgtReadOnly={tp.IsReadOnly} hasVal={sp.HasValue} -> skip"); continue; }
                    if (tp.StorageType != sp.StorageType) continue;
                    string srcVal = sp.AsString() ?? sp.AsValueString() ?? "<non-string>";
                    Write(tp, sp.StorageType, Read(sp));
                    string after = tp.AsString() ?? tp.AsValueString() ?? "<non-string>";
                    LastCopyLog.Add($"{pname}: src='{srcVal}' wrote -> after='{after}'");
                }
                catch (Exception ex) { LastCopyLog.Add($"{pname}: EXCEPTION {ex.Message}"); }
            }

            foreach (Parameter sp in source.Parameters)
            {
                try
                {
                    if (sp.IsReadOnly || !sp.HasValue || sp.Definition == null) continue;

                    string name = sp.Definition.Name;
                    if (string.IsNullOrEmpty(name)) continue;
                    if (_noInheritParams.Contains(name)) continue;

                    // Skip geometry/position/dimension parameters — these come from the
                    // cut and must not be overwritten with the original's values.
                    if (IsGeometryParam(sp)) continue;

                    // If this parameter is a built-in we already handled above, match the
                    // target by the SAME built-in rather than by name (avoids a name-based
                    // mismatch landing on the wrong target parameter).
                    Parameter? tp = null;
                    if (sp.Definition is InternalDefinition sidef &&
                        sidef.BuiltInParameter != BuiltInParameter.INVALID)
                    {
                        tp = target.get_Parameter(sidef.BuiltInParameter);
                    }
                    if (tp == null) tp = target.LookupParameter(name);
                    if (tp == null || tp.IsReadOnly) continue;
                    if (tp.StorageType != sp.StorageType) continue;

                    Write(tp, sp.StorageType, Read(sp));
                }
                catch { /* skip any parameter that resists copying */ }
            }
        }

        // Identity instance parameters every cut piece should inherit. Comments and Mark
        // are copied by their well-known BuiltInParameter for a language-independent match.
        // Service Type is copied by name (its built-in enum name varies / is unconfirmed
        // across API versions, and the shared "V_Service Type" is handled by the name loop).
        private static readonly BuiltInParameter[] _identityBips =
        {
            BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS, // "Comments"
            BuiltInParameter.ALL_MODEL_MARK,              // "Mark"
        };

        // Display names of identity parameters to also copy by name (covers built-ins
        // whose enum we don't hardcode, and localized equivalents the user may have).
        private static readonly string[] _identityParamNames =
        {
            "Service Type", "V_Service Type", "V_Service_Type",
        };

        // Proven built-in geometry parameters (confirmed to exist in 2024 & 2025 APIs).
        private static readonly HashSet<BuiltInParameter> _geometryBips = new()
        {
            BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM,
            BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM,
            BuiltInParameter.CURVE_ELEM_LENGTH,
        };

        // Geometry/position parameter display-name fragments (case-insensitive). Catches
        // offset, justification, elevation, length, slope, etc. without hardcoding their
        // built-in enum names (several of which vary across API versions).
        private static readonly string[] _geometryNameHints =
        {
            "Length", "Offset", "Elevation", "Justification",
            "Slope", "Start Offset", "End Offset", "Middle Elevation",
            "Bottom Elevation", "Top Elevation",
        };

        private static bool IsGeometryParam(Parameter p)
        {
            if (p.Definition is InternalDefinition idef &&
                idef.BuiltInParameter != BuiltInParameter.INVALID &&
                _geometryBips.Contains(idef.BuiltInParameter))
                return true;

            string? n = p.Definition?.Name;
            if (string.IsNullOrEmpty(n)) return false;
            foreach (var hint in _geometryNameHints)
                if (n.IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            return false;
        }

        private static object? Read(Parameter p) => p.StorageType switch
        {
            StorageType.Double => p.AsDouble(), StorageType.Integer => p.AsInteger(),
            StorageType.String => p.AsString(), StorageType.ElementId => p.AsElementId(), _ => null
        };
        private static void Write(Parameter p, StorageType t, object? v)
        {
            if (v == null) return;
            try { switch (t) {
                case StorageType.Double: p.Set((double)v); break;
                case StorageType.Integer: p.Set((int)v); break;
                case StorageType.String: p.Set((string)v); break;
                case StorageType.ElementId: p.Set((ElementId)v); break;
            }} catch { }
        }
    }

    public class CuttingResult
    {
        public int TraysProcessed { get; set; }
        public int TotalCuts { get; set; }
        public int SkippedFittings { get; set; }
        public int AlreadyShortEnough { get; set; }

        /// <summary>
        /// Number of elements skipped because they are mesh channel (_Channel suffix).
        /// These are never physically cut — piece counts are handled in the BOM only.
        /// </summary>
        public int SkippedMeshChannel { get; set; }

        public List<string> UngroupedGroups { get; set; } = new();
        public List<string> RegroupedGroups { get; set; } = new();
        public List<string> ParamCopyLog { get; set; } = new();
        public List<string> Errors { get; set; } = new();

        public string Summary
        {
            get
            {
                var parts = new List<string>
                {
                    $"Split {TraysProcessed} trays into {TotalCuts + TraysProcessed} pieces.",
                    $"Skipped {SkippedFittings} fittings, {AlreadyShortEnough} already short.",
                };
                if (SkippedMeshChannel > 0)
                    parts.Add($"Skipped {SkippedMeshChannel} mesh channel (_Channel) elements — BOM-only, not physically cut.");
                if (UngroupedGroups.Count > 0)
                    parts.Add($"Temporarily ungrouped {UngroupedGroups.Count} group(s): {string.Join(", ", UngroupedGroups)}");
                if (RegroupedGroups.Count > 0)
                    parts.Add($"Re-grouped {RegroupedGroups.Count} group(s): {string.Join(", ", RegroupedGroups)}");
                if (Errors.Count > 0)
                    parts.Add($"{Errors.Count} error(s): {string.Join("; ", Errors.Take(5))}");
                return string.Join("\n", parts);
            }
        }
    }
}
