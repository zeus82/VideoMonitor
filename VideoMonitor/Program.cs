using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;

namespace VideoMonitor
{
    class Program
    {
        private static readonly NLog.ILogger _logger = NLog.LogManager.GetLogger(nameof(Program));

        static readonly List<FileSystemWatcher> _watchers = new List<FileSystemWatcher>();
        static readonly BlockingCollection<string> _fileQueue = new BlockingCollection<string>();
        static string _preset;
        static readonly List<string> _roots = new List<string>();
        static readonly List<string> _exclusions = new List<string>();
        static readonly List<string> _fileInFlight = new List<string>();

        static void Main(string[] args)
        {
            try
            {
                _logger.Info("Starting up");

                var config = new ConfigurationBuilder()
                   .AddJsonFile("appsettings.json", false, false)
                   .AddEnvironmentVariables()
                   .Build();

                _logger.Info("Staring monitor thread");
                Task.Factory.StartNew(() => ProcessNewFiles());

                var timer = TimeSpan.Parse(config.GetSection("OldFileCheckInterval").Value);

                _logger.Info("Scanning for old files every {0}", timer);

                var aTimer = new System.Timers.Timer(timer.TotalMilliseconds);
                // Hook up the Elapsed event for the timer. 
                aTimer.Elapsed += OnTimedEvent;
                aTimer.AutoReset = true;
                aTimer.Enabled = true;

                _roots.AddRange(config.GetSection("FoldersToScan").GetChildren().Select(a => a.Value));
                _exclusions.AddRange(config.GetSection("Exclusions").GetChildren().Select(a => a.Value));
                _preset = config.GetSection("presetFile").Value;

                OnTimedEvent(null, null);

                foreach (var r in _roots)
                {
                    _logger.Info("Monitoring {0}", r);
                    var watcher = new FileSystemWatcher(r);
                    watcher.IncludeSubdirectories = true;
                    watcher.NotifyFilter =
                        NotifyFilters.LastAccess |
                        NotifyFilters.LastWrite |
                        NotifyFilters.FileName |
                        NotifyFilters.DirectoryName |
                        NotifyFilters.CreationTime;

                    watcher.Filter = "*.*";

                    watcher.Created += OnFileCreated;

                    // Begin watching.
                    watcher.EnableRaisingEvents = true;
                    _watchers.Add(watcher);
                }

                while (true)
                    Thread.Sleep(int.MaxValue);
            }
            catch(Exception ex)
            {
                _logger.Fatal(ex, "Fatal Error");
                throw;
            }
        }

        private static void OnFileCreated(object sender, FileSystemEventArgs e)
        {
            if(Path.GetExtension(e.FullPath).Equals(".mkv", StringComparison.OrdinalIgnoreCase))
            {
                var match = _exclusions.FirstOrDefault(a => e.FullPath.Contains(a, StringComparison.OrdinalIgnoreCase));


                if (string.IsNullOrWhiteSpace(match))
                {
                    var fi = new FileInfo(e.FullPath);
                    while (true)
                    {
                        if (!IsFileLocked(fi))
                        {
                            _logger.Debug("Queuing {0}", e.FullPath);
                            QueueFile(e.FullPath);
                            break;
                        }
                        Thread.Sleep(1000);
                    }
                }
                else
                    _logger.Debug("Skipping file {0} because it matches exclusion \"{1}\"", e.FullPath, match);
            }
        }

        static void ProcessNewFiles()
        {
            while (true)
            {
                var file = _fileQueue.Take();
                _logger.Debug("Dequeuing {0}. {1} Remaining items", file, _fileQueue.Count);
                if (HandBrakeRunner.Run(_preset, file))
                {
                    _logger.Info("Successfully converted {0}, deleting original", file);

                    try
                    {
                        File.Delete(file);
                    }
                    catch
                    {
                        _logger.Info("Failed to delete {0}", file);
                    }
                }
                else
                {
                    _logger.Info("Failed to converted {0}", file);
                }

                _fileInFlight.Remove(file);
            }
        }

        static bool IsFileLocked(FileInfo file)
        {
            try
            {
                using (var stream = file.Open(FileMode.Open, FileAccess.Read, FileShare.None))
                {
                };
            }
            catch (IOException)
            {
                return true;
            }

            //file is not locked
            return false;
        }

        private static void OnTimedEvent(object source, ElapsedEventArgs e)
        {

            var mkvs = new List<FileInfo>();
            foreach(var r in _roots)
                mkvs.AddRange(Directory.EnumerateFiles(r, "*.mkv", SearchOption.AllDirectories).Select(f => new FileInfo(f)));

            foreach (var f in mkvs.Where(a => !_fileInFlight.Contains(a.FullName)))
            {
                var match = _exclusions.FirstOrDefault(a => f.FullName.Contains(a, StringComparison.OrdinalIgnoreCase));

                if (string.IsNullOrEmpty(match))
                {
                    if (File.Exists(HandBrakeRunner.GetNewFileName(f.FullName)))
                    {
                        _logger.Info("Found mkv and associated mp4.  Deleting mkv {0}", f.FullName);
                        try
                        {
                            if (!IsFileLocked(f))
                                File.Delete(f.FullName);
                            else
                                _logger.Info("{0} is in use", f.FullName);
                        }
                        catch
                        {
                            _logger.Info("Failed to delete {0}", f);
                        }
                    }
                    else
                    {
                        if (f.CreationTimeUtc > DateTime.UtcNow - TimeSpan.FromDays(14) && f.CreationTimeUtc < DateTime.UtcNow - TimeSpan.FromDays(1))
                        {
                            if (!IsFileLocked(f))
                            {
                                _logger.Info("Found mkv without associated mp4.  Queuing mkv {0}", f.FullName);
                                QueueFile(f.FullName);
                            }
                        }
                        else
                            _logger.Info("Found old mkv without associated mp4. {0}", f.FullName);

                    }
                }
                else
                    _logger.Debug("Skipping file {0} because it matches exclusion \"{1}\"", f.FullName, match);
            }
        }

        private static void QueueFile(string f)
        {
            _fileInFlight.Add(f);
            _fileQueue.Add(f);
            if(_fileQueue.Count > 0)
                _logger.Debug(string.Join(Environment.NewLine, _fileQueue.Select(a => "\t" + a).ToArray()));
        }
    }
}
