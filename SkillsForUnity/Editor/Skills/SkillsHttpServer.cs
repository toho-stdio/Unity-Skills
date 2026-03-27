using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace UnitySkills
{
    /// <summary>
    /// File-based transport service for UnitySkills.
    ///
    /// The public API intentionally keeps the old "server" naming to minimize churn in the
    /// rest of the package, but all transport now flows through a command/result queue on disk.
    /// </summary>
    [InitializeOnLoad]
    public static class SkillsHttpServer
    {
        private static volatile bool _isRunning;
        private static bool _updateHooked;
        private static readonly Queue<FileJob> _jobQueue = new Queue<FileJob>();
        private static readonly HashSet<string> _claimedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly JsonSerializerSettings _jsonSettings = new JsonSerializerSettings
        {
            StringEscapeHandling = StringEscapeHandling.Default
        };

        private static long _totalRequestsProcessed;
        private static long _totalRequestsReceived;
        private static double _lastHeartbeatTime;
        private static int _requestsThisSecond;
        private static long _lastRateLimitResetTicks;
        private static bool _domainReloadPending;

        private const int MaxRequestsPerSecond = 100;
        private const double HeartbeatInterval = 10.0;
        private const int MaxBodySizeBytes = 10 * 1024 * 1024;

        private static readonly string GlobalConfigDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".unity_skills");

        private static string InstanceRoot => Path.Combine(GlobalConfigDir, "instances", RegistryService.InstanceId);
        public static string QueueRoot => InstanceRoot;
        public static string PendingDirectory => Path.Combine(InstanceRoot, "Pending");
        public static string ProcessingDirectory => Path.Combine(InstanceRoot, "Processing");
        public static string ResultsDirectory => Path.Combine(InstanceRoot, "Results");

        private static string PrefKey(string key) => $"UnitySkills_{RegistryService.InstanceId}_{key}";
        private static string PREF_SERVER_SHOULD_RUN => PrefKey("TransportShouldRun");
        private static string PREF_AUTO_START => PrefKey("AutoStart");
        private static string PREF_TOTAL_PROCESSED => PrefKey("TotalProcessed");
        private const string PrefKeyRequestTimeout = "UnitySkills_RequestTimeoutMinutes";

        public static bool IsRunning => _isRunning;
        public static string Url => QueueRoot;
        public static int Port => 0;
        public static int PreferredPort { get => 0; set { } }
        public static int QueuedRequests => _jobQueue.Count;
        public static long TotalProcessed => _totalRequestsProcessed;

        public static bool AutoStart
        {
            get => EditorPrefs.GetBool(PREF_AUTO_START, true);
            set => EditorPrefs.SetBool(PREF_AUTO_START, value);
        }

        public static int RequestTimeoutMinutes
        {
            get => Mathf.Max(1, EditorPrefs.GetInt(PrefKeyRequestTimeout, 60));
            set => EditorPrefs.SetInt(PrefKeyRequestTimeout, Mathf.Max(1, value));
        }

        private class FileJob
        {
            public string FilePath;
            public string ProcessingPath;
            public string RequestId;
            public string AgentId;
            public string CommandType;
            public string SkillName;
            public JObject Envelope;
            public DateTime CreatedAtUtc;
        }

        static SkillsHttpServer()
        {
            EditorApplication.quitting += OnEditorQuitting;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            AssemblyReloadEvents.afterAssemblyReload += OnAfterAssemblyReload;
            CompilationPipeline.compilationStarted += OnCompilationStarted;

            HookUpdateLoop();
            EditorApplication.delayCall += CheckAndRestoreServer;
        }

        public static void ResetStatistics()
        {
            _totalRequestsProcessed = 0;
            EditorPrefs.SetString(PREF_TOTAL_PROCESSED, "0");
        }

        public static void Start(int preferredPort = 0, bool fallbackToAuto = false)
        {
            if (_isRunning)
            {
                SkillsLogger.LogVerbose($"Transport already running at {QueueRoot}");
                return;
            }

            try
            {
                EnsureDirectories();
                HookUpdateLoop();

                _isRunning = true;
                EditorPrefs.SetBool(PREF_SERVER_SHOULD_RUN, true);
                RegistryService.Register(QueueRoot, PendingDirectory, ResultsDirectory, RequestTimeoutMinutes);

                SkillRouter.GetManifest();
                SkillsLogger.Log($"File transport started at {QueueRoot}");
                SkillsLogger.Log($"{SkillRouter.SkillCount} skills loaded | Instance: {RegistryService.InstanceId}");
                SkillsLogger.LogVerbose($"Domain Reload Recovery: ENABLED (AutoStart={AutoStart})");
            }
            catch (Exception ex)
            {
                _isRunning = false;
                EditorPrefs.SetBool(PREF_SERVER_SHOULD_RUN, false);
                SkillsLogger.LogError($"Failed to start file transport: {ex.Message}");
            }
        }

        public static void Stop(bool permanent = false)
        {
            if (!_isRunning) return;

            _isRunning = false;
            if (permanent)
            {
                EditorPrefs.SetBool(PREF_SERVER_SHOULD_RUN, false);
            }

            RegistryService.Unregister();
            _jobQueue.Clear();
            _claimedFiles.Clear();

            if (permanent)
            {
                SkillsLogger.Log("File transport stopped");
            }
            else
            {
                SkillsLogger.LogVerbose("File transport stopped (will auto-restart after reload)");
            }
        }

        public static void StopPermanent()
        {
            Stop(permanent: true);
        }

        private static void OnCompilationStarted(object context)
        {
            if (_isRunning)
            {
                SkillsLogger.LogVerbose("Compilation started - preparing file transport for Domain Reload...");
            }
        }

        private static void OnBeforeAssemblyReload()
        {
            _domainReloadPending = true;
            EditorPrefs.SetBool(PREF_SERVER_SHOULD_RUN, _isRunning);
            EditorPrefs.SetString(PREF_TOTAL_PROCESSED, _totalRequestsProcessed.ToString());

            if (_isRunning)
            {
                RegistryService.Unregister();
                _isRunning = false;
            }
        }

        private static void OnAfterAssemblyReload()
        {
            _domainReloadPending = false;
            var savedTotal = EditorPrefs.GetString(PREF_TOTAL_PROCESSED, "0");
            if (long.TryParse(savedTotal, out var parsed))
            {
                _totalRequestsProcessed = parsed;
            }
        }

        private static void OnEditorQuitting()
        {
            EditorPrefs.SetBool(PREF_SERVER_SHOULD_RUN, false);
            Stop(permanent: true);
        }

        private static void CheckAndRestoreServer()
        {
            var shouldRun = EditorPrefs.GetBool(PREF_SERVER_SHOULD_RUN, false);
            if (shouldRun && AutoStart && !_isRunning)
            {
                SkillsLogger.Log("Auto-restoring file transport after Domain Reload...");
                Start();
            }
        }

        private static void HookUpdateLoop()
        {
            if (_updateHooked) return;
            EditorApplication.update += ProcessCommandQueue;
            _updateHooked = true;
        }

        private static void EnsureDirectories()
        {
            Directory.CreateDirectory(GlobalConfigDir);
            Directory.CreateDirectory(InstanceRoot);
            Directory.CreateDirectory(PendingDirectory);
            Directory.CreateDirectory(ProcessingDirectory);
            Directory.CreateDirectory(ResultsDirectory);
        }

        private static void ProcessCommandQueue()
        {
            if (!_isRunning)
            {
                return;
            }

            EnsureDirectories();
            EnqueuePendingFiles();

            var processed = 0;
            const int maxPerFrame = 20;
            while (processed < maxPerFrame && _jobQueue.Count > 0)
            {
                var job = _jobQueue.Dequeue();
                try
                {
                    ProcessJob(job);
                }
                catch (Exception ex)
                {
                    WriteResult(job, new JObject
                    {
                        ["status"] = "error",
                        ["error"] = ex.Message,
                        ["type"] = ex.GetType().Name
                    });
                    SkillsLogger.LogWarning($"File transport job error: {ex.Message}");
                }
                finally
                {
                    _claimedFiles.Remove(job.FilePath);
                    CleanupProcessingFile(job);
                    _totalRequestsProcessed++;
                    GameObjectFinder.InvalidateCache();
                }

                processed++;
            }

            var now = EditorApplication.timeSinceStartup;
            if (now - _lastHeartbeatTime > HeartbeatInterval)
            {
                _lastHeartbeatTime = now;
                RegistryService.Heartbeat(QueueRoot, PendingDirectory, ResultsDirectory, RequestTimeoutMinutes);
            }
        }

        private static void EnqueuePendingFiles()
        {
            var pendingFiles = Directory.GetFiles(PendingDirectory, "*.json")
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);

            foreach (var file in pendingFiles)
            {
                if (_claimedFiles.Contains(file))
                {
                    continue;
                }

                _claimedFiles.Add(file);
                _totalRequestsReceived++;
                _jobQueue.Enqueue(new FileJob
                {
                    FilePath = file
                });
            }
        }

        private static void ProcessJob(FileJob job)
        {
            if (!CheckRateLimit())
            {
                WriteResult(job, new JObject
                {
                    ["status"] = "error",
                    ["error"] = "Rate limit exceeded",
                    ["limit"] = MaxRequestsPerSecond,
                    ["suggestion"] = "Please slow down requests"
                });
                return;
            }

            job.ProcessingPath = ClaimCommandFile(job.FilePath);
            if (job.ProcessingPath == null)
            {
                return;
            }

            var json = File.ReadAllText(job.ProcessingPath);
            if (json.Length > MaxBodySizeBytes)
            {
                WriteResult(job, new JObject
                {
                    ["status"] = "error",
                    ["error"] = "Request body too large",
                    ["maxSizeBytes"] = MaxBodySizeBytes,
                    ["receivedBytes"] = json.Length
                });
                return;
            }

            JObject envelope;
            try
            {
                envelope = JObject.Parse(json);
            }
            catch (Exception ex)
            {
                WriteResult(job, new JObject
                {
                    ["status"] = "error",
                    ["error"] = $"Invalid JSON command: {ex.Message}"
                });
                return;
            }

            job.Envelope = envelope;
            job.RequestId = envelope.Value<string>("requestId") ?? Path.GetFileNameWithoutExtension(job.FilePath);
            job.AgentId = envelope.Value<string>("agentId") ?? "Unknown";
            job.CommandType = (envelope.Value<string>("command") ?? "skill").ToLowerInvariant();
            job.SkillName = envelope.Value<string>("skill");
            job.CreatedAtUtc = envelope.Value<DateTime?>("createdAtUtc") ?? DateTime.UtcNow;

            switch (job.CommandType)
            {
                case "health":
                    WriteResult(job, BuildHealthPayload());
                    break;
                case "manifest":
                    WriteResult(job, JObject.Parse(SkillRouter.GetManifest()));
                    break;
                case "skill":
                    ExecuteSkill(job);
                    break;
                default:
                    WriteResult(job, new JObject
                    {
                        ["status"] = "error",
                        ["error"] = $"Unsupported command type '{job.CommandType}'"
                    });
                    break;
            }
        }

        private static void ExecuteSkill(FileJob job)
        {
            if (string.IsNullOrEmpty(job.SkillName) ||
                job.SkillName.Contains("/") ||
                job.SkillName.Contains("\\") ||
                job.SkillName.Contains(".."))
            {
                WriteResult(job, new JObject
                {
                    ["status"] = "error",
                    ["error"] = "Invalid skill name"
                });
                return;
            }

            var argsToken = job.Envelope["args"];
            string argsJson;
            if (argsToken == null)
            {
                argsJson = "{}";
            }
            else if (argsToken.Type == JTokenType.Object)
            {
                argsJson = argsToken.ToString(Formatting.None);
            }
            else
            {
                WriteResult(job, new JObject
                {
                    ["status"] = "error",
                    ["error"] = "Command args must be a JSON object"
                });
                return;
            }

            try
            {
                var response = SkillRouter.Execute(job.SkillName, argsJson);
                SkillsLogger.LogAgent(job.AgentId, job.SkillName);
                WriteResult(job, JObject.Parse(response));
            }
            catch (Exception ex)
            {
                WriteResult(job, new JObject
                {
                    ["status"] = "error",
                    ["error"] = ex.Message,
                    ["type"] = ex.GetType().Name,
                    ["skill"] = job.SkillName
                });
            }
        }

        private static JObject BuildHealthPayload()
        {
            return new JObject
            {
                ["status"] = "ok",
                ["service"] = "UnitySkills",
                ["transport"] = "file",
                ["version"] = SkillsLogger.Version,
                ["unityVersion"] = Application.unityVersion,
                ["instanceId"] = RegistryService.InstanceId,
                ["projectName"] = RegistryService.ProjectName,
                ["projectPath"] = RegistryService.ProjectPath,
                ["queueRoot"] = QueueRoot,
                ["pendingDirectory"] = PendingDirectory,
                ["resultsDirectory"] = ResultsDirectory,
                ["transportRunning"] = _isRunning,
                ["queuedRequests"] = QueuedRequests,
                ["totalProcessed"] = _totalRequestsProcessed,
                ["autoRestart"] = AutoStart,
                ["requestTimeoutMinutes"] = RequestTimeoutMinutes,
                ["domainReloadRecovery"] = "enabled",
                ["architecture"] = "File Queue",
                ["note"] = "Commands stay on disk while Unity is busy and will execute when the editor becomes responsive."
            };
        }

        private static string ClaimCommandFile(string originalPath)
        {
            try
            {
                var processingPath = Path.Combine(ProcessingDirectory, Path.GetFileName(originalPath));
                if (File.Exists(processingPath))
                {
                    File.Delete(processingPath);
                }
                File.Move(originalPath, processingPath);
                return processingPath;
            }
            catch (IOException)
            {
                return null;
            }
        }

        private static void WriteResult(FileJob job, JObject payload)
        {
            EnsureDirectories();

            var result = new JObject
            {
                ["requestId"] = job.RequestId ?? Path.GetFileNameWithoutExtension(job.FilePath),
                ["agentId"] = job.AgentId ?? "Unknown",
                ["command"] = job.CommandType ?? "skill",
                ["skill"] = job.SkillName,
                ["createdAtUtc"] = job.CreatedAtUtc,
                ["completedAtUtc"] = DateTime.UtcNow,
                ["payload"] = payload
            };

            var resultPath = Path.Combine(
                ResultsDirectory,
                $"{(job.RequestId ?? Path.GetFileNameWithoutExtension(job.FilePath))}.json");

            File.WriteAllText(resultPath, result.ToString(Formatting.Indented));
        }

        private static void CleanupProcessingFile(FileJob job)
        {
            try
            {
                if (!string.IsNullOrEmpty(job.ProcessingPath) && File.Exists(job.ProcessingPath))
                {
                    File.Delete(job.ProcessingPath);
                }
            }
            catch
            {
                // Best effort cleanup only.
            }
        }

        private static bool CheckRateLimit()
        {
            long nowTicks = DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond;
            if (nowTicks != _lastRateLimitResetTicks)
            {
                _lastRateLimitResetTicks = nowTicks;
                _requestsThisSecond = 0;
            }

            _requestsThisSecond++;
            return _requestsThisSecond <= MaxRequestsPerSecond;
        }
    }
}
