using System;
using System.Collections.Generic;

namespace CableTrayBOM.Models
{
    // ═══════════════════════════════════════════════════════════════════
    // ENUMERATIONS
    // ═══════════════════════════════════════════════════════════════════

    public enum TrayCategory
    {
        MeshCableTray,
        PerforatedCableTray,
        NonPerforatedCableTray,
        CableLadder,
        FiberTray
    }

    public enum MountingType
    {
        SupportChannel,  // Mupro profile mounting
        Console,         // Console/bracket mounting
        Panel,           // Panel mounting
        Steel            // Direct steel mounting
    }

    public enum FittingType
    {
        Straight,
        Elbow90,
        Elbow45,
        Tee,
        Cross,
        Reducer,
        RiserUp,     // Vertical rise
        RiserDown,   // Vertical drop
        OffsetUp,
        OffsetDown
    }

    public enum FixtureCategory
    {
        LightingFixture,
        EmergencyLight,
        PanicLight,
        Socket,
        FusedSpur,
        PresenceDetector,
        JunctionBoxAC,
        JunctionBoxEmergency,
        JunctionBoxFDP,
        JunctionBoxSignal,
        JunctionBoxSignalDC,
        JunctionBoxDoorBox,
        FlexibleConduit,
        CableGland
    }

    public enum Orientation
    {
        Horizontal,
        Vertical,
        Sloped
    }

    // ═══════════════════════════════════════════════════════════════════
    // CABLE TRAY / LADDER SEGMENT MODEL
    // ═══════════════════════════════════════════════════════════════════

    public class CableTraySegment
    {
        public string ElementId { get; set; } = "";
        public string RevitUniqueId { get; set; } = "";
        public TrayCategory TrayType { get; set; }
        public MountingType Mounting { get; set; }
        public string Size { get; set; } = "";        // e.g. "100x55", "300x105"
        public double Width { get; set; }              // mm
        public double Height { get; set; }             // mm
        public double OriginalLength { get; set; }     // mm - total original length
        public string PartNumber { get; set; } = "";
        public string Manufacturer { get; set; } = "";
        public string Description { get; set; } = "";
        public string RoomName { get; set; } = "";
        public string RoomNumber { get; set; } = "";
        public string Level { get; set; } = "";
        public string ServiceType { get; set; } = "";    // Service Type shared parameter
        public bool IsInGroup { get; set; }               // Element is inside a Revit Group
        public string GroupName { get; set; } = "";       // Name of the group (if any)
        public Orientation Orientation { get; set; }
        public int SupportCount { get; set; }          // Number of supports for THIS element
        public int RunSupportCount { get; set; }       // Total supports for the entire connected run
        public int RunId { get; set; }                 // Connected run identifier
        public bool IsFitting { get; set; }            // Is this a fitting (bend, tee, etc.)
        public FittingType? Fitting { get; set; }

        // Calculated after slicing
        public List<SlicedPiece> SlicedPieces { get; set; } = new();
        public int ConnectionCount { get; set; }       // Number of connections/couplings
    }

    public class SlicedPiece
    {
        public double Length { get; set; }             // mm
        public double LengthWithCoupling { get; set; } // mm (adds 1mm per joint)
        public bool IsFullLength { get; set; }
        public int OrderPieces { get; set; }           // How many standard pieces to order
    }

    // ═══════════════════════════════════════════════════════════════════
    // JOINT MATERIAL MODEL
    // ═══════════════════════════════════════════════════════════════════

    public class JointMaterial
    {
        public string Code { get; set; } = "";         // e.g. "(137)", "(119)"
        public string Name { get; set; } = "";
        public string SAPCode { get; set; } = "";
        public int QuantityPerSupport { get; set; }    // qty per Mupro support
        public int QuantityPerConnection { get; set; } // qty per segment connection
        public string Formula { get; set; } = "";      // e.g. "2 per support", "15% from GKS34FT"
        public bool IsPercentageBased { get; set; }
        public double Percentage { get; set; }
        public string PercentageOf { get; set; } = ""; // reference material code
    }

    public class JointMaterialQuantity
    {
        public JointMaterial Material { get; set; } = new();
        public int TotalQuantity { get; set; }
        public string CalculationNote { get; set; } = "";
    }

    // ═══════════════════════════════════════════════════════════════════
    // FIXTURE / EQUIPMENT MODEL
    // ═══════════════════════════════════════════════════════════════════

    public class FixtureElement
    {
        public string ElementId { get; set; } = "";
        public string RevitUniqueId { get; set; } = "";
        public FixtureCategory Category { get; set; }
        public MountingType Mounting { get; set; }
        public string PartNumber { get; set; } = "";
        public string SAPCode { get; set; } = "";
        public string Manufacturer { get; set; } = "";
        public string Equipment { get; set; } = "";
        public string Description { get; set; } = "";
        public string Dimensions { get; set; } = "";
        public double Length { get; set; }             // meters
        public int Quantity { get; set; } = 1;
        public string Unit { get; set; } = "pcs";
        public string RoomName { get; set; } = "";
        public string RoomNumber { get; set; } = "";
        public string Level { get; set; } = "";
        public string ServiceType { get; set; } = "";    // Service Type shared parameter
    }

    public class FixtureJointMaterial
    {
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
        public string Formula { get; set; } = "";      // e.g. "2 * Quantity", "Quantity"
        public int Multiplier { get; set; } = 1;
        public bool IsFixedQuantity { get; set; }
        public int FixedQuantity { get; set; }
    }

    // ═══════════════════════════════════════════════════════════════════
    // BOM LINE ITEM (OUTPUT)
    // ═══════════════════════════════════════════════════════════════════

    public class BOMLineItem
    {
        public int Number { get; set; }
        public string PartNumber { get; set; } = "";
        public string SAPCode { get; set; } = "";
        public string Manufacturer { get; set; } = "";
        public string Description { get; set; } = "";
        public int QuantityPieces { get; set; }
        public double TotalLengthMeters { get; set; }
        public int OrderPieces { get; set; }           // Rounded up full pieces to order
        public string RoomName { get; set; } = "";
        public string RoomNumber { get; set; } = "";
        public Dictionary<string, int> JointMaterials { get; set; } = new();
        public string Comment { get; set; } = "";
    }

    // ═══════════════════════════════════════════════════════════════════
    // ROOM SUMMARY
    // ═══════════════════════════════════════════════════════════════════

    public class RoomBOMSummary
    {
        public string RoomName { get; set; } = "";
        public string RoomNumber { get; set; } = "";
        public string Level { get; set; } = "";
        public List<BOMLineItem> CableTrayItems { get; set; } = new();
        public List<BOMLineItem> CableLadderItems { get; set; } = new();
        public List<BOMLineItem> FixtureItems { get; set; } = new();
        public Dictionary<string, int> TotalJointMaterials { get; set; } = new();
    }

    // ═══════════════════════════════════════════════════════════════════
    // SETTINGS
    // ═══════════════════════════════════════════════════════════════════

    public class BOMSettings
    {
        public double DefaultSliceLength { get; set; } = 3000;  // mm (3 meters)
        public double CouplingGap { get; set; } = 1;            // mm
        public double GSV34FTPercentage { get; set; } = 15;     // % of GKS34FT
        public bool IncludeEarthingBridge { get; set; } = true;
        public bool RoundUpOrderQuantity { get; set; } = true;

        // Joint material quantities per support (Mupro) - Cable Trays
        public Dictionary<string, int> MeshTrayJointPerSupport { get; set; } = new()
        {
            { "(137) Bolt DIN 7985 M6x20 ZN", 2 },
            { "(119) Threaded plate M6 ZN", 2 },
            { "(100) Clamp for screw M6", 2 }
        };

        // Joint material quantities per connection - Mesh Cable Tray
        public Dictionary<string, int> MeshTrayJointPerConnection { get; set; } = new()
        {
            { "(99) Clamping element GKS34FT", 3 },
        };

        // Cable Ladder joint per support
        public Dictionary<string, int> LadderJointPerSupport { get; set; } = new()
        {
            { "(137) Bolt DIN 7985 M6x20 ZN", 2 },
            { "(23) Washer DIN 9021 M6 ZN", 2 },
            { "(119) Threaded plate M6 ZN", 2 }
        };

        // Cable Ladder joint per connection
        public Dictionary<string, int> LadderJointPerConnection { get; set; } = new()
        {
            { "(N/A) Straight connector", 2 },
            { "(N/A) Truss-head bolt with nut 6x12", 8 }  // 4 per connector, 2 connectors
        };

        // Perforated Cable Tray joint per support
        public Dictionary<string, int> PerforatedTrayJointPerSupport { get; set; } = new()
        {
            { "(137) Bolt DIN 7985 M6x20 ZN", 2 },
            { "(23) Washer DIN 9021 M6 ZN", 2 },
            { "(119) Threaded plate M6 ZN", 2 }
        };

        // Perforated Cable Tray joint per connection
        public Dictionary<string, int> PerforatedTrayJointPerConnection { get; set; } = new()
        {
            { "(N/A) Straight connector", 2 },
            { "(N/A) Joint plate", 1 },
            { "(N/A) Truss-head bolt with nut 6x12", 16 }  // 8 per connector + 8 per joint plate
        };

        // Non-Perforated Cable Tray joint per support
        public Dictionary<string, int> NonPerforatedTrayJointPerSupport { get; set; } = new()
        {
            { "(137) Bolt DIN 7985 M6x20 ZN", 2 },
            { "(23) Washer DIN 9021 M6 ZN", 2 },
            { "(119) Threaded plate M6 ZN", 2 }
        };

        // Non-Perforated Cable Tray joint per connection
        public Dictionary<string, int> NonPerforatedTrayJointPerConnection { get; set; } = new()
        {
            { "Truss-head bolt with flange nut 6x16", 2 }
        };

        // Fiber Tray joint per support (Console mounting)
        public Dictionary<string, int> FiberTrayJointPerSupport { get; set; } = new()
        {
            { "(66) Bolt DIN 933 M6x12 8.8 ZN", 2 },
            { "(101) Rail nut M6 18X18", 2 },
            { "(4) Washer DIN 125 M6 ZN", 2 }
        };

        // Fiber Tray joint per connection
        public Dictionary<string, int> FiberTrayJointPerConnection { get; set; } = new()
        {
            { "(N/A) Fibre Runner Coupler", 1 }
        };

        // Mesh Tray to Console joint per support
        public Dictionary<string, int> MeshTrayConsoleJointPerSupport { get; set; } = new()
        {
            { "(21) Bolt DIN 7985 M6x25 ZN", 2 },
            { "(100) Clamp for screw M6", 2 },
            { "(33) Nut DIN 934 M6 ZN", 2 }
        };

        // Mesh Tray to Console joint per connection
        public Dictionary<string, int> MeshTrayConsoleJointPerConnection { get; set; } = new()
        {
            { "(99) Clamping element GKS34FT", 3 },
        };

        // ───────────────────────────────────────────────────────────
        // FIXTURE JOINT MATERIALS
        // ───────────────────────────────────────────────────────────

        // Lighting Fixture - Support Channel Mounting
        public Dictionary<string, int> LightingChannelJoint { get; set; } = new()
        {
            { "(85) ISO 7380 MF M6x20 10.9 ZN", 2 },
            { "(119) Threaded plate M6 ZN", 2 }
        };

        // Lighting Fixture conduit materials per fixture
        public Dictionary<string, int> LightingConduitMaterials { get; set; } = new()
        {
            { "(132) SIDE HOLDER FOR CABLE GLAND 25mm", 1 },
            { "(133) LOCKNUT 25mm FOR FLEXIBLE CONDUIT GLAND", 2 },
            { "(134) MALE FIXED FITTING + RING 25mm", 2 }
        };

        public double FlexibleConduitLengthPerFixtureMm { get; set; } = 400;

        // Panel mounting
        public Dictionary<string, int> LightingPanelJoint { get; set; } = new()
        {
            { "(38) JF2-2-5.5X25-V16ZN", 0 },  // varies
            { "(23) DIN9021-M6-ZN", 0 }          // varies
        };

        // Junction Box - Mesh Tray Mounting
        public Dictionary<string, int> JunctionBoxMeshTrayJoint { get; set; } = new()
        {
            { "(92) MOUNTING PLATE FOR MESH CABLE TRAY MPG 90 FT", 1 },
            { "(21) BOLT DIN 7985 M6X25 ZN", 4 },
            { "(4) WASHER DIN 125 M6 ZN", 8 },
            { "(33) NUT DIN 934 M6 ZN", 4 },
            { "(132) SIDE HOLDER FOR CABLE GLAND 25mm", 1 },
            { "(133) LOCKNUT 25mm FOR FLEXIBLE CONDUIT GLAND", 1 },
            { "(134) MALE FIXED FITTING + RING 25mm", 1 },
            { "(139) RUBBER END GZ16", 1 }
        };

        // Junction Box - Panel Mounting
        public Dictionary<string, int> JunctionBoxPanelJoint { get; set; } = new()
        {
            { "(38) JF2-2-5.5x25-V16", 4 },
            { "(133) LOCKNUT 25mm FOR FLEXIBLE CONDUIT GLAND", 1 },
            { "(134) MALE FIXED FITTING + RING 25mm", 1 },
            { "(139) RUBBER END GZ16", 1 }
        };

        // Socket/Spur - Panel Mounting
        public Dictionary<string, int> SocketPanelJoint { get; set; } = new()
        {
            { "(38) JF2-2-5.5X25-V16ZN", 0 },
            { "(23) DIN9021-M6-ZN", 0 },
            { "(133) LOCKNUT 25mm FOR FLEXIBLE CONDUIT GLAND", 0 },
            { "(134) MALE FIXED FITTING + RING 25mm", 0 }
        };

        // ───────────────────────────────────────────────────────────
        // RECTANGULAR PRISM DETECTION TOLERANCES
        // The Z-check uses the tray BOUNDING BOX Z range (not centerline).
        // "Above" means above the BB top, "Below" means below BB bottom.
        // Suspension insertion points are typically 0-500mm above tray top.
        // ───────────────────────────────────────────────────────────
        public double VerticalToleranceAboveMm { get; set; } = 500.0;
        public double VerticalToleranceBelowMm { get; set; } = 50.0;
        public double HorizontalToleranceMm { get; set; } = 100.0;
        public double TrayEndExtensionMm { get; set; } = 50.0;
    }
}
