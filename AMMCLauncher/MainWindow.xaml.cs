using System;
using System.IO;
using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using CmlLib.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.Auth.Microsoft;
using CmlLib.Core.Version;
using CmlLib.Core.Downloader;
using Newtonsoft.Json;
using System.Collections.Generic;

namespace AMMCLauncher
{
    public partial class MainWindow : Window
    {
        private MSession? session;
        private readonly JELoginHandler authenticator;
        public ObservableCollection<GameInstance> Instances { get; set; }
        private MVersionCollection? gameVersions;

        public MainWindow()
        {
            InitializeComponent();
            this.authenticator = new JELoginHandlerBuilder().Build();
            Instances = new ObservableCollection<GameInstance>();
            InstanceList.ItemsSource = Instances;
            this.Loaded += MainWindow_Loaded;

            LoadInstances();
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try { session = await authenticator.AuthenticateSilently(); }
            catch { session = null; }
            UpdateLoginUI();

            try
            {
                var path = new MinecraftPath();
                var launcher = new CMLauncher(path);
                gameVersions = await launcher.GetAllVersionsAsync();
            }
            catch (Exception ex) { MessageBox.Show("Failed to load Minecraft versions: " + ex.Message); }
        }

        private async void SignInButton_Click(object sender, RoutedEventArgs e)
        {
            if (session == null)
            {
                try { session = await authenticator.Authenticate(); }
                catch (TaskCanceledException) { MessageBox.Show("Login Canceled"); session = null; }
                catch (Exception ex) { MessageBox.Show($"Login Failed: {ex.Message}"); session = null; }
            }
            else
            {
                try { await authenticator.Signout(); session = null; MessageBox.Show("Signed Out!"); }
                catch (Exception ex) { MessageBox.Show($"Sign Out Failed: {ex.Message}"); }
            }
            UpdateLoginUI();
        }

        private void UpdateLoginUI()
        {
            if (session?.CheckIsValid() == true)
            {
                SignInButton.Content = "Sign Out";
                UsernameLabel.Text = session.Username;
            }
            else
            {
                SignInButton.Content = "Sign In";
                UsernameLabel.Text = "";
            }
        }

        private void LoadInstances()
        {
            string instancesDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "instances");
            if (!Directory.Exists(instancesDirectory))
            {
                Directory.CreateDirectory(instancesDirectory);
                return;
            }

            Instances.Clear();
            foreach (var dirPath in Directory.GetDirectories(instancesDirectory))
            {
                string instanceMetaFile = Path.Combine(dirPath, "instance.json");
                if (File.Exists(instanceMetaFile))
                {
                    try
                    {
                        string json = File.ReadAllText(instanceMetaFile);
                        var instance = JsonConvert.DeserializeObject<GameInstance>(json);
                        if (instance != null)
                        {
                            instance.Path = dirPath;
                            var minecraftPath = new MinecraftPath(instance.Path);
                            string versionJarPath = minecraftPath.GetVersionJarPath(instance.VersionId);
                            instance.IsReady = File.Exists(versionJarPath);
                            Instances.Add(instance);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Failed to load instance from {dirPath}: {ex.Message}");
                    }
                }
            }
        }

        private void AddInstanceButton_Click(object sender, RoutedEventArgs e)
        {
            if (gameVersions == null)
            {
                MessageBox.Show("Game versions are not loaded yet. Please wait or restart the launcher.");
                return;
            }

            var addInstanceDialog = new AddInstanceWindow(gameVersions);
            addInstanceDialog.Owner = this;

            if (addInstanceDialog.ShowDialog() == true && addInstanceDialog.SelectedVersion != null)
            {
                string instanceFolderName = addInstanceDialog.InstanceName;
                string instancePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "instances", instanceFolderName);

                if (Directory.Exists(instancePath))
                {
                    MessageBox.Show("An instance with this name already exists!", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                Directory.CreateDirectory(instancePath);

                var newInstance = new GameInstance
                {
                    Name = addInstanceDialog.InstanceName,
                    VersionId = addInstanceDialog.SelectedVersion.Name,
                    Path = instancePath,
                    IsReady = false
                };

                try
                {
                    string instanceMetaFile = Path.Combine(instancePath, "instance.json");
                    string json = JsonConvert.SerializeObject(newInstance, Formatting.Indented);
                    File.WriteAllText(instanceMetaFile, json);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Failed to save instance metadata: " + ex.Message);
                    return;
                }

                Instances.Add(newInstance);
                _ = DownloadInstance(newInstance);
            }
        }

        private async void InstanceButton_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var instance = button?.Tag as GameInstance;
            if (instance == null || instance.IsDownloading) return;

            if (instance.IsReady)
            {
                await LaunchInstance(instance);
            }
            else
            {
                await DownloadInstance(instance);
            }
        }

        private async Task DownloadInstance(GameInstance instance)
        {
            instance.IsDownloading = true;
            try
            {
                var path = new MinecraftPath(instance.Path);
                var launcher = new CMLauncher(path);
                launcher.FileChanged += (e) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (e.TotalFileCount > 0)
                        {
                            instance.Progress = (int)((double)e.ProgressedFileCount / e.TotalFileCount * 100);
                        }
                    });
                };

                var versionToDownload = await launcher.GetVersionAsync(instance.VersionId);
                await launcher.CheckAndDownloadAsync(versionToDownload);
                instance.IsReady = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to download {instance.Name}: {ex.Message}");
                instance.IsReady = false;
            }
            finally
            {
                instance.IsDownloading = false;
                instance.Progress = 0;
            }
        }

        private async Task LaunchInstance(GameInstance instance)
        {
            try
            {
                var path = new MinecraftPath(instance.Path);
                var launcher = new CMLauncher(path);
                var launchOptions = new MLaunchOption
                {
                    Session = this.session ?? MSession.GetOfflineSession("Player"),
                };
                await launcher.LaunchAsync(instance.VersionId, launchOptions);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to launch {instance.Name}: {ex.Message}");
            }
        }
    }

    public class GameInstance : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private string _name = "";
        public string Name { get => _name; set { _name = value; OnPropertyChanged(); } }

        private string _versionId = "";
        public string VersionId { get => _versionId; set { _versionId = value; OnPropertyChanged(); } }

        [JsonIgnore]
        public string Path { get; set; } = "";

        private int _progress;
        [JsonIgnore]
        public int Progress { get => _progress; set { _progress = value; OnPropertyChanged(); OnPropertyChanged(nameof(ProgressText)); } }

        private bool _isDownloading;
        [JsonIgnore]
        public bool IsDownloading
        {
            get => _isDownloading;
            set
            {
                _isDownloading = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusText));
            }
        }

        private bool _isReady;
        [JsonIgnore]
        public bool IsReady { get => _isReady; set { _isReady = value; OnPropertyChanged(); } }

        [JsonIgnore]
        public string ProgressText => IsDownloading ? $"{Progress}%" : "";
        [JsonIgnore]
        public string StatusText => IsDownloading ? "Downloading..." : (IsReady ? "Play" : "Download Required");
    }
}