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

        /// <summary>
        /// True when the element's Revit type name contains "_Channel" (case-insensitive).
        /// These are mesh cable tray elements (both straight CableTray and CableTrayFitting
        /// variants) that are factory-bent as one continuous piece. They follow different
        /// slicing rules: fittings contribute their length to the stock-piece count,
        /// remainder pieces are rounded up to the nearest 100 mm, and QuickSlice is
        /// disabled for them entirely.
        /// </summary>
        public bool IsMeshChannel { get; set; }

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