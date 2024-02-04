using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Threading;
using System.Xml.Linq;
using NLog;
using WindowsDisplayAPI.DisplayConfig;

namespace DwmLutGUI
{
    internal class MainViewModel : INotifyPropertyChanged
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public event PropertyChangedEventHandler PropertyChanged;

        private string _activeText;
        private MonitorData _selectedMonitor;
        private bool _isActive;
        private Key _toggleKey;

        private readonly string _configPath;

        private bool _configChanged;
        private XElement _lastConfig;
        private XElement _activeConfig;

        public MainViewModel()
        {
            Logger.Debug("Initializing MainViewModel");
            UpdateActiveStatus();
            var dispatcherTimer = new DispatcherTimer();
            dispatcherTimer.Tick += DispatcherTimer_Tick;
            dispatcherTimer.Interval = new TimeSpan(0, 0, 1);
            dispatcherTimer.Start();

            _configPath = AppDomain.CurrentDomain.BaseDirectory + "config.xml";

            _allMonitors = new List<MonitorData>();
            Monitors = new ObservableCollection<MonitorData>();
            UpdateMonitors();

            CanApply = !Injector.NoDebug;
            MonitorData.StaticPropertyChanged += MonitorDataOnStaticPropertyChanged;
        }

        private void MonitorDataOnStaticPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            OnPropertyChanged(nameof(SdrLutPath));
            OnPropertyChanged(nameof(HdrLutPath));
            SaveConfig();
        }

        public string ActiveText
        {
            private set
            {
                if (value == _activeText) return;
                _activeText = value;
                OnPropertyChanged();
            }
            get => _activeText;
        }

        public MonitorData SelectedMonitor
        {
            set
            {
                if (value == _selectedMonitor) return;
                _selectedMonitor = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SdrLutPath));
                OnPropertyChanged(nameof(HdrLutPath));
            }
            get => _selectedMonitor;
        }

        private void UpdateConfigChanged()
        {
            _configChanged = _lastConfig != _activeConfig && !XNode.DeepEquals(_lastConfig, _activeConfig);
        }

        private void SaveConfig()
        {
            var xElem = new XElement("monitors",
                new XAttribute("lut_toggle", _toggleKey),
                _allMonitors.Select(x =>
                    new XElement("monitor", new XAttribute("path", x.DevicePath),
                        x.SdrLutPath != null ? new XAttribute("sdr_lut", x.SdrLutPath) : null,
                        x.HdrLutPath != null ? new XAttribute("hdr_lut", x.HdrLutPath) : null,
                        x.SdrLuts != null
                            ? new XElement("sdr_luts", x.SdrLuts.Select(s => new XElement("sdr_lut", s)))
                            : null)));

            xElem.Save(_configPath);

            _lastConfig = xElem;
            UpdateConfigChanged();
            UpdateActiveStatus();
        }

        public string SdrLutPath
        {
            set
            {
                if (SelectedMonitor == null || SelectedMonitor.SdrLutPath == value) return;
                SelectedMonitor.SdrLutPath = value;
                OnPropertyChanged();

                SaveConfig();
            }
            get => SelectedMonitor?.SdrLutPath;
        }

        public string HdrLutPath
        {
            set
            {
                if (SelectedMonitor == null || SelectedMonitor.HdrLutPath == value) return;
                SelectedMonitor.HdrLutPath = value;
                OnPropertyChanged();

                SaveConfig();
            }
            get => SelectedMonitor?.HdrLutPath;
        }

        public Key ToggleKey
        {
            set
            {
                if (value == _toggleKey) return;
                _toggleKey = value;
                OnPropertyChanged();
                SaveConfig();
            }
            get => _toggleKey;
        }

        public bool IsActive
        {
            set
            {
                if (value == _isActive) return;
                _isActive = value;
                OnPropertyChanged();
            }
            get => _isActive;
        }

        public bool CanApply { get; }

        private readonly List<MonitorData> _allMonitors;
        
        public ObservableCollection<MonitorData> Monitors { get; }

        private void UpdateMonitors()
        {
            Logger.Debug("Updating monitors");
            _allMonitors.Clear();
            Monitors.Clear();

            // Load configuration
            var config = LoadMonitorsFromConfig();

            // Load toggle key
            _toggleKey = GetToggleKeyFromConfig();

            // Load active monitors
            var paths = PathInfo.GetActivePaths();
            foreach (var path in paths)
            {
                if (path.IsCloneMember) continue;
                var monitor = CreateActiveMonitorData(path, config);
                _allMonitors.Add(monitor);
                Monitors.Add(monitor);
            }
            Logger.Debug("Active monitors: {0}", string.Join(", ", Monitors.Select(m => m.ToString())));

            // Load inactive monitors
            foreach (var monitor in config)
            {
                var path = (string)monitor.Attribute("path");
                if (path == null || Monitors.Any(x => x.DevicePath == path)) continue;

                var newMonitorData = CreateMonitorData(monitor);
                _allMonitors.Add(newMonitorData);
            }
            Logger.Debug("All monitors: {0}", string.Join(", ", _allMonitors.Select(m => m.ToString())));

            UpdateSelectedMonitor();
            
            Logger.Debug("Monitors updated");
            return;

            MonitorData CreateActiveMonitorData(PathInfo path, IEnumerable<XElement> xElements)
            {
                var targetInfo = path.TargetsInfo[0];
                var devicePath = targetInfo.DisplayTarget.DevicePath;
                var name = targetInfo.DisplayTarget.FriendlyName;
                if (string.IsNullOrEmpty(name))
                {
                    name = "???";
                }

                var connector = targetInfo.OutputTechnology.ToString();
                if (connector == "DisplayPortExternal")
                {
                    connector = "DisplayPort";
                }

                var position = path.Position.X + "," + path.Position.Y;

                var settings = xElements.FirstOrDefault(x => (string)x.Attribute("path") == devicePath);
                var sdrLutPath = settings != null ? (string)settings.Attribute("sdr_lut") : null;
                var hdrLutPath = settings != null ? (string)settings.Attribute("hdr_lut") : null;

                var monitorData = new MonitorData(devicePath, path.DisplaySource.SourceId + 1, name, connector, position,
                    sdrLutPath, hdrLutPath);
                var sdrLutPaths = settings?.Element("sdr_luts")?.Elements("sdr_lut").Select(x => (string)x).ToList();
                var hdrLutPaths = settings?.Element("hdr_luts")?.Elements("hdr_lut").Select(x => (string)x).ToList();
                if (sdrLutPaths != null) monitorData.SdrLuts = new ObservableCollection<string>(sdrLutPaths);
                if (hdrLutPaths != null) monitorData.HdrLuts = new ObservableCollection<string>(hdrLutPaths);
                return monitorData;
            }

            MonitorData CreateMonitorData(XElement monitor)
            {
                var sdrLutPath = (string)monitor.Attribute("sdr_lut");
                var hdrLutPath = (string)monitor.Attribute("hdr_lut");

                var monitorData = new MonitorData((string)monitor.Attribute("path"), sdrLutPath, hdrLutPath);
                var sdrLutPaths = monitor.Element("sdr_luts")?.Elements("sdr_lut").Select(x => (string)x).ToList();
                var hdrLutPaths = monitor.Element("hdr_luts")?.Elements("hdr_lut").Select(x => (string)x).ToList();
                if (sdrLutPaths != null) monitorData.SdrLuts = new ObservableCollection<string>(sdrLutPaths);
                if (hdrLutPaths != null) monitorData.HdrLuts = new ObservableCollection<string>(hdrLutPaths);
                return monitorData;
            }

            void UpdateSelectedMonitor()
            {
                var selectedPath = SelectedMonitor?.DevicePath;
                if (selectedPath != null)
                {
                    var previous = Monitors.FirstOrDefault(monitor => monitor.DevicePath == selectedPath);
                    if (previous != null)
                    {
                        SelectedMonitor = previous;
                    }
                }
                Logger.Debug("Selected monitor: {0}", SelectedMonitor?.ToString());
            }
        }
        
        private List<XElement> LoadMonitorsFromConfig()
        {
            if (!File.Exists(_configPath))
            {
                Logger.Debug("Configuration file not found: {0}", _configPath);
                return new List<XElement>();
            }

            List<XElement> config;
            try
            {
                config = XElement.Load(_configPath).Descendants("monitor").ToList();
                Logger.Debug("{0} monitors found in configuration", config.Count);
            }
            catch (Exception e)
            {
                Logger.Error(e, "Configuration load failed");
                config = new List<XElement>();
            }

            return config;
        }

        /**
         * Get toggle key configuration from _configPath.
         * Return Key.Pause if _configPath doesn't exist or can't be loaded.
         */
        private Key GetToggleKeyFromConfig()
        {
            if (!File.Exists(_configPath))
            {
                Logger.Debug("Configuration file not found: {0}", _configPath);
                return Key.Pause;
            }

            Key key;
            try
            {
                key = (Key)Enum.Parse(typeof(Key),
                    (string)XElement.Load(_configPath).Attribute("lut_toggle"));
                Logger.Debug("Toggle key from configuration: {0}", key.ToString());
            }
            catch (Exception e)
            {
                Logger.Error(e, "Configuration load failed, using Pause key as default");
                key = Key.Pause;
            }

            return key;
        }

        public void ReInject()
        {
            Injector.Uninject();
            if (!Monitors.All(monitor =>
                    string.IsNullOrEmpty(monitor.SdrLutPath) && string.IsNullOrEmpty(monitor.HdrLutPath)))
            {
                Injector.Inject(Monitors);
            }

            _activeConfig = _lastConfig;
            UpdateConfigChanged();

            UpdateActiveStatus();
        }

        public void Uninject()
        {
            Injector.Uninject();
            UpdateActiveStatus();
        }

        private void UpdateActiveStatus()
        {
            Logger.Debug("Updating active status");
            var status = Injector.GetStatus();
            if (status != null)
            {
                IsActive = (bool)status;
                if (status == true)
                {
                    ActiveText = "Active" + (_configChanged ? " (changed)" : "");
                }
                else
                {
                    ActiveText = "Inactive";
                }
            }
            else
            {
                IsActive = false;
                ActiveText = "???";
            }

            Logger.Debug("Active status updated");
        }

        public void OnDisplaySettingsChanged(object sender, EventArgs e)
        {
            UpdateMonitors();
            if (!_configChanged)
            {
                ReInject();
            }
        }

        private void DispatcherTimer_Tick(object sender, EventArgs e)
        {
            UpdateActiveStatus();
        }

        private void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}