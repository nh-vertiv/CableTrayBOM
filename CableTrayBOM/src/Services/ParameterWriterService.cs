using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using CableTrayBOM.Models;

namespace CableTrayBOM.Services
{
    /// <summary>
    /// Writes calculated joint material quantities into each element's Revit shared parameters
    /// (G-Joint Material group). Each individual piece gets its values based on its actual
    /// support count and connection count, not schedule-level aggregates.
    /// </summary>
    public class ParameterWriterService
    {
        private readonly Document _doc;
        private readonly BOMSettings _settings;
        private readonly JointMaterialService _jmService;
        private readonly string _unassignedRoomLabel;

        public ParameterWriterService(Document doc, BOMSettings settings, string unassignedRoomLabel = "Unassigned")
        {
            _doc = doc;
            _settings = settings;
            _jmService = new JointMaterialService(settings);
            _unassignedRoomLabel = unassignedRoomLabel;
        }

        /// <summary>
        /// Write joint material values to each cable tray element based on its actual
        /// support count and connection count. Must be called inside a Transaction.
        /// </summary>
        public int WriteCableTrayParameters(List<CableTraySegment> segments)
        {
            int updated = 0;
            foreach (var seg in segments)
            {
                var elem = _doc.GetElement(RevitCompat.ToElementId(seg.ElementId));
                if (elem == null) continue;

                bool isInGroup = elem.GroupId != ElementId.InvalidElementId;

                // For grouped elements: ensure VaryByGroup is set before writing
                if (isInGroup)
                    EnsureVaryByGroupOnAllParams(elem);

                // Reset ALL joint material params to zero first
                ZeroAllParams(elem);

                // Now write calculated values
                var jm = _jmService.CalculateSegmentJointMaterials(seg);
                bool any = false;

                any |= SafeSetNum(elem, SharedParameterMap.Count, seg.SupportCount, isInGroup);
                any |= SafeSetNum(elem, SharedParameterMap.CountConnections, seg.ConnectionCount, isInGroup);
                int orderQty = seg.IsFitting ? 1 : (int)Math.Ceiling(seg.OriginalLength / _settings.DefaultSliceLength);
                any |= SafeSetNumByName(elem, "V_Order_Quantity", orderQty, isInGroup);
                any |= SafeSetText(elem, SharedParameterMap.MountingType, seg.Mounting.ToString(), isInGroup);

                // V_BOM_Description = Type name + Size (e.g. "Ladder Cable Tray 300x100")
                string bomDesc = $"{seg.Description} {seg.Size}".Trim();
                any |= SafeSetTextByName(elem, "V_BOM_Description", bomDesc, isInGroup);

                // V_Room_Name = "[Room Number] - [Room Name]" or "Unassigned" for empty
                string roomDisplay = BuildRoomDisplay(seg.RoomNumber, seg.RoomName);
                bool hasRoom = !string.IsNullOrEmpty(roomDisplay);
                if (!hasRoom)
                    roomDisplay = _unassignedRoomLabel;
                any |= SafeSetTextByName(elem, "V_Room_Name", roomDisplay, isInGroup);

                // V_Room Name Manual - auto-populate when room is recognized
                if (hasRoom)
                {
                    try
                    {
                        var manualParam = elem.LookupParameter("V_Room Name Manual");
                        if (manualParam != null && !manualParam.IsReadOnly &&
                            manualParam.StorageType == StorageType.String &&
                            string.IsNullOrEmpty(manualParam.AsString()))
                        {
                            manualParam.Set(roomDisplay);
                        }
                    }
                    catch { } // Silently skip if group constraint prevents write
                }

                foreach (var kvp in jm)
                {
                    if (SharedParameterMap.NameToGuid.TryGetValue(kvp.Key, out Guid guid))
                        any |= SetNum(elem, guid, kvp.Value);
                }

                if (any) updated++;
            }
            return updated;
        }

        /// <summary>
        /// Write joint material values to each fixture element.
        /// </summary>
        public int WriteFixtureParameters(List<FixtureElement> fixtures)
        {
            int updated = 0;
            foreach (var fixture in fixtures)
            {
                var elem = _doc.GetElement(RevitCompat.ToElementId(fixture.ElementId));
                if (elem == null) continue;

                // ── Reset ALL joint material params to zero first ──
                ZeroAllParams(elem);

                // ── Now write calculated values ──
                var jm = _jmService.CalculateFixtureJointMaterials(fixture);
                bool any = false;

                any |= SetText(elem, SharedParameterMap.MountingType, fixture.Mounting.ToString());
                any |= SetTextByName(elem, "V_BOM_Description", $"{fixture.Equipment} {fixture.Description}".Trim());
                string fxRoom = BuildRoomDisplay(fixture.RoomNumber, fixture.RoomName);
                bool fxHasRoom = !string.IsNullOrEmpty(fxRoom);
                if (!fxHasRoom)
                    fxRoom = _unassignedRoomLabel;
                any |= SetTextByName(elem, "V_Room_Name", fxRoom);

                // V_Room Name Manual - auto-populate when room is recognized
                if (fxHasRoom)
                {
                    var manualParam = elem.LookupParameter("V_Room Name Manual");
                    if (manualParam != null && !manualParam.IsReadOnly &&
                        manualParam.StorageType == StorageType.String &&
                        string.IsNullOrEmpty(manualParam.AsString()))
                    {
                        any |= manualParam.Set(fxRoom);
                    }
                }

                foreach (var kvp in jm)
                {
                    if (SharedParameterMap.NameToGuid.TryGetValue(kvp.Key, out Guid guid))
                    {
                        if (kvp.Key.Contains("Flexible conduit") && guid == SharedParameterMap.FlexibleConduit)
                        {
                            double ft = UnitUtils.ConvertToInternalUnits(kvp.Value, UnitTypeId.Millimeters);
                            any |= SetDbl(elem, guid, ft);
                        }
                        else
                        {
                            any |= SetNum(elem, guid, kvp.Value);
                        }
                    }
                }
                if (any) updated++;
            }
            return updated;
        }

        private bool SetNum(Element e, Guid guid, int val)
        {
            var p = e.get_Parameter(guid);
            if (p == null || p.IsReadOnly) return false;
            try { return p.StorageType == StorageType.Double ? p.Set((double)val) : p.Set(val); }
            catch { return false; }
        }

        private bool SetDbl(Element e, Guid guid, double val)
        {
            var p = e.get_Parameter(guid);
            if (p == null || p.IsReadOnly || p.StorageType != StorageType.Double) return false;
            try { return p.Set(val); } catch { return false; }
        }

        private bool SetText(Element e, Guid guid, string val)
        {
            var p = e.get_Parameter(guid);
            if (p == null || p.IsReadOnly || p.StorageType != StorageType.String) return false;
            try { return p.Set(val); } catch { return false; }
        }

        private bool SetTextByName(Element e, string paramName, string val)
        {
            var p = e.LookupParameter(paramName);
            if (p == null || p.IsReadOnly || p.StorageType != StorageType.String) return false;
            try { return p.Set(val ?? ""); } catch { return false; }
        }

        private bool SetNumByName(Element e, string paramName, double val)
        {
            var p = e.LookupParameter(paramName);
            if (p == null || p.IsReadOnly) return false;
            try
            {
                if (p.StorageType == StorageType.Double) return p.Set(val);
                if (p.StorageType == StorageType.Integer) return p.Set((int)val);
                return false;
            }
            catch { return false; }
        }

        private static string BuildRoomDisplay(string? roomNumber, string? roomName)
        {
            bool hasNum = !string.IsNullOrEmpty(roomNumber);
            bool hasName = !string.IsNullOrEmpty(roomName);
            if (hasNum && hasName) return $"{roomNumber} - {roomName}";
            if (hasNum) return roomNumber!;
            if (hasName) return roomName!;
            return "";
        }

        // ── GROUP-SAFE WRITE METHODS ──
        // These catch exceptions from group constraints so writing to grouped elements
        // doesn't dissolve the group. If a param write fails, it's silently skipped.

        private bool SafeSetNum(Element e, Guid guid, int val, bool isInGroup)
        {
            try { return SetNum(e, guid, val); }
            catch { if (isInGroup) return false; throw; }
        }
        private bool SafeSetText(Element e, Guid guid, string val, bool isInGroup)
        {
            try { return SetText(e, guid, val); }
            catch { if (isInGroup) return false; throw; }
        }
        private bool SafeSetTextByName(Element e, string name, string val, bool isInGroup)
        {
            try { return SetTextByName(e, name, val); }
            catch { if (isInGroup) return false; throw; }
        }
        private bool SafeSetNumByName(Element e, string name, double val, bool isInGroup)
        {
            try { return SetNumByName(e, name, val); }
            catch { if (isInGroup) return false; throw; }
        }

        /// <summary>
        /// For elements inside groups: ensure all writable shared parameters have
        /// "Values can vary by group instance" set to true. This must be done before
        /// writing values, otherwise Revit will dissolve the group.
        /// </summary>
        private void EnsureVaryByGroupOnAllParams(Element elem)
        {
            try
            {
                var bindMap = _doc.ParameterBindings;
                var iter = bindMap.ForwardIterator();
                while (iter.MoveNext())
                {
                    if (iter.Key is InternalDefinition intDef)
                    {
                        try
                        {
                            if (!intDef.VariesAcrossGroups)
                                intDef.SetAllowVaryBetweenGroups(_doc, true);
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// Zero ALL joint material parameters and counts on an element.
        /// Called before writing new values so every run starts from zero.
        /// </summary>
        private void ZeroAllParams(Element elem)
        {
            // Zero counts
            SetNum(elem, SharedParameterMap.Count, 0);
            SetNum(elem, SharedParameterMap.CountConnections, 0);
            SetNum(elem, SharedParameterMap.L1Count, 0);
            SetNum(elem, SharedParameterMap.L2Count, 0);
            SetNum(elem, SharedParameterMap.L3Count, 0);

            // Zero all containment joint materials
            SetNum(elem, SharedParameterMap.Bolt_M6x20_137, 0);
            SetNum(elem, SharedParameterMap.ThreadedPlate_119, 0);
            SetNum(elem, SharedParameterMap.ClampForScrew_100, 0);
            SetNum(elem, SharedParameterMap.Washer9021_M6_23, 0);
            SetNum(elem, SharedParameterMap.Bolt_M6x25_21, 0);
            SetNum(elem, SharedParameterMap.Nut934_M6_33, 0);
            SetNum(elem, SharedParameterMap.Bolt933_M6x12_66, 0);
            SetNum(elem, SharedParameterMap.RailNut_101, 0);
            SetNum(elem, SharedParameterMap.Washer125_M6_4, 0);
            SetNum(elem, SharedParameterMap.GKS34FT_99, 0);
            SetNum(elem, SharedParameterMap.GSV34FT_124, 0);
            SetNum(elem, SharedParameterMap.StraightConnector, 0);
            SetNum(elem, SharedParameterMap.TrussHeadBolt6x12, 0);
            SetNum(elem, SharedParameterMap.JointPlate, 0);
            SetNum(elem, SharedParameterMap.TrussHeadFlangeNut6x16, 0);
            SetNum(elem, SharedParameterMap.FibreRunnerCoupler, 0);

            // Zero fixture joint materials
            SetNum(elem, SharedParameterMap.ISO7380_M6x20_85, 0);
            SetNum(elem, SharedParameterMap.SideHolder_132, 0);
            SetNum(elem, SharedParameterMap.Locknut_133, 0);
            SetNum(elem, SharedParameterMap.MaleFixedFitting_134, 0);
            SetNum(elem, SharedParameterMap.RubberEnd_139, 0);
            SetNum(elem, SharedParameterMap.JF2_V16ZN_38, 0);
            SetNum(elem, SharedParameterMap.JF2_V16_38, 0);
            SetNum(elem, SharedParameterMap.MountingPlate_92, 0);
            SetNum(elem, SharedParameterMap.SteelConduit, 0);
            SetNum(elem, SharedParameterMap.GClampZN, 0);
            SetNum(elem, SharedParameterMap.Bolt933_M8x25_30, 0);
            SetNum(elem, SharedParameterMap.Washer127_M8_7, 0);
            SetNum(elem, SharedParameterMap.Washer9021_M8_24, 0);
            SetNum(elem, SharedParameterMap.Nut934_M8_54, 0);
            SetNum(elem, SharedParameterMap.Washer125_M8_5, 0);
            SetNum(elem, SharedParameterMap.Screw7504N_35x13_86, 0);
            SetNum(elem, SharedParameterMap.Screw7504N_42x16_136, 0);

            // Zero flexible conduit (LENGTH type)
            SetDbl(elem, SharedParameterMap.FlexibleConduit, 0.0);
        }
    }
}
