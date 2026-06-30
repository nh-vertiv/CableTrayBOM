using System;
using System.Collections.Generic;
using System.Linq;
using CableTrayBOM.Models;

namespace CableTrayBOM.Services
{
    /// <summary>
    /// Calculates joint material quantities for cable trays, ladders, and fixtures.
    /// 
    /// Joint materials are counted based on:
    /// - Number of Mupro suspension supports (per support quantities)
    /// - Number of connections between segments (per connection quantities)
    /// - Mounting type affects which materials are needed
    /// - Some materials are percentage-based (e.g., GSV34FT = 15% of GKS34FT)
    /// </summary>
    public class JointMaterialService
    {
        private readonly BOMSettings _settings;

        public JointMaterialService(BOMSettings settings)
        {
            _settings = settings;
        }

        // ═══════════════════════════════════════════════════════════════
        // CABLE TRAY / LADDER JOINT MATERIALS
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Calculate all joint materials for a cable tray/ladder segment.
        /// </summary>
        public Dictionary<string, int> CalculateSegmentJointMaterials(CableTraySegment segment)
        {
            var result = new Dictionary<string, int>();
            int supportCount = segment.SupportCount;
            int connectionCount = segment.ConnectionCount;

            // Get the right material sets based on tray type and mounting
            var perSupport = GetPerSupportMaterials(segment.TrayType, segment.Mounting);
            var perConnection = GetPerConnectionMaterials(segment.TrayType, segment.Mounting);

            // Calculate per-support materials
            foreach (var mat in perSupport)
            {
                int qty = mat.Value * supportCount;
                AddToDict(result, mat.Key, qty);
            }

            // Calculate per-connection materials
            foreach (var mat in perConnection)
            {
                int qty = mat.Value * connectionCount;
                AddToDict(result, mat.Key, qty);
            }

            // Handle percentage-based materials (GSV34FT = 15% of GKS34FT)
            if (segment.TrayType == TrayCategory.MeshCableTray)
            {
                string gksKey = "(99) Clamping element GKS34FT";
                string gsvKey = "(124) Clamping element GSV34FT";

                if (result.ContainsKey(gksKey))
                {
                    int gksQty = result[gksKey];
                    int gsvQty = (int)Math.Ceiling(gksQty * (_settings.GSV34FTPercentage / 100.0));
                    AddToDict(result, gsvKey, gsvQty);
                }
            }

            // Add earthing bridge at each connection if enabled
            if (_settings.IncludeEarthingBridge && connectionCount > 0)
            {
                AddToDict(result, "Earthing bridge", connectionCount);
            }

            return result;
        }

        /// <summary>
        /// Calculate joint materials for an entire collection of segments, grouped by room.
        /// </summary>
        public Dictionary<string, Dictionary<string, int>> CalculateByRoom(
            IEnumerable<CableTraySegment> segments)
        {
            var roomMap = new Dictionary<string, Dictionary<string, int>>();

            var groupedByRoom = segments.GroupBy(s =>
                string.IsNullOrEmpty(s.RoomNumber) ? s.RoomName : $"{s.RoomNumber} - {s.RoomName}");

            foreach (var roomGroup in groupedByRoom)
            {
                string roomKey = string.IsNullOrEmpty(roomGroup.Key) ? "Unassigned" : roomGroup.Key;
                var roomMaterials = new Dictionary<string, int>();

                foreach (var segment in roomGroup)
                {
                    var segMaterials = CalculateSegmentJointMaterials(segment);
                    foreach (var mat in segMaterials)
                    {
                        AddToDict(roomMaterials, mat.Key, mat.Value);
                    }
                }

                roomMap[roomKey] = roomMaterials;
            }

            return roomMap;
        }

        /// <summary>
        /// Calculate total joint materials across all segments.
        /// </summary>
        public Dictionary<string, int> CalculateTotal(IEnumerable<CableTraySegment> segments)
        {
            var total = new Dictionary<string, int>();

            foreach (var segment in segments)
            {
                var segMaterials = CalculateSegmentJointMaterials(segment);
                foreach (var mat in segMaterials)
                {
                    AddToDict(total, mat.Key, mat.Value);
                }
            }

            return total;
        }

        // ═══════════════════════════════════════════════════════════════
        // FIXTURE JOINT MATERIALS
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Calculate joint materials for a lighting fixture or equipment element.
        /// </summary>
        public Dictionary<string, int> CalculateFixtureJointMaterials(FixtureElement fixture)
        {
            var result = new Dictionary<string, int>();
            int qty = fixture.Quantity;

            switch (fixture.Category)
            {
                case FixtureCategory.LightingFixture:
                case FixtureCategory.EmergencyLight:
                    CalculateLightingJointMaterials(fixture, result);
                    break;

                case FixtureCategory.PanicLight:
                    // Panic lights typically have no joint materials (N/A)
                    break;

                case FixtureCategory.Socket:
                case FixtureCategory.FusedSpur:
                case FixtureCategory.PresenceDetector:
                    CalculateSocketJointMaterials(fixture, result);
                    break;

                case FixtureCategory.JunctionBoxAC:
                case FixtureCategory.JunctionBoxEmergency:
                case FixtureCategory.JunctionBoxFDP:
                case FixtureCategory.JunctionBoxSignal:
                case FixtureCategory.JunctionBoxSignalDC:
                case FixtureCategory.JunctionBoxDoorBox:
                    CalculateJunctionBoxJointMaterials(fixture, result);
                    break;
            }

            return result;
        }

        private void CalculateLightingJointMaterials(FixtureElement fixture,
            Dictionary<string, int> result)
        {
            int qty = fixture.Quantity;

            switch (fixture.Mounting)
            {
                case MountingType.SupportChannel:
                    // ISO bolt + Threaded plate, 2 each per fixture
                    foreach (var mat in _settings.LightingChannelJoint)
                        AddToDict(result, mat.Key, mat.Value * qty);

                    // Flexible conduit and fittings
                    AddToDict(result, "Flexible conduit (mm)",
                        (int)(_settings.FlexibleConduitLengthPerFixtureMm * qty));
                    foreach (var mat in _settings.LightingConduitMaterials)
                        AddToDict(result, mat.Key, mat.Value * qty);
                    break;

                case MountingType.Panel:
                    // Panel-specific screws
                    AddToDict(result, "(38) JF2-2-5.5X25-V16ZN", 0); // varies by fixture
                    AddToDict(result, "(23) DIN9021-M6-ZN", 0);

                    // Conduit materials
                    AddToDict(result, "Flexible conduit", qty);
                    AddToDict(result, "(133) LOCKNUT 25mm FOR FLEXIBLE CONDUIT GLAND", 2 * qty);
                    AddToDict(result, "(134) MALE FIXED FITTING + RING 25mm", 2 * qty);
                    AddToDict(result, "Steel conduit", qty);
                    AddToDict(result, "(38) JF2-2-5.5x25-V16", 2 * qty);
                    break;

                case MountingType.Steel:
                    AddToDict(result, "JT2-6-5.5X19", 0); // varies by fixture
                    break;
            }
        }

        private void CalculateSocketJointMaterials(FixtureElement fixture,
            Dictionary<string, int> result)
        {
            int qty = fixture.Quantity;

            switch (fixture.Mounting)
            {
                case MountingType.Panel:
                    AddToDict(result, "(38) JF2-2-5.5X25-V16ZN", 0);
                    AddToDict(result, "(23) DIN9021-M6-ZN", 0);
                    AddToDict(result, "Flexible conduit", qty);
                    AddToDict(result, "(133) LOCKNUT 25mm FOR FLEXIBLE CONDUIT GLAND", 2 * qty);
                    AddToDict(result, "(134) MALE FIXED FITTING + RING 25mm", 2 * qty);
                    AddToDict(result, "Steel conduit", qty);
                    AddToDict(result, "(38) JF2-2-5.5x25-V16", 2 * qty);
                    break;

                case MountingType.SupportChannel:
                    AddToDict(result, "Self Drilling Screw JF2-2-5.5x25 with Sealing Washer", 0);
                    AddToDict(result, "(23) Washer DIN 9021 M6 ZN", 0);
                    break;
            }
        }

        private void CalculateJunctionBoxJointMaterials(FixtureElement fixture,
            Dictionary<string, int> result)
        {
            int qty = fixture.Quantity;

            switch (fixture.Mounting)
            {
                case MountingType.SupportChannel: // Mesh tray mounting
                    foreach (var mat in _settings.JunctionBoxMeshTrayJoint)
                        AddToDict(result, mat.Key, mat.Value * qty);
                    AddToDict(result, "Flexible conduit", qty);
                    break;

                case MountingType.Panel:
                    foreach (var mat in _settings.JunctionBoxPanelJoint)
                        AddToDict(result, mat.Key, mat.Value * qty);
                    AddToDict(result, "Flexible conduit", qty);
                    AddToDict(result, "Steel conduit", qty);
                    AddToDict(result, "(38) JF2-2-5.5x25-V16 (conduit)", 2 * qty);
                    break;
            }
        }

        /// <summary>
        /// Calculate fixture joint materials grouped by room.
        /// </summary>
        public Dictionary<string, Dictionary<string, int>> CalculateFixturesByRoom(
            IEnumerable<FixtureElement> fixtures)
        {
            var roomMap = new Dictionary<string, Dictionary<string, int>>();

            var groupedByRoom = fixtures.GroupBy(f =>
                string.IsNullOrEmpty(f.RoomNumber) ? f.RoomName : $"{f.RoomNumber} - {f.RoomName}");

            foreach (var roomGroup in groupedByRoom)
            {
                string roomKey = string.IsNullOrEmpty(roomGroup.Key) ? "Unassigned" : roomGroup.Key;
                var roomMaterials = new Dictionary<string, int>();

                foreach (var fixture in roomGroup)
                {
                    var fixMaterials = CalculateFixtureJointMaterials(fixture);
                    foreach (var mat in fixMaterials)
                    {
                        AddToDict(roomMaterials, mat.Key, mat.Value);
                    }
                }

                roomMap[roomKey] = roomMaterials;
            }

            return roomMap;
        }

        // ═══════════════════════════════════════════════════════════════
        // HELPERS
        // ═══════════════════════════════════════════════════════════════

        private Dictionary<string, int> GetPerSupportMaterials(TrayCategory type, MountingType mounting)
        {
            return (type, mounting) switch
            {
                (TrayCategory.MeshCableTray, MountingType.SupportChannel) => _settings.MeshTrayJointPerSupport,
                (TrayCategory.MeshCableTray, MountingType.Console) => _settings.MeshTrayConsoleJointPerSupport,
                (TrayCategory.CableLadder, _) => _settings.LadderJointPerSupport,
                (TrayCategory.PerforatedCableTray, _) => _settings.PerforatedTrayJointPerSupport,
                (TrayCategory.NonPerforatedCableTray, _) => _settings.NonPerforatedTrayJointPerSupport,
                (TrayCategory.FiberTray, _) => _settings.FiberTrayJointPerSupport,
                _ => _settings.MeshTrayJointPerSupport
            };
        }

        private Dictionary<string, int> GetPerConnectionMaterials(TrayCategory type, MountingType mounting)
        {
            return (type, mounting) switch
            {
                (TrayCategory.MeshCableTray, MountingType.SupportChannel) => _settings.MeshTrayJointPerConnection,
                (TrayCategory.MeshCableTray, MountingType.Console) => _settings.MeshTrayConsoleJointPerConnection,
                (TrayCategory.CableLadder, _) => _settings.LadderJointPerConnection,
                (TrayCategory.PerforatedCableTray, _) => _settings.PerforatedTrayJointPerConnection,
                (TrayCategory.NonPerforatedCableTray, _) => _settings.NonPerforatedTrayJointPerConnection,
                (TrayCategory.FiberTray, _) => _settings.FiberTrayJointPerConnection,
                _ => _settings.MeshTrayJointPerConnection
            };
        }

        private static void AddToDict(Dictionary<string, int> dict, string key, int value)
        {
            if (dict.ContainsKey(key))
                dict[key] += value;
            else
                dict[key] = value;
        }
    }
}
