using System;
using System.Collections.Generic;

namespace CableTrayBOM.Models
{
    /// <summary>
    /// Maps G-Joint Material (GROUP 29) shared parameter names to their GUIDs
    /// from VERTIV-Shared_Parameters.txt. Used by ParameterWriterService to write
    /// calculated values directly into Revit elements.
    /// </summary>
    public static class SharedParameterMap
    {
        // ── Cable Tray Containment Joint Materials ──
        public static readonly Guid Bolt_M6x20_137       = new("5e6cb0e2-06ef-402f-9df8-865fa9f32d34"); // V_(137) Bolt DIN 7985 M6x20 ZN
        public static readonly Guid ThreadedPlate_119     = new("f471f316-4f93-4f14-8136-b614268df6d9"); // V_(119) Threaded plate M6 ZN
        public static readonly Guid ClampForScrew_100     = new("f98a3c0f-c06e-4e77-9546-8998cda03d9d"); // V_(100) Clamp for screw M6
        public static readonly Guid Washer9021_M6_23      = new("664f1764-aded-4744-96d9-0379e2ac4e2e"); // V_(23) Washer DIN 9021 M6 ZN
        public static readonly Guid Bolt_M6x25_21         = new("a7be6015-591d-4a0d-bb95-8f5d9a7edbbb"); // V_(21) Bolt DIN 7985 M6x25 ZN
        public static readonly Guid Nut934_M6_33          = new("2b984768-c3ab-4778-8727-491648eb48cc"); // V_(33) Nut DIN 934 M6 ZN
        public static readonly Guid Bolt933_M6x12_66      = new("cbd9a081-f898-4fb7-842e-933182f50000"); // V_(66) Bolt DIN 933 M6x12 8.8 ZN
        public static readonly Guid RailNut_101           = new("44748e83-cbe5-408c-808a-2fde8d27890e"); // V_(101) Rail nut M6 18X18
        public static readonly Guid Washer125_M6_4        = new("335df1c1-8f8d-4394-9e6f-cdcdd797de05"); // V_(4) Washer DIN 125 M6 ZN
        public static readonly Guid GKS34FT_99            = new("0b834f68-b8b5-4aa5-85ce-537c761eafcc"); // V_(99) Clamping element GKS34FT
        public static readonly Guid GSV34FT_124           = new("f1a45694-d7f7-4114-b22f-c4432b5edcc7"); // V_(124) Clamping element GSV34FT
        public static readonly Guid StraightConnector     = new("ff6d7f9b-f9e0-4cfa-b875-786580d2d321"); // V_Containment_Straight_Connector
        public static readonly Guid TrussHeadBolt6x12     = new("973dd2e2-899c-4a58-98c7-65da6fd5a9bc"); // V_Containment_Truss-head Bolt with Nut 6x12
        public static readonly Guid JointPlate            = new("5988ad5a-ba99-4aa5-b127-eb553907fab3"); // V_JointPlate
        public static readonly Guid TrussHeadFlangeNut6x16= new("b38a7209-4613-4b5e-a147-9bbb78a59c45"); // V_Containment_Truss-head bolt with flange nut 6x16
        public static readonly Guid FibreRunnerCoupler    = new("f0db8947-024a-4bc4-9a89-f048815f5bb7"); // V_(N/A) Fibre Runner Coupler

        // ── Count / Metadata ──
        public static readonly Guid Count                 = new("7883dc6a-dd5c-4c65-bbb3-7680c9802ac3"); // V_Count
        public static readonly Guid CountConnections      = new("6fb1e3bf-cae5-4a91-8711-25edc7a98ad2"); // V_Count_Connections
        public static readonly Guid L1Count               = new("38d85f60-40f3-4f8f-9afd-508fb33dd154"); // V_L1 Count
        public static readonly Guid L2Count               = new("7e728f83-c496-4fa2-bcd3-91a21dd4acee"); // V_L2 Count
        public static readonly Guid L3Count               = new("5e5915f2-7a81-4e26-a90f-6144b71a7589"); // V_L3 Count
        public static readonly Guid MountingType          = new("2c34789a-487d-411f-840d-d203b91a2ba6"); // V_Mounting_Type

        // ── Lighting / Small Power Joint Materials ──
        public static readonly Guid ISO7380_M6x20_85      = new("acec007a-6bd0-4ffb-ab5a-46f118ff5544"); // V_(85) ISO 7380 MF M6x20 10.9 ZN
        public static readonly Guid SideHolder_132        = new("99706329-c9d2-45e1-a18d-25b0c65e1489"); // V_(132) SIDE HOLDER FOR CABLE GLAND 25mm
        public static readonly Guid Locknut_133           = new("84a4a96f-662a-491f-8d74-b0e187186dab"); // V_(133) LOCKNUT 25mm
        public static readonly Guid MaleFixedFitting_134  = new("2ef3a787-4fcc-4e2f-a0fc-70c3f66e00de"); // V_(134) MALE FIXED FITTING + RING 25mm
        public static readonly Guid FlexibleConduit       = new("769c5543-5a29-475b-af0b-e900f6422c18"); // V_Flexible Conduit (LENGTH)
        public static readonly Guid RubberEnd_139         = new("70e08a16-767c-441e-a3a3-c168d9c79295"); // V_(139) RUBBER END GZ16
        public static readonly Guid JF2_V16ZN_38          = new("18e04e09-a4fe-4948-bcb8-8f36ac3e6c0f"); // V_(38) JF2-2-5.5X25-V16ZN
        public static readonly Guid JF2_V16_38            = new("39e9ddd3-ddcb-48d9-91db-3ab382d21499"); // V_(38) JF2-2-5.5x25-V16
        public static readonly Guid MountingPlate_92      = new("6fd286d4-b63b-422b-85ab-c1aca084b14d"); // V_(92) MOUNTING PLATE MPG 90 FT
        public static readonly Guid SteelConduit          = new("e04ad3ad-dd8d-445a-87f9-7fe8bb0be9c3"); // V_Steel Conduit
        public static readonly Guid GClampZN              = new("a693850f-b935-4afb-9c55-1e951f53929d"); // V_G-Clamp ZN

        // ── M8 size bolts/washers ──
        public static readonly Guid Bolt933_M8x25_30      = new("febd9836-bf06-44de-9283-d51e67a4c9a8"); // V_(30) Bolt DIN 933 M8x25 8.8 ZN
        public static readonly Guid Washer127_M8_7        = new("dd7f7c7b-7cc8-4231-aaac-990ad0612152"); // V_(7) Washer DIN 127 M8 ZN
        public static readonly Guid Washer9021_M8_24      = new("40681498-1af1-4df8-adf5-fe0e29851ba9"); // V_(24) Washer DIN 9021 M8 ZN
        public static readonly Guid Nut934_M8_54          = new("04c95384-b189-428f-a923-ea9b61536e93"); // V_(54) Nut DIN 934 M8 ZN
        public static readonly Guid Washer125_M8_5        = new("4afacf9a-77c4-481f-8514-d2d6c6ee7709"); // V_(5) Washer DIN 125 M8 ZN
        public static readonly Guid Screw7504N_35x13_86   = new("83662d2a-f801-4b01-8789-621c6cbbdb8a"); // V_(86) Screw DIN 7504N 3,5x13 ZN
        public static readonly Guid Screw7504N_42x16_136  = new("f7ecbd4a-c6b0-4b12-b4ba-5d55667b3222"); // V_(136) Screw DIN 7504N 4,2x16 ZN

        /// <summary>
        /// Mapping from the joint material display name (used in BOMSettings dictionaries and Excel headers)
        /// to the shared parameter GUID. Used by ParameterWriterService.
        /// </summary>
        public static readonly Dictionary<string, Guid> NameToGuid = new(StringComparer.OrdinalIgnoreCase)
        {
            // Containment per-support
            ["(137) Bolt DIN 7985 M6x20 ZN"]           = Bolt_M6x20_137,
            ["(119) Threaded plate M6 ZN"]              = ThreadedPlate_119,
            ["(100) Clamp for screw M6"]                = ClampForScrew_100,
            ["(23) Washer DIN 9021 M6 ZN"]              = Washer9021_M6_23,
            ["(21) Bolt DIN 7985 M6x25 ZN"]             = Bolt_M6x25_21,
            ["(33) Nut DIN 934 M6 ZN"]                  = Nut934_M6_33,
            ["(66) Bolt DIN 933 M6x12 8.8 ZN"]          = Bolt933_M6x12_66,
            ["(101) Rail nut M6 18X18"]                  = RailNut_101,
            ["(4) Washer DIN 125 M6 ZN"]                = Washer125_M6_4,
            // Containment per-connection
            ["(99) Clamping element GKS34FT"]            = GKS34FT_99,
            ["(124) Clamping element GSV34FT"]           = GSV34FT_124,
            ["(N/A) Straight connector"]                 = StraightConnector,
            ["(N/A) Truss-head bolt with nut 6x12"]     = TrussHeadBolt6x12,
            ["(N/A) Joint plate"]                        = JointPlate,
            ["Truss-head bolt with flange nut 6x16"]    = TrussHeadFlangeNut6x16,
            ["(N/A) Fibre Runner Coupler"]               = FibreRunnerCoupler,
            // Fixture materials
            ["(85) ISO 7380 MF M6x20 10.9 ZN"]          = ISO7380_M6x20_85,
            ["(132) SIDE HOLDER FOR CABLE GLAND 25mm"]   = SideHolder_132,
            ["(133) LOCKNUT 25mm FOR FLEXIBLE CONDUIT GLAND"] = Locknut_133,
            ["(134) MALE FIXED FITTING + RING 25mm"]     = MaleFixedFitting_134,
            ["(139) RUBBER END GZ16"]                    = RubberEnd_139,
            ["(38) JF2-2-5.5X25-V16ZN"]                 = JF2_V16ZN_38,
            ["(38) JF2-2-5.5x25-V16"]                   = JF2_V16_38,
            ["(92) MOUNTING PLATE FOR MESH CABLE TRAY MPG 90 FT"] = MountingPlate_92,
            // JB mesh tray materials (uppercase variants)
            ["(21) BOLT DIN 7985 M6X25 ZN"]              = Bolt_M6x25_21,
            ["(4) WASHER DIN 125 M6 ZN"]                 = Washer125_M6_4,
            ["(33) NUT DIN 934 M6 ZN"]                   = Nut934_M6_33,
            ["(23) DIN9021-M6-ZN"]                       = Washer9021_M6_23,
            ["(38) JF2-2-5.5x25-V16 (conduit)"]         = JF2_V16_38,
            // Metadata
            ["V_Count"]                                   = Count,
            ["V_Count_Connections"]                        = CountConnections,
            ["V_Mounting_Type"]                            = MountingType,
        };
    }
}
