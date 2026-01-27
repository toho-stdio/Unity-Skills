using UnityEngine;
using UnityEditor;
using UnityEngine.Profiling;

namespace UnitySkills
{
    /// <summary>
    /// Profiler skills - FPS, memory, stats.
    /// </summary>
    public static class ProfilerSkills
    {
        [UnitySkill("profiler_get_stats", "Get performance statistics (FPS, Memory, Batches)")]
        public static object ProfilerGetStats()
        {
            // Note: UnityStats is only accurate in the Game View when it is visible
            
            long totalAllocatedMemory = Profiler.GetTotalAllocatedMemoryLong();
            long totalReservedMemory = Profiler.GetTotalReservedMemoryLong();
            long totalUnusedReservedMemory = Profiler.GetTotalUnusedReservedMemoryLong();
            
            // Calculate FPS from frameTime (frameTime is in ms, so we divide 1000 by it)
            float frameTime = UnityStats.frameTime;
            float fps = frameTime > 0 ? 1000f / frameTime : 0f;
            
            // Count visible skinned meshes manually
            int visibleSkinnedMeshes = 0;
            var skinnedMeshRenderers = Object.FindObjectsOfType<SkinnedMeshRenderer>();
            foreach (var smr in skinnedMeshRenderers)
            {
                if (smr.isVisible)
                    visibleSkinnedMeshes++;
            }
            
            // Count visible animators
            int visibleAnimators = 0;
            var animators = Object.FindObjectsOfType<Animator>();
            foreach (var anim in animators)
            {
                var renderer = anim.GetComponent<Renderer>();
                if (renderer != null && renderer.isVisible)
                    visibleAnimators++;
            }
            
            return new
            {
                fps,
                frameTime,
                renderTime = UnityStats.renderTime,
                triangles = UnityStats.triangles,
                vertices = UnityStats.vertices,
                batches = UnityStats.batches,
                setPassCalls = UnityStats.setPassCalls,
                drawCalls = UnityStats.drawCalls,
                dynamicBatchedDrawCalls = UnityStats.dynamicBatchedDrawCalls,
                staticBatchedDrawCalls = UnityStats.staticBatchedDrawCalls,
                instancedBatchedDrawCalls = UnityStats.instancedBatchedDrawCalls,
                visibleSkinnedMeshes,
                visibleAnimators,
                memory = new
                {
                    totalAllocatedMB = totalAllocatedMemory / (1024f * 1024f),
                    totalReservedMB = totalReservedMemory / (1024f * 1024f),
                    unusedReservedMB = totalUnusedReservedMemory / (1024f * 1024f),
                    monoHeapMB = Profiler.GetMonoHeapSizeLong() / (1024f * 1024f),
                    monoUsedMB = Profiler.GetMonoUsedSizeLong() / (1024f * 1024f)
                }
            };
        }
    }
}

// Internal helper for TextureUtil if needed, but for simplicity let's stick to safe Profiler API.
// Actually, UnityEditor.UnityStats provides render stats.
// UnityEngine.Profiling.Profiler provides memory stats.
