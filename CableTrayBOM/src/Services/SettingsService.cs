using System;
using System.IO;
using Newtonsoft.Json;
using CableTrayBOM.Models;

namespace CableTrayBOM.Services
{
    /// <summary>
    /// Handles saving/loading BOM settings to/from a JSON file.
    /// Settings are stored per-user in %AppData%\CableTrayBOM\settings.json
    /// </summary>
    public static class SettingsService
    {
        private static readonly string SettingsFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CableTrayBOM");

        private static readonly string SettingsFile = Path.Combine(SettingsFolder, "settings.json");

        public static BOMSettings Load()
        {
            try
            {
                if (File.Exists(SettingsFile))
                {
                    string json = File.ReadAllText(SettingsFile);
                    var settings = JsonConvert.DeserializeObject<BOMSettings>(json);
                    if (settings != null)
                    {
                        // Sanity check: fix corrupted values from previous versions
                        if (settings.DefaultSliceLength < 100 || settings.DefaultSliceLength > 50000)
                            settings.DefaultSliceLength = 3000;
                        if (settings.CouplingGap < 0.1 || settings.CouplingGap > 100)
                            settings.CouplingGap = 1.0;
                        if (settings.GSV34FTPercentage < 0 || settings.GSV34FTPercentage > 100)
                            settings.GSV34FTPercentage = 15;
                        return settings;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading settings: {ex.Message}");
            }
            return new BOMSettings();
        }

        public static void Save(BOMSettings settings)
        {
            try
            {
                if (!Directory.Exists(SettingsFolder))
                    Directory.CreateDirectory(SettingsFolder);

                string json = JsonConvert.SerializeObject(settings, Formatting.Indented);
                File.WriteAllText(SettingsFile, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error saving settings: {ex.Message}");
            }
        }

        public static void ResetToDefaults()
        {
            Save(new BOMSettings());
        }
    }
}
