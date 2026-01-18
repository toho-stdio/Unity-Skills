using UnityEngine;
using UnityEditor;
using System;
using System.IO;

namespace UnitySkills
{
    /// <summary>
    /// One-click skill installer for Claude Code and Antigravity.
    /// </summary>
    public static class SkillInstaller
    {
        // Claude Code paths
        public static string ClaudeProjectPath => Path.Combine(Application.dataPath, "..", ".claude", "skills", "unity-skills");
        public static string ClaudeGlobalPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "skills", "unity-skills");
        
        // Antigravity paths
        public static string AntigravityProjectPath => Path.Combine(Application.dataPath, "..", ".agent", "skills", "unity-skills");
        public static string AntigravityGlobalPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "antigravity", "skills", "unity-skills");

        public static bool IsClaudeProjectInstalled => Directory.Exists(ClaudeProjectPath) && File.Exists(Path.Combine(ClaudeProjectPath, "SKILL.md"));
        public static bool IsClaudeGlobalInstalled => Directory.Exists(ClaudeGlobalPath) && File.Exists(Path.Combine(ClaudeGlobalPath, "SKILL.md"));
        public static bool IsAntigravityProjectInstalled => Directory.Exists(AntigravityProjectPath) && File.Exists(Path.Combine(AntigravityProjectPath, "SKILL.md"));
        public static bool IsAntigravityGlobalInstalled => Directory.Exists(AntigravityGlobalPath) && File.Exists(Path.Combine(AntigravityGlobalPath, "SKILL.md"));

        public static (bool success, string message) InstallClaude(bool global)
        {
            try
            {
                var targetPath = global ? ClaudeGlobalPath : ClaudeProjectPath;
                return InstallSkill(targetPath, "Claude Code");
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        public static (bool success, string message) InstallAntigravity(bool global)
        {
            try
            {
                var targetPath = global ? AntigravityGlobalPath : AntigravityProjectPath;
                return InstallSkill(targetPath, "Antigravity");
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        private static (bool success, string message) InstallSkill(string targetPath, string name)
        {
            // Create directory
            if (!Directory.Exists(targetPath))
                Directory.CreateDirectory(targetPath);

            // Generate SKILL.md
            var skillMd = GenerateSkillMd();
            File.WriteAllText(Path.Combine(targetPath, "SKILL.md"), skillMd);

            // Generate unity_skills.py helper
            var pythonHelper = GeneratePythonHelper();
            var scriptsPath = Path.Combine(targetPath, "scripts");
            if (!Directory.Exists(scriptsPath))
                Directory.CreateDirectory(scriptsPath);
            File.WriteAllText(Path.Combine(scriptsPath, "unity_skills.py"), pythonHelper);

            Debug.Log($"[UnitySkills] Installed skill to: {targetPath}");
            return (true, targetPath);
        }

        private static string GenerateSkillMd()
        {
            return @"---
name: Unity Editor Control
description: Control Unity Editor via REST API. Create GameObjects, manage scenes, materials, prefabs, scripts, and more.
---

# Unity Editor Control Skill

This skill enables AI to control Unity Editor through a REST API.

## Prerequisites

1. Install the UnitySkills package in Unity
2. Start the REST server: Window → UnitySkills → Start Server
3. Server runs at: http://localhost:8090

## Usage

Use the Python helper to call skills:

```python
import sys
sys.path.insert(0, 'scripts')
from unity_skills import call_skill

# Create a cube
call_skill('gameobject_create', name='MyCube', primitiveType='Cube', x=0, y=1, z=0)

# Set color
call_skill('material_set_color', gameObjectName='MyCube', r=1, g=0, b=0)

# Save scene
call_skill('scene_save', scenePath='Assets/Scenes/MyScene.unity')
```

## Available Skills

### Scene Management
- `scene_create(scenePath)` - Create new scene
- `scene_load(scenePath, additive)` - Load scene
- `scene_save(scenePath)` - Save scene
- `scene_get_info()` - Get scene info
- `scene_get_hierarchy(maxDepth)` - Get hierarchy tree

### GameObject Operations
- `gameobject_create(name, primitiveType, x, y, z)` - Create object
- `gameobject_delete(name, instanceId)` - Delete object
- `gameobject_find(name, tag, component)` - Find objects
- `gameobject_set_transform(name, posX, posY, posZ, rotX, rotY, rotZ, scaleX, scaleY, scaleZ)` - Set transform
- `gameobject_duplicate(name)` - Duplicate object
- `gameobject_set_parent(childName, parentName)` - Set parent

### Component Operations
- `component_add(gameObjectName, componentType)` - Add component
- `component_remove(gameObjectName, componentType)` - Remove component
- `component_list(gameObjectName)` - List components
- `component_set_property(gameObjectName, componentType, propertyName, value)` - Set property

### Material Operations
- `material_create(name, shaderName, savePath)` - Create material
- `material_set_color(gameObjectName, r, g, b, a)` - Set color
- `material_set_texture(gameObjectName, texturePath)` - Set texture
- `material_assign(gameObjectName, materialPath)` - Assign material

### Asset Operations
- `asset_import(sourcePath, destinationPath)` - Import asset
- `asset_delete(assetPath)` - Delete asset
- `asset_find(searchFilter)` - Find assets
- `asset_refresh()` - Refresh database

### Editor Control
- `editor_play()` / `editor_stop()` / `editor_pause()` - Play mode
- `editor_select(gameObjectName)` - Select object
- `editor_undo()` / `editor_redo()` - Undo/Redo
- `editor_execute_menu(menuPath)` - Execute menu item

### Prefab Operations
- `prefab_create(gameObjectName, savePath)` - Create prefab
- `prefab_instantiate(prefabPath, x, y, z)` - Instantiate prefab
- `prefab_apply(gameObjectName)` - Apply changes

### Script Operations
- `script_create(scriptName, folder)` - Create script
- `script_read(scriptPath)` - Read script
- `script_find_in_file(pattern, folder)` - Search in scripts

### Console
- `console_log(message, type)` - Log message
- `console_clear()` - Clear console
- `console_get_logs(filter)` - Get logs

## Direct REST API

```bash
# List all skills
curl http://localhost:8090/skills

# Call a skill
curl -X POST http://localhost:8090/skill/gameobject_create \
  -H ""Content-Type: application/json"" \
  -d '{""name"":""Cube"",""primitiveType"":""Cube""}'
```
";
        }

        private static string GeneratePythonHelper()
        {
            return @"\"\"\"
Unity Skills Python Helper
\"\"\"
import requests
from typing import Any, Dict, Optional

UNITY_URL = ""http://localhost:8090""

def call_skill(name: str, **kwargs) -> Dict[str, Any]:
    \"\"\"Call a Unity skill with the given parameters.\"\"\"
    try:
        response = requests.post(
            f""{UNITY_URL}/skill/{name}"",
            json=kwargs,
            timeout=30
        )
        return response.json()
    except requests.exceptions.ConnectionError:
        return {""error"": ""Cannot connect to Unity. Is the REST server running?""}
    except Exception as e:
        return {""error"": str(e)}

def get_skills() -> Dict[str, Any]:
    \"\"\"Get list of all available skills.\"\"\"
    try:
        response = requests.get(f""{UNITY_URL}/skills"", timeout=10)
        return response.json()
    except:
        return {""error"": ""Cannot connect to Unity""}

def is_unity_running() -> bool:
    \"\"\"Check if Unity REST server is running.\"\"\"
    try:
        response = requests.get(f""{UNITY_URL}/health"", timeout=5)
        return response.status_code == 200
    except:
        return False

# Convenience functions
def create_gameobject(name: str, primitive_type: str = None, x: float = 0, y: float = 0, z: float = 0):
    return call_skill(""gameobject_create"", name=name, primitiveType=primitive_type, x=x, y=y, z=z)

def delete_gameobject(name: str):
    return call_skill(""gameobject_delete"", name=name)

def set_color(game_object: str, r: float, g: float, b: float, a: float = 1):
    return call_skill(""material_set_color"", gameObjectName=game_object, r=r, g=g, b=b, a=a)

def save_scene(path: str = None):
    return call_skill(""scene_save"", scenePath=path) if path else call_skill(""scene_save"")

def play():
    return call_skill(""editor_play"")

def stop():
    return call_skill(""editor_stop"")
";
        }
    }
}
