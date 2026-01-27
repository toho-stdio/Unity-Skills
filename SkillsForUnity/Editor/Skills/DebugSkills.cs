using UnityEngine;
using UnityEditor;
using UnityEditor.Compilation;
using System.Linq;
using System.Collections.Generic;

namespace UnitySkills
{
    /// <summary>
    /// Debug skills - self-healing, active error checking, compilation control.
    /// </summary>
    public static class DebugSkills
    {
        [UnitySkill("debug_get_errors", "Get only active errors and exceptions from the console logs.")]
        public static object DebugGetErrors(int limit = 50) => DebugGetLogs("Error", null, limit);

        [UnitySkill("debug_get_logs", "Get console logs filtered by type (Error/Warning/Log) and content.")]
        public static object DebugGetLogs(string type = "Error", string filter = null, int limit = 50)
        {
            // We can't access internal log entries easily via public API without reflection,
            // but we can piggyback on ConsoleSkills if active, or use LogEntries reflection again.
            // Let's use reflection to get current active logs from the actual Console Window backend.
            
            var assembly = System.Reflection.Assembly.GetAssembly(typeof(SceneView));
            var logEntriesType = assembly.GetType("UnityEditor.LogEntries");
            var getCountMethod = logEntriesType.GetMethod("GetCount");
            var getEntryMethod = logEntriesType.GetMethod("GetEntryInternal");
            var startMethod = logEntriesType.GetMethod("StartGettingEntries");
            var endMethod = logEntriesType.GetMethod("EndGettingEntries");
            
            startMethod.Invoke(null, null);
            int count = (int)getCountMethod.Invoke(null, null);
            var results = new List<object>();
            
            // Create an instance of LogEntry (internal struct/class)
            var logEntryType = assembly.GetType("UnityEditor.LogEntry");
            var logEntryInstance = System.Activator.CreateInstance(logEntryType);
            var modeField = logEntryType.GetField("mode");
            var messageField = logEntryType.GetField("message");
            var fileField = logEntryType.GetField("file");
            var lineField = logEntryType.GetField("line");
            
            // Mask mapping
            // LogEntryMode: Error = 1, Assert = 2, Log = 4, Fatal = 16, Warning = 256, AssetImportError = ..., AssetImportWarning = ...
            // We'll simplify: 
            int errorMask = 1 | 2 | 16 | 32 | 64 | 128; // Error, Assert, Fatal...
            int warningMask = 256 | 512 | 1024; // Warning...
            int logMask = 4; // Log
            
            int targetMask = 0;
            if (type.Contains("Error")) targetMask |= errorMask;
            if (type.Contains("Warning")) targetMask |= warningMask;
            if (type.Contains("Log")) targetMask |= logMask;
            if (targetMask == 0) targetMask = errorMask; // Default to error
            
            // Iterate backwards
            int found = 0;
            for (int i = count - 1; i >= 0 && found < limit; i--)
            {
                getEntryMethod.Invoke(null, new object[] { i, logEntryInstance });
                
                int mode = (int)modeField.GetValue(logEntryInstance);
                
                if ((mode & targetMask) == 0) continue;
                
                string msg = (string)messageField.GetValue(logEntryInstance);
                if (!string.IsNullOrEmpty(filter) && !msg.Contains(filter)) continue;
                
                string file = (string)fileField.GetValue(logEntryInstance);
                int line = (int)lineField.GetValue(logEntryInstance);
                
                results.Add(new 
                {
                    type = (mode & errorMask) != 0 ? "Error" : (mode & warningMask) != 0 ? "Warning" : "Log",
                    message = msg.Length > 500 ? msg.Substring(0, 500) + "..." : msg, 
                    file,
                    line
                });
                found++;
            }
            
            endMethod.Invoke(null, null);
            
            return new
            {
                count = results.Count,
                logs = results
            };
        }

        [UnitySkill("debug_check_compilation", "Check if Unity is currently compiling scripts.")]
        public static object DebugCheckCompilation()
        {
            return new
            {
                isCompiling = EditorApplication.isCompiling,
                isUpdating = EditorApplication.isUpdating
            };
        }

        [UnitySkill("debug_force_recompile", "Force script recompilation.")]
        public static object DebugForceRecompile()
        {
            // 1. Refresh AssetDatabase
            AssetDatabase.Refresh();
            
            // 2. Request Script Compilation (Target specific or all)
            CompilationPipeline.RequestScriptCompilation();
            
            return new { success = true, message = "Compilation requested" };
        }
        
        [UnitySkill("debug_get_system_info", "Get Editor and System capabilities.")]
        public static object DebugGetSystemInfo()
        {
            return new
            {
                unityVersion = Application.unityVersion,
                platform = Application.platform.ToString(),
                deviceModel = SystemInfo.deviceModel,
                processorType = SystemInfo.processorType,
                systemMemorySize = SystemInfo.systemMemorySize,
                graphicsDeviceName = SystemInfo.graphicsDeviceName,
                graphicsMemorySize = SystemInfo.graphicsMemorySize,
                editorSkin = EditorGUIUtility.isProSkin ? "Dark" : "Light"
            };
        }
    }
}
