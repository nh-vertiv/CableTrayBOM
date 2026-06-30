using Autodesk.Revit.DB;

namespace CableTrayBOM
{
    /// <summary>
    /// Abstracts API differences between Revit 2024 (.NET 4.8) and Revit 2025 (.NET 8).
    /// </summary>
    internal static class RevitCompat
    {
        /// <summary>Create ElementId from string (int ctor in 2024, long ctor in 2025).</summary>
        public static ElementId ToElementId(string idStr)
        {
#if REVIT2024
            return new ElementId(int.Parse(idStr));
#else
            return new ElementId(long.Parse(idStr));
#endif
        }

        /// <summary>Parameter group for the Data section (enum in 2024, ForgeTypeId in 2025).</summary>
#if REVIT2024
        public static BuiltInParameterGroup DataGroup => BuiltInParameterGroup.PG_DATA;
#else
        public static ForgeTypeId DataGroup => GroupTypeId.Data;
#endif

        /// <summary>Insert parameter binding with the correct group type for the target API.</summary>
        public static bool InsertBinding(Document doc, Definition def, ElementBinding binding)
        {
            return doc.ParameterBindings.Insert(def, binding, DataGroup);
        }

        /// <summary>ReInsert parameter binding with the correct group type for the target API.</summary>
        public static bool ReInsertBinding(Document doc, Definition def, ElementBinding binding)
        {
            return doc.ParameterBindings.ReInsert(def, binding, DataGroup);
        }
    }
}
