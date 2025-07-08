using System.Linq;
using System.Windows;
using CmlLib.Core.Version;
using CmlLib.Core.VersionMetadata;

namespace AMMCLauncher
{
    public partial class AddInstanceWindow : Window
    {
        private readonly MVersionCollection _versions;
        public MVersionMetadata? SelectedVersion { get; private set; }
        public string InstanceName => InstanceNameTextBox.Text;

        public AddInstanceWindow(MVersionCollection versions)
        {
            InitializeComponent();
            _versions = versions;

            var versionNames = versions.Select(v => v.Name).ToList();
            VersionComboBox.ItemsSource = versionNames;

            if (versions.LatestReleaseVersion != null)
            {
                VersionComboBox.SelectedItem = versions.LatestReleaseVersion.Name;
            }
        }

        private void CreateButton_Click(object sender, RoutedEventArgs e)
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

            this.DialogResult = true;
        }
    }
}