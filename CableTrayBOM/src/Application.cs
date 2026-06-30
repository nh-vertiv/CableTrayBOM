using System;
using System.Reflection;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;

namespace CableTrayBOM
{
    public class Application : IExternalApplication
    {
        public Result OnStartup(UIControlledApplication application)
        {
            try
            {
                string assemblyPath = Assembly.GetExecutingAssembly().Location;
                string tabName = "Vertiv BOM";
                application.CreateRibbonTab(tabName);

                var mainPanel = application.CreateRibbonPanel(tabName, "Procurement Schedule");

                mainPanel.AddItem(new PushButtonData(
                    "CableTrayBOM", "Generate\nBOM", assemblyPath,
                    "CableTrayBOM.Commands.GenerateBOMCommand")
                {
                    ToolTip = "Open the BOM Generator window to scan model, update counts, and export schedules.",
                    LargeImage = GetIcon("CableTrayBOM.resources.icon_bom_32.png"),
                    Image = GetIcon("CableTrayBOM.resources.icon_bom_16.png")
                });

                mainPanel.AddItem(new PushButtonData(
                    "UpdateCounts", "Update\nParameters", assemblyPath,
                    "CableTrayBOM.Commands.UpdateCountsCommand")
                {
                    ToolTip = "Scan model and write V_Count, V_Count_Connections, and joint material\n" +
                              "values to all cable tray, ladder, fitting, and fixture elements.",
                    LargeImage = GetIcon("CableTrayBOM.resources.icon_bom_32.png"),
                    Image = GetIcon("CableTrayBOM.resources.icon_bom_16.png")
                });

                mainPanel.AddItem(new PushButtonData(
                    "QuickSlice", "Quick\nSlice", assemblyPath,
                    "CableTrayBOM.Commands.QuickSliceCommand")
                {
                    ToolTip = "Select cable trays or groups, then cut into standard pieces.\nFor groups: select the group from outside group edit mode.",
                    LargeImage = GetIcon("CableTrayBOM.resources.icon_slice_32.png"),
                    Image = GetIcon("CableTrayBOM.resources.icon_slice_16.png")
                });

                var settingsPanel = application.CreateRibbonPanel(tabName, "Settings");

                settingsPanel.AddItem(new PushButtonData(
                    "BOMSettings", "Joint Material\nSettings", assemblyPath,
                    "CableTrayBOM.Commands.SettingsCommand")
                {
                    ToolTip = "Configure joint material quantities per support, per connection, and per fixture type.",
                    LargeImage = GetIcon("CableTrayBOM.resources.icon_settings_32.png"),
                    Image = GetIcon("CableTrayBOM.resources.icon_settings_16.png")
                });

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("CableTrayBOM Error", $"Failed to initialize:\n{ex.Message}");
                return Result.Failed;
            }
        }

        public Result OnShutdown(UIControlledApplication application) => Result.Succeeded;

        private static BitmapImage? GetIcon(string name)
        {
            try
            {
                using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
                if (s == null) return null;
                var img = new BitmapImage();
                img.BeginInit(); img.StreamSource = s;
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.EndInit(); img.Freeze();
                return img;
            }
            catch { return null; }
        }
    }
}
