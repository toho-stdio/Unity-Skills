using UnityEngine;
using UnityEditor;
using System;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Collections.Generic;

namespace UnitySkills
{
    /// <summary>
    /// Scene Understanding Skills - Help AI quickly perceive project state.
    /// </summary>
    public static class PerceptionSkills
    {
        private static readonly HashSet<string> UnityCallbacks = new HashSet<string>
        {
            "Awake", "Start", "Update", "FixedUpdate", "LateUpdate",
            "OnEnable", "OnDisable", "OnDestroy",
            "OnCollisionEnter", "OnCollisionExit", "OnCollisionStay",
            "OnCollisionEnter2D", "OnCollisionExit2D", "OnCollisionStay2D",
            "OnTriggerEnter", "OnTriggerExit", "OnTriggerStay",
            "OnTriggerEnter2D", "OnTriggerExit2D", "OnTriggerStay2D",
            "OnMouseDown", "OnMouseUp", "OnMouseEnter", "OnMouseExit",
            "OnGUI", "OnDrawGizmos", "OnValidate",
            "OnBecameVisible", "OnBecameInvisible",
            "OnApplicationPause", "OnApplicationQuit", "OnApplicationFocus",
            "OnAnimatorIK", "OnAnimatorMove",
            "OnParticleCollision", "OnParticleTrigger",
            "OnRenderObject", "OnPreRender", "OnPostRender",
            "OnWillRenderObject", "OnRenderImage"
        };

        [UnitySkill("scene_summarize", "Get a structured summary of the current scene (object counts, component stats, hierarchy depth)")]
        public static object SceneSummarize(bool includeComponentStats = true, int topComponentsLimit = 10)
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            var allObjects = UnityEngine.Object.FindObjectsOfType<GameObject>();
            var rootObjects = scene.GetRootGameObjects();

            int totalObjects = allObjects.Length;
            int activeObjects = 0;
            int maxDepth = 0;
            int lightCount = 0, cameraCount = 0, canvasCount = 0;
            var componentCounts = new Dictionary<string, int>();

            foreach (var go in allObjects)
            {
                if (go.activeInHierarchy) activeObjects++;

                // Calculate depth
                int depth = 0;
                var t = go.transform;
                while (t.parent != null) { depth++; t = t.parent; }
                if (depth > maxDepth) maxDepth = depth;

                // Count components in single pass
                foreach (var comp in go.GetComponents<Component>())
                {
                    if (comp == null) continue;
                    var typeName = comp.GetType().Name;

                    // Count key types inline
                    if (comp is Light) lightCount++;
                    else if (comp is Camera) cameraCount++;
                    else if (comp is Canvas) canvasCount++;

                    if (includeComponentStats)
                    {
                        if (!componentCounts.ContainsKey(typeName))
                            componentCounts[typeName] = 0;
                        componentCounts[typeName]++;
                    }
                }
            }

            componentCounts.Remove("Transform");
            var topComponents = componentCounts
                .OrderByDescending(kv => kv.Value)
                .Take(topComponentsLimit)
                .Select(kv => new { component = kv.Key, count = kv.Value })
                .ToList();

            return new
            {
                success = true,
                sceneName = scene.name,
                scenePath = scene.path,
                isDirty = scene.isDirty,
                stats = new
                {
                    totalObjects,
                    activeObjects,
                    inactiveObjects = totalObjects - activeObjects,
                    rootObjects = rootObjects.Length,
                    maxHierarchyDepth = maxDepth,
                    lights = lightCount,
                    cameras = cameraCount,
                    canvases = canvasCount
                },
                topComponents
            };
        }

        [UnitySkill("hierarchy_describe", "Get a text tree of the scene hierarchy (like 'tree' command)")]
        public static object HierarchyDescribe(int maxDepth = 5, bool includeInactive = false, int maxItemsPerLevel = 20)
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            var rootObjects = scene.GetRootGameObjects()
                .Where(g => includeInactive || g.activeInHierarchy)
                .OrderBy(g => g.transform.GetSiblingIndex())
                .Take(maxItemsPerLevel)
                .ToArray();

            var sb = new StringBuilder();
            sb.AppendLine($"Scene: {scene.name}");
            sb.AppendLine("─".PadRight(40, '─'));

            int totalShown = 0;
            foreach (var root in rootObjects)
            {
                BuildHierarchyTree(sb, root.transform, 0, maxDepth, includeInactive, maxItemsPerLevel, ref totalShown);
            }

            var allRoots = scene.GetRootGameObjects();
            if (allRoots.Length > maxItemsPerLevel)
            {
                sb.AppendLine($"... and {allRoots.Length - maxItemsPerLevel} more root objects");
            }

            return new
            {
                success = true,
                sceneName = scene.name,
                hierarchy = sb.ToString(),
                totalObjectsShown = totalShown
            };
        }

        private static void BuildHierarchyTree(StringBuilder sb, Transform t, int depth, int maxDepth, bool includeInactive, int maxItems, ref int total)
        {
            if (depth > maxDepth) return;
            if (!includeInactive && !t.gameObject.activeInHierarchy) return;

            total++;
            string indent = new string(' ', depth * 2);
            string prefix = depth == 0 ? "► " : "├─";
            string activeMarker = t.gameObject.activeSelf ? "" : " [inactive]";
            string componentHint = GetComponentHint(t);

            sb.AppendLine($"{indent}{prefix} {t.name}{componentHint}{activeMarker}");

            int childrenShown = 0;
            foreach (Transform child in t)
            {
                if (childrenShown >= maxItems)
                {
                    sb.AppendLine($"{indent}  ... and {t.childCount - childrenShown} more children");
                    break;
                }
                BuildHierarchyTree(sb, child, depth + 1, maxDepth, includeInactive, maxItems, ref total);
                childrenShown++;
            }
        }

        private static string GetComponentHint(Transform t)
        {
            if (t.GetComponent<Camera>()) return " 📷";
            if (t.GetComponent<Light>()) return " 💡";
            if (t.GetComponent<Canvas>()) return " 🖼";
            if (t.GetComponent<UnityEngine.UI.Button>()) return " 🔘";
            if (t.GetComponent<Animator>()) return " 🎬";
            if (t.GetComponent<AudioSource>()) return " 🔊";
            if (t.GetComponent<ParticleSystem>()) return " ✨";
            if (t.GetComponent<Collider>() || t.GetComponent<Collider2D>()) return " 🧱";
            if (t.GetComponent<Rigidbody>() || t.GetComponent<Rigidbody2D>()) return " ⚙";
            if (t.GetComponent<SkinnedMeshRenderer>()) return " 🦴";
            if (t.GetComponent<MeshRenderer>()) return " ▣";
            if (t.GetComponent<SpriteRenderer>()) return " 🖾";
            if (t.GetComponent<UnityEngine.UI.Text>() || t.GetComponent<UnityEngine.UI.Image>()) return " 🎨";
            return "";
        }

        [UnitySkill("script_analyze", "Analyze a script's public API (MonoBehaviour, ScriptableObject, or plain class)")]
        public static object ScriptAnalyze(string scriptName, bool includePrivate = false)
        {
            var type = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => { try { return a.GetTypes(); } catch { return new Type[0]; } })
                .FirstOrDefault(t => t.Name.Equals(scriptName, StringComparison.OrdinalIgnoreCase) &&
                                     (typeof(MonoBehaviour).IsAssignableFrom(t) ||
                                      typeof(ScriptableObject).IsAssignableFrom(t) ||
                                      (t.IsClass && !t.IsAbstract && t.Namespace != null &&
                                       !t.Namespace.StartsWith("Unity") && !t.Namespace.StartsWith("System"))));

            if (type == null)
            {
                return new { success = false, error = $"Script '{scriptName}' not found (searched MonoBehaviour, ScriptableObject, and user classes)" };
            }

            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly;
            if (includePrivate) flags |= BindingFlags.NonPublic;

            var fields = type.GetFields(flags)
                .Where(f => !f.Name.StartsWith("<"))
                .Select(f => new
                {
                    name = f.Name,
                    type = GetFriendlyTypeName(f.FieldType),
                    isSerializable = f.IsPublic || f.GetCustomAttribute<SerializeField>() != null
                })
                .ToList();

            var properties = type.GetProperties(flags)
                .Where(p => p.CanRead)
                .Select(p => new
                {
                    name = p.Name,
                    type = GetFriendlyTypeName(p.PropertyType),
                    canWrite = p.CanWrite
                })
                .ToList();

            var methods = type.GetMethods(flags)
                .Where(m => !m.IsSpecialName)
                .Select(m => new
                {
                    name = m.Name,
                    returnType = GetFriendlyTypeName(m.ReturnType),
                    parameters = string.Join(", ", m.GetParameters().Select(p => $"{GetFriendlyTypeName(p.ParameterType)} {p.Name}"))
                })
                .ToList();

            // Unity callbacks only for MonoBehaviour
            List<string> unityEvents = null;
            if (typeof(MonoBehaviour).IsAssignableFrom(type))
            {
                unityEvents = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                    .Where(m => UnityCallbacks.Contains(m.Name))
                    .Select(m => m.Name)
                    .ToList();
            }

            string scriptKind = typeof(MonoBehaviour).IsAssignableFrom(type) ? "MonoBehaviour"
                : typeof(ScriptableObject).IsAssignableFrom(type) ? "ScriptableObject"
                : "Class";

            return new
            {
                success = true,
                script = scriptName,
                fullName = type.FullName,
                kind = scriptKind,
                baseClass = type.BaseType?.Name,
                fields,
                properties,
                methods,
                unityCallbacks = unityEvents
            };
        }

        [UnitySkill("scene_spatial_query", "Find objects within a radius of a point, or near another object")]
        public static object SceneSpatialQuery(
            float x = 0, float y = 0, float z = 0,
            float radius = 10f,
            string nearObject = null,
            string componentFilter = null,
            int maxResults = 50)
        {
            Vector3 center;

            if (!string.IsNullOrEmpty(nearObject))
            {
                var go = GameObjectFinder.Find(nearObject);
                if (go == null)
                    return new { success = false, error = $"Object '{nearObject}' not found" };
                center = go.transform.position;
            }
            else
            {
                center = new Vector3(x, y, z);
            }

            var allObjects = UnityEngine.Object.FindObjectsOfType<GameObject>();
            float radiusSq = radius * radius;

            Type filterType = null;
            if (!string.IsNullOrEmpty(componentFilter))
            {
                filterType = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => { try { return a.GetTypes(); } catch { return new Type[0]; } })
                    .FirstOrDefault(t => t.Name.Equals(componentFilter, StringComparison.OrdinalIgnoreCase) &&
                                         typeof(Component).IsAssignableFrom(t));
            }

            var found = new List<(float dist, object info)>();
            foreach (var go in allObjects)
            {
                if (filterType != null && go.GetComponent(filterType) == null) continue;

                var pos = go.transform.position;
                float distSq = (pos - center).sqrMagnitude;
                if (distSq <= radiusSq)
                {
                    float dist = Mathf.Sqrt(distSq);
                    found.Add((dist, new
                    {
                        name = go.name,
                        path = GameObjectFinder.GetPath(go),
                        distance = dist,
                        position = new { x = pos.x, y = pos.y, z = pos.z }
                    }));
                }
            }

            var results = found.Count <= maxResults
                ? found.Select(f => f.info).ToList()
                : found.OrderBy(f => f.dist).Take(maxResults).Select(f => f.info).ToList();

            return new
            {
                success = true,
                center = new { x = center.x, y = center.y, z = center.z },
                radius,
                totalFound = found.Count,
                results
            };
        }

        [UnitySkill("scene_materials", "Get an overview of all materials and shaders used in the current scene")]
        public static object SceneMaterials(bool includeProperties = false)
        {
            var renderers = UnityEngine.Object.FindObjectsOfType<Renderer>();
            var materialMap = new Dictionary<string, MaterialInfo>();

            foreach (var renderer in renderers)
            {
                foreach (var mat in renderer.sharedMaterials)
                {
                    if (mat == null) continue;
                    var key = mat.GetInstanceID().ToString();
                    if (!materialMap.ContainsKey(key))
                    {
                        materialMap[key] = new MaterialInfo
                        {
                            name = mat.name,
                            shader = mat.shader != null ? mat.shader.name : "null",
                            renderQueue = mat.renderQueue,
                            path = AssetDatabase.GetAssetPath(mat),
                            users = new List<string>()
                        };
                        if (includeProperties && mat.shader != null)
                        {
                            var props = new List<object>();
                            int count = ShaderUtil.GetPropertyCount(mat.shader);
                            for (int i = 0; i < count; i++)
                            {
                                props.Add(new
                                {
                                    name = ShaderUtil.GetPropertyName(mat.shader, i),
                                    type = ShaderUtil.GetPropertyType(mat.shader, i).ToString()
                                });
                            }
                            materialMap[key].properties = props;
                        }
                    }
                    materialMap[key].users.Add(renderer.gameObject.name);
                }
            }

            // Group by shader
            var shaderGroups = materialMap.Values
                .GroupBy(m => m.shader)
                .Select(g => new
                {
                    shader = g.Key,
                    materialCount = g.Count(),
                    materials = g.Select(m => new
                    {
                        m.name, m.path, m.renderQueue,
                        userCount = m.users.Count,
                        users = m.users.Take(5).ToList(),
                        properties = includeProperties ? m.properties : null
                    }).ToList()
                })
                .OrderByDescending(g => g.materialCount)
                .ToList();

            return new
            {
                success = true,
                totalMaterials = materialMap.Count,
                totalShaders = shaderGroups.Count,
                shaders = shaderGroups
            };
        }

        private class MaterialInfo
        {
            public string name, shader, path;
            public int renderQueue;
            public List<string> users;
            public List<object> properties;
        }

        private static string GetFriendlyTypeName(Type type)
        {
            if (type == typeof(void)) return "void";
            if (type == typeof(int)) return "int";
            if (type == typeof(float)) return "float";
            if (type == typeof(bool)) return "bool";
            if (type == typeof(string)) return "string";
            if (type.IsGenericType)
            {
                var baseName = type.Name.Split('`')[0];
                var args = string.Join(", ", type.GetGenericArguments().Select(GetFriendlyTypeName));
                return $"{baseName}<{args}>";
            }
            if (type.IsArray)
            {
                return GetFriendlyTypeName(type.GetElementType()) + "[]";
            }
            return type.Name;
        }
    }
}
