using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using CableTrayBOM.Models;

namespace CableTrayBOM.UI
{
    public partial class SettingsWindow : Window
    {
        public BOMSettings Settings { get; private set; }

        public SettingsWindow(BOMSettings settings)
        {
            InitializeComponent();
            Settings = CloneSettings(settings);
            LoadSettingsToGrid();
        }

        private void LoadSettingsToGrid()
        {
            dgMeshTraySupport.ItemsSource = ToObservable(Settings.MeshTrayJointPerSupport);
            dgMeshTrayConnection.ItemsSource = ToObservable(Settings.MeshTrayJointPerConnection);
            dgMeshConsoleSupport.ItemsSource = ToObservable(Settings.MeshTrayConsoleJointPerSupport);
            dgMeshConsoleConnection.ItemsSource = ToObservable(Settings.MeshTrayConsoleJointPerConnection);
            dgLadderSupport.ItemsSource = ToObservable(Settings.LadderJointPerSupport);
            dgLadderConnection.ItemsSource = ToObservable(Settings.LadderJointPerConnection);
            dgPerfSupport.ItemsSource = ToObservable(Settings.PerforatedTrayJointPerSupport);
            dgPerfConnection.ItemsSource = ToObservable(Settings.PerforatedTrayJointPerConnection);
            dgNonPerfSupport.ItemsSource = ToObservable(Settings.NonPerforatedTrayJointPerSupport);
            dgNonPerfConnection.ItemsSource = ToObservable(Settings.NonPerforatedTrayJointPerConnection);
            dgFiberSupport.ItemsSource = ToObservable(Settings.FiberTrayJointPerSupport);
            dgFiberConnection.ItemsSource = ToObservable(Settings.FiberTrayJointPerConnection);
            dgLightingChannel.ItemsSource = ToObservable(Settings.LightingChannelJoint);
            dgJBMeshTray.ItemsSource = ToObservable(Settings.JunctionBoxMeshTrayJoint);
            dgJBPanel.ItemsSource = ToObservable(Settings.JunctionBoxPanelJoint);
            txtConduitLength.Text = Settings.FlexibleConduitLengthPerFixtureMm.ToString("F0");
        }

        private void SaveSettingsFromGrid()
        {
            Settings.MeshTrayJointPerSupport = FromObservable(dgMeshTraySupport.ItemsSource);
            Settings.MeshTrayJointPerConnection = FromObservable(dgMeshTrayConnection.ItemsSource);
            Settings.MeshTrayConsoleJointPerSupport = FromObservable(dgMeshConsoleSupport.ItemsSource);
            Settings.MeshTrayConsoleJointPerConnection = FromObservable(dgMeshConsoleConnection.ItemsSource);
            Settings.LadderJointPerSupport = FromObservable(dgLadderSupport.ItemsSource);
            Settings.LadderJointPerConnection = FromObservable(dgLadderConnection.ItemsSource);
            Settings.PerforatedTrayJointPerSupport = FromObservable(dgPerfSupport.ItemsSource);
            Settings.PerforatedTrayJointPerConnection = FromObservable(dgPerfConnection.ItemsSource);
            Settings.NonPerforatedTrayJointPerSupport = FromObservable(dgNonPerfSupport.ItemsSource);
            Settings.NonPerforatedTrayJointPerConnection = FromObservable(dgNonPerfConnection.ItemsSource);
            Settings.FiberTrayJointPerSupport = FromObservable(dgFiberSupport.ItemsSource);
            Settings.FiberTrayJointPerConnection = FromObservable(dgFiberConnection.ItemsSource);
            Settings.LightingChannelJoint = FromObservable(dgLightingChannel.ItemsSource);
            Settings.JunctionBoxMeshTrayJoint = FromObservable(dgJBMeshTray.ItemsSource);
            Settings.JunctionBoxPanelJoint = FromObservable(dgJBPanel.ItemsSource);

            if (double.TryParse(txtConduitLength.Text, out double condLen))
                Settings.FlexibleConduitLengthPerFixtureMm = condLen;
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            SaveSettingsFromGrid();
            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void BtnReset_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show("Reset all joint material quantities to factory defaults?",
                "Reset Settings", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                Settings = new BOMSettings();
                LoadSettingsToGrid();
            }
        }

        // ── Helpers ──

        private static ObservableCollection<KeyValuePair<string, int>> ToObservable(Dictionary<string, int> dict)
        {
            return new ObservableCollection<KeyValuePair<string, int>>(dict.ToList());
        }

        private static Dictionary<string, int> FromObservable(object? itemsSource)
        {
            if (itemsSource is ObservableCollection<KeyValuePair<string, int>> collection)
            {
                var dict = new Dictionary<string, int>();
                foreach (var kvp in collection)
                {
                    if (!string.IsNullOrWhiteSpace(kvp.Key))
                        dict[kvp.Key] = kvp.Value;
                }
                return dict;
            }
            return new Dictionary<string, int>();
        }

        private static BOMSettings CloneSettings(BOMSettings original)
        {
            var json = Newtonsoft.Json.JsonConvert.SerializeObject(original);
            return Newtonsoft.Json.JsonConvert.DeserializeObject<BOMSettings>(json) ?? new BOMSettings();
        }
    }
}
