#if REVIT2024
using System;

namespace CableTrayBOM
{
    /// <summary>
    /// Polyfills for APIs that exist in .NET 8 (Revit 2025 target) but are missing
    /// from .NET Framework 4.8 (Revit 2024 target). Compiled into the net48 build only,
    /// so the rest of the codebase can use the modern overloads unconditionally.
    /// </summary>
    internal static class Net48Polyfills
    {
        /// <summary>
        /// .NET Framework 4.8 lacks string.Contains(string, StringComparison).
        /// This supplies it with identical semantics.
        /// </summary>
        public static bool Contains(this string source, string value, StringComparison comparisonType)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (value == null) throw new ArgumentNullException(nameof(value));
            return source.IndexOf(value, comparisonType) >= 0;
        }
    }
}
#endif
