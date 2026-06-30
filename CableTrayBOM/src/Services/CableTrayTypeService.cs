using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using CableTrayBOM;

namespace CableTrayBOM.Services
{
    /// <summary>
    /// Checks and creates/fixes cable tray types and their routing preferences.
    ///
    /// For MISSING types: duplicates template + creates fitting types + wires routing.
    /// For EXISTING types: checks if routing preferences point to correctly named
    /// fitting types, and offers to fix them if they still point to "Standard".
    /// </summary>
    public class CableTrayTypeService
    {
        private readonly Document _doc;

        private static readonly string[] RequiredTypes = {
            "Mesh Cable Tray",
            "Perforated Cable Tray",
            "Non-Perforated Cable Tray"
        };

        private static readonly string[] FittingSlotNames = {
            "Horizontal Bend",
            "Vertical Inside Bend",
            "Vertical Outside Bend",
            "Tee",
            "Cross",
            "Transition"
        };

        public CableTrayTypeService(Document doc) { _doc = doc; }

        /// <summary>
        /// Comprehensive check: type existence + routing preference correctness.
        /// Also detects variant names (e.g. "Wire Mesh Cable Tray" for "Mesh Cable Tray").
        /// </summary>
        public TypeCheckResult CheckTypes()
        {
            var result = new TypeCheckResult();

            var allTypes = new FilteredElementCollector(_doc)
                .OfClass(typeof(CableTrayType))
                .Cast<CableTrayType>().ToList();

            foreach (var reqName in RequiredTypes)
            {
                string keyword = GetKeyword(reqName);

                // Find matching type (exact or contains keyword)
                var match = allTypes.FirstOrDefault(t =>
                    t.Name.Equals(reqName, StringComparison.OrdinalIgnoreCase)) ??
                    allTypes.FirstOrDefault(t =>
                    t.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase) &&
                    t.FamilyName.Contains("Cable Tray", StringComparison.OrdinalIgnoreCase));

                if (match != null)
                {
                    result.Existing.Add($"{match.Name} (matches {reqName})");

                    // Check routing preferences
                    var slots = ReadRoutingPreferences(match);
                    foreach (var slot in slots)
                    {
                        var fitting = _doc.GetElement(slot.FittingTypeId) as FamilySymbol;
                        if (fitting == null) continue;

                        string fittingName = fitting.Name;
                        bool isCorrect = fittingName.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                            || fittingName.Equals(match.Name, StringComparison.OrdinalIgnoreCase);

                        if (isCorrect)
                            result.CorrectRouting.Add($"  {slot.SlotName}: {fitting.FamilyName}: {fittingName}");
                        else
                            result.WrongRouting.Add($"  {slot.SlotName}: {fitting.FamilyName}: {fittingName} (should contain '{keyword}')");
                    }

                    if (slots.Count == 0)
                        result.WrongRouting.Add($"  No routing preferences found on {match.Name}");
                }
                else
                {
                    result.Missing.Add(reqName);
                }
            }

            return result;
        }

        /// <summary>
        /// Create missing types AND fix routing preferences on existing types.
        /// </summary>
        public CreateTypeResult CreateAndFixTypes()
        {
            var result = new CreateTypeResult();

            var allCableTrayTypes = new FilteredElementCollector(_doc)
                .OfClass(typeof(CableTrayType))
                .Cast<CableTrayType>().ToList();

            // Find template
            CableTrayType? templateType = allCableTrayTypes
                .FirstOrDefault(t => t.FamilyName.Contains("Channel") && t.Name == "Standard")
                ?? allCableTrayTypes.FirstOrDefault(t => t.FamilyName.Contains("Channel"))
                ?? allCableTrayTypes.FirstOrDefault();

            if (templateType == null)
            {
                result.Errors.Add("No existing cable tray types found to duplicate from.");
                return result;
            }

            result.TemplateType = $"{templateType.FamilyName}: {templateType.Name}";

            foreach (var reqName in RequiredTypes)
            {
                string keyword = GetKeyword(reqName);

                // Find existing type (exact or keyword match)
                var existingType = allCableTrayTypes
                    .FirstOrDefault(t => t.Name.Equals(reqName, StringComparison.OrdinalIgnoreCase))
                    ?? allCableTrayTypes.FirstOrDefault(t =>
                        t.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase) &&
                        t.FamilyName.Contains("Cable Tray", StringComparison.OrdinalIgnoreCase));

                CableTrayType targetType;
                string targetTypeName;

                if (existingType != null)
                {
                    targetType = existingType;
                    targetTypeName = existingType.Name;
                    result.Skipped.Add($"{targetTypeName} (already exists)");
                }
                else
                {
                    // Create new type
                    try
                    {
                        var newType = templateType.Duplicate(reqName) as CableTrayType;
                        if (newType == null)
                        {
                            result.Errors.Add($"Failed to duplicate type: {reqName}");
                            continue;
                        }
                        targetType = newType;
                        targetTypeName = reqName;
                        result.CreatedTypes.Add(reqName);
                    }
                    catch (Exception ex)
                    {
                        result.Errors.Add($"{reqName}: {ex.Message}");
                        continue;
                    }
                }

                // Fix routing preferences (for both new and existing types)
                FixRoutingPreferences(targetType, targetTypeName, result);
            }

            return result;
        }

        /// <summary>
        /// Check and fix each fitting slot on a cable tray type.
        /// For each slot: if the current fitting doesn't match the tray type name,
        /// create a new fitting type and update the routing preference.
        /// </summary>
        private void FixRoutingPreferences(CableTrayType trayType, string trayTypeName,
            CreateTypeResult result)
        {
            var slots = ReadRoutingPreferences(trayType);
            string keyword = GetKeyword(trayTypeName);

            foreach (var slot in slots)
            {
                try
                {
                    var currentFitting = _doc.GetElement(slot.FittingTypeId) as FamilySymbol;
                    if (currentFitting == null) continue;

                    string familyName = currentFitting.FamilyName;

                    // Skip Union
                    if (familyName.IndexOf("Union", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        result.SkippedFittings.Add($"{slot.SlotName}: {familyName} (Union - skipped)");
                        continue;
                    }

                    // Check if current fitting already has correct name
                    bool isCorrect = currentFitting.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                        || currentFitting.Name.Equals(trayTypeName, StringComparison.OrdinalIgnoreCase);

                    if (isCorrect)
                    {
                        result.SkippedFittings.Add($"{slot.SlotName}: {familyName}: {currentFitting.Name} (correct)");
                        continue;
                    }

                    // Find or create correct fitting type
                    var correctFitting = new FilteredElementCollector(_doc)
                        .OfCategory(BuiltInCategory.OST_CableTrayFitting)
                        .WhereElementIsElementType()
                        .Cast<FamilySymbol>()
                        .FirstOrDefault(f => f.FamilyName == familyName &&
                            (f.Name.Equals(trayTypeName, StringComparison.OrdinalIgnoreCase) ||
                             f.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)));

                    ElementId newFittingId;

                    if (correctFitting != null)
                    {
                        newFittingId = correctFitting.Id;
                        result.SkippedFittings.Add($"{slot.SlotName}: {familyName}: {correctFitting.Name} (found existing)");
                    }
                    else
                    {
                        // Duplicate current fitting with correct name
                        var newFitting = currentFitting.Duplicate(trayTypeName);
                        if (newFitting == null)
                        {
                            result.Errors.Add($"Failed to create fitting: {familyName}: {trayTypeName}");
                            continue;
                        }
                        newFittingId = newFitting.Id;
                        result.CreatedFittings.Add($"{slot.SlotName}: {familyName}: {trayTypeName}");
                    }

                    // Update routing preference
                    bool updated = SetRoutingPreference(trayType, slot.SlotName, newFittingId);
                    if (updated)
                        result.RoutingUpdates.Add(
                            $"{trayTypeName} -> {slot.SlotName} = {familyName}: {trayTypeName} " +
                            $"(was: {currentFitting.Name})");
                    else
                        result.Errors.Add($"Could not update {slot.SlotName} on {trayTypeName}");
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"{slot.SlotName} for {trayTypeName}: {ex.Message}");
                }
            }
        }

        private List<FittingSlot> ReadRoutingPreferences(CableTrayType trayType)
        {
            var slots = new List<FittingSlot>();

            foreach (var slotName in FittingSlotNames)
            {
                ElementId? fittingId = null;

                try
                {
                    var param = trayType.LookupParameter(slotName);
                    if (param != null && param.StorageType == StorageType.ElementId)
                    {
                        var id = param.AsElementId();
                        if (id != ElementId.InvalidElementId) fittingId = id;
                    }
                }
                catch { }

                if (fittingId == null)
                {
                    foreach (Parameter p in trayType.Parameters)
                    {
                        if (p.Definition?.Name == slotName && p.StorageType == StorageType.ElementId)
                        {
                            var id = p.AsElementId();
                            if (id != ElementId.InvalidElementId) { fittingId = id; break; }
                        }
                    }
                }

                if (fittingId == null && slotName == "Horizontal Bend")
                {
                    try
                    {
                        var p = trayType.get_Parameter(BuiltInParameter.RBS_CURVETYPE_DEFAULT_BEND_PARAM);
                        if (p != null && p.StorageType == StorageType.ElementId)
                        {
                            var id = p.AsElementId();
                            if (id != ElementId.InvalidElementId) fittingId = id;
                        }
                    }
                    catch { }
                }

                if (fittingId != null)
                    slots.Add(new FittingSlot { SlotName = slotName, FittingTypeId = fittingId });
            }

            return slots;
        }

        private bool SetRoutingPreference(CableTrayType trayType, string slotName, ElementId fittingTypeId)
        {
            try
            {
                var param = trayType.LookupParameter(slotName);
                if (param != null && !param.IsReadOnly && param.StorageType == StorageType.ElementId)
                { param.Set(fittingTypeId); return true; }
            }
            catch { }

            try
            {
                foreach (Parameter p in trayType.Parameters)
                {
                    if (p.Definition?.Name == slotName && !p.IsReadOnly && p.StorageType == StorageType.ElementId)
                    { p.Set(fittingTypeId); return true; }
                }
            }
            catch { }

            if (slotName == "Horizontal Bend")
            {
                try
                {
                    var p = trayType.get_Parameter(BuiltInParameter.RBS_CURVETYPE_DEFAULT_BEND_PARAM);
                    if (p != null && !p.IsReadOnly) { p.Set(fittingTypeId); return true; }
                }
                catch { }
            }

            return false;
        }

        /// <summary>
        /// Extract the keyword from a type name for matching.
        /// "Mesh Cable Tray" -> "Mesh", "Wire Mesh Cable Tray" -> "Mesh",
        /// "Non-Perforated Cable Tray" -> "Non-Perforated"
        /// </summary>
        private static string GetKeyword(string typeName)
        {
            if (typeName.Contains("Non-Perforated", StringComparison.OrdinalIgnoreCase))
                return "Non-Perforated";
            if (typeName.Contains("Perforated", StringComparison.OrdinalIgnoreCase))
                return "Perforated";
            if (typeName.Contains("Mesh", StringComparison.OrdinalIgnoreCase))
                return "Mesh";
            if (typeName.Contains("Fiber", StringComparison.OrdinalIgnoreCase))
                return "Fiber";
            if (typeName.Contains("Ladder", StringComparison.OrdinalIgnoreCase))
                return "Ladder";
            return typeName.Split(' ')[0];
        }

        private class FittingSlot
        {
            public string SlotName { get; set; } = "";
            public ElementId FittingTypeId { get; set; } = ElementId.InvalidElementId;
        }
    }

    public class TypeCheckResult
    {
        public List<string> Existing { get; set; } = new();
        public List<string> Missing { get; set; } = new();
        public List<string> CorrectRouting { get; set; } = new();
        public List<string> WrongRouting { get; set; } = new();
        public List<string> ExistingFittings { get; set; } = new();
        public List<string> MissingFittings { get; set; } = new();

        public bool HasRoutingIssues => WrongRouting.Count > 0;

        public string Summary
        {
            get
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("CABLE TRAY TYPES:");
                foreach (var e in Existing) sb.AppendLine($"  [OK] {e}");
                foreach (var m in Missing) sb.AppendLine($"  [MISSING] {m}");

                if (CorrectRouting.Count > 0 || WrongRouting.Count > 0)
                {
                    sb.AppendLine("\nROUTING PREFERENCES:");
                    foreach (var c in CorrectRouting) sb.AppendLine($"  [OK]{c}");
                    foreach (var w in WrongRouting) sb.AppendLine($"  [FIX]{w}");
                }

                return sb.ToString();
            }
        }
    }

    public class CreateTypeResult
    {
        public string TemplateType { get; set; } = "";
        public List<string> CreatedTypes { get; set; } = new();
        public List<string> CreatedFittings { get; set; } = new();
        public List<string> RoutingUpdates { get; set; } = new();
        public List<string> Skipped { get; set; } = new();
        public List<string> SkippedFittings { get; set; } = new();
        public List<string> Errors { get; set; } = new();

        public string Summary
        {
            get
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"Template: {TemplateType}\n");

                if (CreatedTypes.Count > 0)
                { sb.AppendLine("CREATED CABLE TRAY TYPES:"); foreach (var t in CreatedTypes) sb.AppendLine($"  [NEW] {t}"); }
                if (CreatedFittings.Count > 0)
                { sb.AppendLine("\nCREATED FITTING TYPES:"); foreach (var f in CreatedFittings) sb.AppendLine($"  [NEW] {f}"); }
                if (RoutingUpdates.Count > 0)
                { sb.AppendLine("\nROUTING PREFERENCES UPDATED:"); foreach (var r in RoutingUpdates) sb.AppendLine($"  [FIX] {r}"); }
                if (Skipped.Count > 0)
                { sb.AppendLine("\nSKIPPED:"); foreach (var s in Skipped) sb.AppendLine($"  [SKIP] {s}"); }
                if (SkippedFittings.Count > 0)
                { sb.AppendLine("\nSKIPPED FITTINGS:"); foreach (var s in SkippedFittings) sb.AppendLine($"  [SKIP] {s}"); }
                if (Errors.Count > 0)
                { sb.AppendLine("\nERRORS:"); foreach (var e in Errors) sb.AppendLine($"  [ERROR] {e}"); }

                int total = CreatedTypes.Count + CreatedFittings.Count;
                sb.AppendLine($"\nTotal: {total} created, {RoutingUpdates.Count} routing fixes");

                return sb.ToString();
            }
        }
    }
}
