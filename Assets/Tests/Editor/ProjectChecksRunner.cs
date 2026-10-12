using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace WavelineBase.Tests
{
    /// <summary>
    /// Tools > WavelineBase > Run Checks: runs every EditMode test in the open Editor (no restart,
    /// no batch mode) and writes the results to &lt;project&gt;/Logs/TestResults/:
    ///   summary.txt   - pass/fail counts and each failure's message (short, easy to read or share)
    ///   results.xml   - full NUnit results
    /// The same files are produced by Tools/run-checks.bat for runs with the Editor closed.
    /// </summary>
    public static class ProjectChecksRunner
    {
        private static string OutputFolder =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Logs", "TestResults"));

        [MenuItem("Tools/WavelineBase/Run Checks (EditMode tests)", priority = 0)]
        public static void RunChecks()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Debug.LogWarning("[Checks] Stop Play mode before running the checks.");
                return;
            }

            var api = ScriptableObject.CreateInstance<TestRunnerApi>();
            api.RegisterCallbacks(new Callbacks(api));
            Debug.Log("[Checks] Running EditMode checks...");
            api.Execute(new ExecutionSettings(new Filter { testMode = TestMode.EditMode }));
        }

        [MenuItem("Tools/WavelineBase/Open Check Results", priority = 1)]
        public static void OpenResults() => OpenFile(Path.Combine(OutputFolder, "summary.txt"));

        [MenuItem("Tools/WavelineBase/Open Latest Runtime Log", priority = 2)]
        public static void OpenLatestRuntimeLog()
        {
            var folder = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Logs", "Runtime"));
            var latest = Path.Combine(folder, "LATEST.txt");
            if (!File.Exists(latest))
            {
                Debug.LogWarning("[Checks] No runtime logs yet - enter Play mode first.");
                return;
            }
            OpenFile(Path.Combine(folder, File.ReadAllText(latest).Trim()));
        }

        private static void OpenFile(string path)
        {
            if (File.Exists(path)) EditorUtility.OpenWithDefaultApp(path);
            else Debug.LogWarning($"[Checks] {path} doesn't exist yet.");
        }

        private sealed class Callbacks : ICallbacks
        {
            private readonly TestRunnerApi _api;
            public Callbacks(TestRunnerApi api) => _api = api;

            public void RunStarted(ITestAdaptor testsToRun) { }
            public void TestStarted(ITestAdaptor test) { }
            public void TestFinished(ITestResultAdaptor result) { }

            public void RunFinished(ITestResultAdaptor result)
            {
                _api.UnregisterCallbacks(this);
                try
                {
                    Directory.CreateDirectory(OutputFolder);
                    TestRunnerApi.SaveResultToFile(result, Path.Combine(OutputFolder, "results.xml"));
                    var summary = BuildSummary(result);
                    File.WriteAllText(Path.Combine(OutputFolder, "summary.txt"), summary, new UTF8Encoding(false));

                    if (result.FailCount > 0) Debug.LogError(summary);
                    else Debug.Log(summary);
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
            }
        }

        private static string BuildSummary(ITestResultAdaptor result)
        {
            var failures = new List<ITestResultAdaptor>();
            Collect(result, failures);

            var text = new StringBuilder();
            text.AppendLine($"WavelineBase checks - {DateTime.Now:yyyy-MM-dd HH:mm:ss} - EditMode (in Editor)");
            text.AppendLine($"[Checks] {(result.FailCount == 0 ? "ALL PASSED" : "FAILURES")}: " +
                            $"{result.PassCount} passed, {result.FailCount} failed, {result.SkipCount} skipped " +
                            $"({result.Duration:0.0}s)");
            foreach (var failure in failures)
            {
                text.AppendLine($"FAILED: {failure.FullName}");
                foreach (var line in (failure.Message ?? "").Split('\n').Take(6))
                    text.AppendLine("    " + line.TrimEnd());
                var where = (failure.StackTrace ?? "").Split('\n').FirstOrDefault(l => l.Contains("Assets/"));
                if (!string.IsNullOrEmpty(where)) text.AppendLine("    at " + where.Trim());
            }
            return text.ToString();
        }

        private static void Collect(ITestResultAdaptor node, List<ITestResultAdaptor> failures)
        {
            if (!node.HasChildren)
            {
                if (node.TestStatus == TestStatus.Failed) failures.Add(node);
                return;
            }
            foreach (var child in node.Children) Collect(child, failures);
        }
    }
}
