// MainWindow.xaml.cs
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using CmlLib.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.Auth.Microsoft;
using CmlLib.Core.Downloader;
using CmlLib.Core.Installer.Forge;
using CmlLib.Core.Installer.Forge.Installers;
using CmlLib.Core.Installer.Forge.Versions;
using CmlLib.Core.Java;
using CmlLib.Core.Version;
using Newtonsoft.Json;

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
                var launcher = new CMLauncher(new MinecraftPath());
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
                    catch (Exception ex) { Debug.WriteLine($"Failed to load instance from {dirPath}: {ex.Message}"); }
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
                    IsReady = false,
                    ModloaderType = addInstanceDialog.SelectedModloader
                };

                if (newInstance.ModloaderType == Modloader.Forge)
                {
                    newInstance.ForgeVersion = addInstanceDialog.SelectedForgeVersion;
                }

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
            instance.Progress = 0;

            try
            {
                var path = new MinecraftPath(instance.Path);
                var launcher = new CMLauncher(path);

                launcher.FileChanged += e =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (e.TotalFileCount > 0)
                            instance.Progress = (int)((double)e.ProgressedFileCount / e.TotalFileCount * 100);
                    });
                };

                // Download base Minecraft version first
                var baseVersionMeta = await launcher.GetVersionAsync(instance.VersionId);
                await launcher.CheckAndDownloadAsync(baseVersionMeta);

                if (instance.ModloaderType == Modloader.Forge)
                {
                    string forgeVersion = instance.ForgeVersion;
                    string mcVersion = instance.VersionId;

                    string installerFileName = $"forge-{mcVersion}-{forgeVersion}-installer.jar";
                    string installerUrl = $"https://maven.minecraftforge.net/net/minecraftforge/forge/{mcVersion}-{forgeVersion}/{installerFileName}";
                    string installerPath = Path.Combine(instance.Path, installerFileName);

                    Debug.WriteLine($"Downloading Forge installer from: {installerUrl}");
                    using (var httpClient = new HttpClient())
                    using (var response = await httpClient.GetAsync(installerUrl))
                    {
                        response.EnsureSuccessStatusCode();
                        var installerBytes = await response.Content.ReadAsByteArrayAsync();
                        await File.WriteAllBytesAsync(installerPath, installerBytes);
                    }

                    var resolver = new MinecraftJavaPathResolver(path);
                    var javaDir = resolver.GetJavaDirPath(baseVersionMeta.JavaVersion!);
                    string javaExe = Path.Combine(javaDir, "bin", "java.exe");

                    if (string.IsNullOrEmpty(javaExe) || !File.Exists(javaExe))
                        throw new Exception("Java runtime not found.");

                    var process = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = javaExe,
                            Arguments = $"-jar \"{installerPath}\" --installClient", // Removed --installPath argument
                            WorkingDirectory = instance.Path,
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            CreateNoWindow = true
                        }
                    };

                    process.OutputDataReceived += (s, e) => Debug.WriteLine("[Forge] " + e.Data);
                    process.ErrorDataReceived += (s, e) => Debug.WriteLine("[Forge Error] " + e.Data);

                    Debug.WriteLine("Starting Forge installer...");
                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    process.WaitForExit();
                    Debug.WriteLine("Forge installer finished.");

                    // Detect the installed Forge version folder in global Minecraft versions folder
                    string installedVersionFolder = DetectForgeInstalledVersionFolder(instance.Path, mcVersion);
                    Debug.WriteLine($"Detected Forge version folder: {installedVersionFolder}");

                    // Copy Forge version folder to instance versions folder
                    string versionsDir = Path.Combine(instance.Path, "versions");
                    if (!Directory.Exists(versionsDir))
                        Directory.CreateDirectory(versionsDir);

                    string targetPath = Path.Combine(versionsDir, Path.GetFileName(installedVersionFolder));
                    CopyDirectory(installedVersionFolder, targetPath);

                    // Update instance.VersionId to the Forge version folder name
                    instance.VersionId = Path.GetFileName(installedVersionFolder);
                }

                // Download full Forge (or vanilla) version files after Forge installation
                var finalMeta = await launcher.GetVersionAsync(instance.VersionId);
                await launcher.CheckAndDownloadAsync(finalMeta);

                instance.IsReady = true;

                var metaFile = Path.Combine(instance.Path, "instance.json");
                File.WriteAllText(metaFile, JsonConvert.SerializeObject(instance, Formatting.Indented));
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Failed to download {instance.Name}: {ex.Message}",
                    "Download Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                instance.IsReady = false;
            }
            finally
            {
                instance.IsDownloading = false;
                instance.Progress = 0;
            }
        }

        private string DetectForgeInstalledVersionFolder(string instancePath, string mcVersion)
        {
            var globalVersionsDir = Path.Combine(new MinecraftPath().BasePath, "versions");

            if (!Directory.Exists(globalVersionsDir))
                throw new Exception("Global versions folder does not exist.");

            var found = Directory.GetDirectories(globalVersionsDir)
                .FirstOrDefault(d => Path.GetFileName(d).Contains("forge") && Path.GetFileName(d).Contains(mcVersion));

            if (found == null)
                throw new Exception("Forge version folder was not found in global versions.");

            return found;
        }

        private void CopyDirectory(string sourceDir, string targetDir)
        {
            Directory.CreateDirectory(targetDir);

            foreach (var file in Directory.GetFiles(sourceDir))
            {
                string targetFilePath = Path.Combine(targetDir, Path.GetFileName(file));
                File.Copy(file, targetFilePath, overwrite: true);
            }

            foreach (var directory in Directory.GetDirectories(sourceDir))
            {
                string targetSubDir = Path.Combine(targetDir, Path.GetFileName(directory));
                CopyDirectory(directory, targetSubDir);
            }
        }

        private async Task LaunchInstance(GameInstance instance)
        {
            try
            {
                var path = new MinecraftPath(instance.Path);
                var launcher = new CMLauncher(path);

                var versionMeta = await launcher.GetVersionAsync(instance.VersionId);

                var resolver = new MinecraftJavaPathResolver(path);
                var javaDir = resolver.GetJavaDirPath(versionMeta.JavaVersion!);

                string javaExe;
                if (!string.IsNullOrEmpty(javaDir) && Directory.Exists(javaDir))
                {
                    javaExe = Path.Combine(javaDir, "bin", "java.exe");
                }
                else
                {
                    javaExe = resolver.GetJavaDirPath(versionMeta.JavaVersion!)!;
                }

                if (string.IsNullOrEmpty(javaExe) || !File.Exists(javaExe))
                {
                    MessageBox.Show(
                        "Could not find a Java runtime for this instance.\n" +
                        "Please install Java (or bundle a JRE under your instance folder) and try again.",
                        "Java Not Found",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    return;
                }

                var opts = new MLaunchOption
                {
                    Session = session ?? MSession.CreateOfflineSession("Player"),
                    JavaPath = javaExe
                };
                await launcher.LaunchAsync(instance.VersionId, opts);
            }
            catch (System.Exception ex)
            {
                MessageBox.Show(
                    $"Failed to launch {instance.Name}: {ex.Message}",
                    "Launch Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
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

            public Modloader ModloaderType { get; set; } = Modloader.Vanilla;

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
            public string StatusText => IsDownloading ? "Downloading..." : (IsReady ? "Ready to Play" : "Download Required");

            [JsonIgnore]
            public bool IsForge => ModloaderType == Modloader.Forge;

            public string ForgeVersion { get; set; } = "";
        }
    }
}
