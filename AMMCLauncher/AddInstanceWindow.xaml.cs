// AddInstanceWindow.xaml.cs
using CmlLib.Core.Version;
using CmlLib.Core.VersionMetadata;
using CmlLib.Core.Installer.Forge.Versions;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace AMMCLauncher
{
    public enum Modloader
    {
        Vanilla,
        Forge
    }

    public partial class AddInstanceWindow : Window
    {
        private readonly MVersionCollection _versions;
        public MVersionMetadata? SelectedVersion { get; private set; }
        public string InstanceName => InstanceNameTextBox.Text;
        public Modloader SelectedModloader { get; private set; } = Modloader.Vanilla;
        public string SelectedForgeVersion { get; private set; } = "";

        public AddInstanceWindow(MVersionCollection versions)
        {
            InitializeComponent();
            _versions = versions;

            FabricRadioButton.Visibility = Visibility.Collapsed;
            QuiltRadioButton.Visibility = Visibility.Collapsed;

            UpdateVersionFilter();
        }

        private void FilterCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            UpdateVersionFilter();
        }

        private void UpdateVersionFilter()
        {
            bool showSnapshots = SnapshotCheckBox.IsChecked == true;
            bool showOldAlpha = OldAlphaCheckBox.IsChecked == true;
            bool showOldBeta = OldBetaCheckBox.IsChecked == true;

            var filteredVersions = _versions.Where(v =>
            {
                switch (v.Type)
                {
                    case "release":
                        return true;
                    case "snapshot":
                        return showSnapshots;
                    case "old_alpha":
                        return showOldAlpha;
                    case "old_beta":
                        return showOldBeta;
                    default:
                        return false;
                }
            });

            VersionComboBox.ItemsSource = filteredVersions.Select(v => v.Name).ToList();

            if (_versions.LatestReleaseVersion != null && VersionComboBox.Items.Contains(_versions.LatestReleaseVersion.Name))
            {
                VersionComboBox.SelectedItem = _versions.LatestReleaseVersion.Name;
            }
            else if (VersionComboBox.Items.Count > 0)
            {
                VersionComboBox.SelectedIndex = 0;
            }
        }

        private void VersionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateInstanceName();
        }

        private void InstanceNameTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
        }

        private void UpdateInstanceName()
        {
            if (VersionComboBox.SelectedItem != null)
            {
                InstanceNameTextBox.Text = $"My {VersionComboBox.SelectedItem} Instance";
            }
        }

        private async void CreateButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(InstanceNameTextBox.Text))
            {
                MessageBox.Show("Instance name cannot be empty.", "Invalid Name", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string? selectedVersionName = VersionComboBox.SelectedItem as string;
            if (string.IsNullOrEmpty(selectedVersionName))
            {
                MessageBox.Show("You must select a Minecraft version.", "No Version Selected", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SelectedVersion = _versions.FirstOrDefault(v => v.Name == selectedVersionName);

            if (ForgeRadioButton.IsChecked == true)
            {
                SelectedModloader = Modloader.Forge;

                try
                {
                    var versionLoader = new ForgeVersionLoader(new HttpClient());
                    var forgeVersions = await versionLoader.GetForgeVersions(selectedVersionName);

                    var recommended = forgeVersions.FirstOrDefault(v => v.IsRecommendedVersion);
                    if (recommended != null)
                    {
                        SelectedForgeVersion = recommended.ForgeVersionName;
                    }
                    else
                    {
                        SelectedForgeVersion = forgeVersions.FirstOrDefault()?.ForgeVersionName ?? "";
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to get Forge version for {selectedVersionName}: {ex.Message}");
                    return;
                }
            }
            else
            {
                SelectedModloader = Modloader.Vanilla;
                SelectedForgeVersion = "";
            }

            this.DialogResult = true;
        }
    }
}
