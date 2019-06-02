using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace VideoMonitor
{
    class HandBrakeRunner
    {
        private static readonly NLog.ILogger _logger = NLog.LogManager.GetLogger(nameof(HandBrakeRunner));
        private static readonly string HandbrakePath;

        static HandBrakeRunner()
        {
            HandbrakePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Handbrake", "handbrakecli.exe");
        }

        public static string GetNewFileName(string sourceFile)
        {
            var folder = Path.GetDirectoryName(sourceFile);
            var file = Path.GetFileName(sourceFile).Replace('_', ' ');
            return Path.Combine(folder, Path.ChangeExtension(file, ".mp4"));
        }

        public static bool Run(string presetFile, string sourceFile)
        {
            var sb = new StringBuilder();
            sb.AppendFormat("--preset-import-file \"{0}\" ", presetFile);
            sb.Append("-f av_mp4 ");
            sb.AppendFormat("-i \"{0}\" ", sourceFile);
            sb.AppendFormat("-o \"{0}\" ", GetNewFileName(sourceFile));

            _logger.Info("Launching handbrake with parameters {0}", sb.ToString());

            var startInfo = new ProcessStartInfo
            {
                FileName = HandbrakePath,
                Arguments = sb.ToString(),
                WorkingDirectory = Path.GetDirectoryName(AppDomain.CurrentDomain.BaseDirectory),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using (var p = Process.Start(startInfo))
            {
                var output = new StringBuilder();
                var error = new StringBuilder();

                p.OutputDataReceived += (sender, eventArgs) => output.AppendLine(eventArgs.Data);
                p.ErrorDataReceived += (sender, eventArgs) => error.AppendLine(eventArgs.Data);

                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();

                p.WaitForExit();

                _logger.Info("Finished processing {0}, with status {1}", sourceFile, p.ExitCode);
                if(p.ExitCode != 0)
                    _logger.Warn(error);

                return p.ExitCode == 0;
            }
        }
    }
}
