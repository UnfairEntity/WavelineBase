using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Core.Diagnostics
{
    /// <summary>
    /// Writes every Unity log message of a play session to its own file, so problems can be read
    /// (by you or a tool) without copying text out of the Console.
    ///
    /// Where: Editor play sessions -> &lt;project&gt;/Logs/Runtime/; builds -> &lt;persistentDataPath&gt;/Logs/.
    /// Override with the command-line argument  -logCaptureDir "&lt;folder&gt;"  (the automated checks
    /// use this to collect logs from builds into the project). Each file is named
    /// &lt;yyyyMMdd-HHmmss&gt;_&lt;editor|player&gt;_&lt;processId&gt;.log; the newest 20 are kept.
    /// A summary line (errors / warnings / exceptions counted) is appended when the session ends,
    /// and LATEST.txt in the same folder always names the newest file.
    /// </summary>
    public static class LogCapture
    {
        private const int FilesToKeep = 20;
        private static readonly object Gate = new();
        private static StreamWriter _writer;
        private static int _errors, _warnings, _exceptions;

        /// <summary>Full path of the current session's log file (null if capture failed).</summary>
        public static string CurrentFile { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Begin()
        {
            End(); // in case Domain Reload is off and a previous session's writer is still open

            try
            {
                var folder = ResolveFolder();
                Directory.CreateDirectory(folder);
                var role = (Application.isEditor ? "editor" : "player") + "-" + InstanceIdentity.Label;
                var name = $"{DateTime.Now:yyyyMMdd-HHmmss}_{role}_{Process.GetCurrentProcess().Id}.log";
                CurrentFile = Path.Combine(folder, name);

                _writer = new StreamWriter(new FileStream(CurrentFile, FileMode.Create, FileAccess.Write, FileShare.ReadWrite),
                                           new UTF8Encoding(false)) { AutoFlush = true };
                _errors = _warnings = _exceptions = 0;
                _mainThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId; // Begin runs on the main thread

                _writer.WriteLine($"# {Application.productName} {Application.version} | Unity {Application.unityVersion} | " +
                                  $"{(Application.isEditor ? "Editor" : "Player")} | {SystemInfo.operatingSystem}");
                _writer.WriteLine($"# Started {DateTime.Now:yyyy-MM-dd HH:mm:ss} | args: {string.Join(" ", Environment.GetCommandLineArgs().Skip(1))}");

                File.WriteAllText(Path.Combine(folder, "LATEST.txt"), name);
                Prune(folder);

                Application.logMessageReceivedThreaded += OnLog;
                Application.quitting += End;
            }
            catch (Exception e)
            {
                CurrentFile = null;
                Debug.LogWarning($"[LogCapture] Couldn't start log capture: {e.Message}");
            }
        }

        private static string ResolveFolder()
        {
            var fromArgs = InstanceIdentity.CommandLineValue("-logCaptureDir");
            if (!string.IsNullOrEmpty(fromArgs)) return fromArgs;

            // EditorProjectRoot is the real project folder even inside a Multiplayer Play Mode
            // virtual player, so all instances' logs end up side by side.
            return Application.isEditor
                ? Path.Combine(InstanceIdentity.EditorProjectRoot, "Logs", "Runtime")
                : Path.Combine(Application.persistentDataPath, "Logs");
        }

        private static void OnLog(string message, string stackTrace, LogType type)
        {
            lock (Gate)
            {
                if (_writer == null) return;

                switch (type)
                {
                    case LogType.Error:
                    case LogType.Assert: _errors++; break;
                    case LogType.Exception: _exceptions++; break;
                    case LogType.Warning: _warnings++; break;
                }

                var tag = type switch
                {
                    LogType.Error => "ERROR",
                    LogType.Assert => "ASSERT",
                    LogType.Exception => "EXCEPTION",
                    LogType.Warning => "WARN",
                    _ => "INFO"
                };

                _writer.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [f{FrameCount()}] {tag}: {message}");
                // Stack traces only for problems, to keep the file readable.
                if ((type is LogType.Error or LogType.Exception or LogType.Assert) && !string.IsNullOrEmpty(stackTrace))
                    _writer.WriteLine("    " + stackTrace.TrimEnd().Replace("\n", "\n    "));
            }
        }

        // Time.frameCount may only be read on the main thread; log callbacks can come from others.
        private static int _mainThreadId = -1;
        private static string FrameCount()
        {
            return System.Threading.Thread.CurrentThread.ManagedThreadId == _mainThreadId ? Time.frameCount.ToString() : "-";
        }

        private static void End()
        {
            Application.logMessageReceivedThreaded -= OnLog;
            Application.quitting -= End;
            lock (Gate)
            {
                if (_writer == null) return;
                _writer.WriteLine($"# Ended {DateTime.Now:yyyy-MM-dd HH:mm:ss} | errors: {_errors} | exceptions: {_exceptions} | warnings: {_warnings}");
                _writer.Dispose();
                _writer = null;
            }
        }

        private static void Prune(string folder)
        {
            IEnumerable<FileInfo> old = new DirectoryInfo(folder).GetFiles("*.log")
                .OrderByDescending(f => f.CreationTimeUtc)
                .Skip(FilesToKeep);
            foreach (var file in old)
            {
                try { file.Delete(); }
                catch (IOException) { /* in use by another instance - leave it */ }
            }
        }
    }
}
