#!/usr/bin/env python3
# -*- coding: utf-8 -*-
# Unity Skills Python Helper

import sys
if sys.platform == 'win32':
    import codecs
    if hasattr(sys.stdout, 'buffer'):
        sys.stdout = codecs.getwriter('utf-8')(sys.stdout.buffer, 'replace')
    if hasattr(sys.stderr, 'buffer'):
        sys.stderr = codecs.getwriter('utf-8')(sys.stderr.buffer, 'replace')

import json
import os
import time
import uuid
from typing import Any, Dict, Optional

__version__ = "2.0.0"

DEFAULT_CALL_TIMEOUT = 3600
POLL_INTERVAL = 0.1
STALE_SECONDS = 120


def get_registry_path() -> str:
    return os.path.join(os.path.expanduser("~"), ".unity_skills", "registry.json")


def _version_matches(actual_version: str, target: str) -> bool:
    if not actual_version or not target:
        return False

    cleaned = target.strip()
    if cleaned.lower().startswith("unity"):
        cleaned = cleaned[5:].strip()

    if not cleaned:
        return False

    if cleaned.split(".")[0] == "6" and not cleaned.startswith("6000"):
        return actual_version.startswith("6000.")

    return actual_version.startswith(cleaned)


class UnitySkills:
    def __init__(
        self,
        port: int = None,
        target: str = None,
        version: str = None,
        agent_id: str = None,
        timeout: int = None,
    ):
        self.agent_id = agent_id or _load_agent_id() or "Python"
        self.timeout = timeout or DEFAULT_CALL_TIMEOUT
        self.instance = self._resolve_instance(port=port, target=target, version=version)
        if timeout is None:
            self.timeout = max(1, int(self.instance.get("requestTimeoutMinutes", 60))) * 60

    @property
    def queue_root(self) -> str:
        return self.instance["queueRoot"]

    @property
    def pending_directory(self) -> str:
        return self.instance["pendingDirectory"]

    @property
    def results_directory(self) -> str:
        return self.instance["resultsDirectory"]

    def _resolve_instance(self, port: int = None, target: str = None, version: str = None) -> Dict[str, Any]:
        instances = list_instances()
        if not instances:
            raise ConnectionError("No Unity instance found in registry. Is UnitySkills transport running?")

        if port:
            raise ValueError("Port-based routing is no longer supported. Use target or version instead.")

        if target:
            for info in instances:
                if info.get("id") == target or info.get("name") == target:
                    return info
            raise ValueError(f"Could not find Unity instance matching '{target}' in registry.")

        if version:
            for info in instances:
                if _version_matches(info.get("unityVersion", ""), version):
                    return info
            raise ValueError(f"Could not find Unity instance matching version '{version}'.")

        return instances[0]

    def _send_command(self, command: str, skill_name: str = None, **kwargs) -> Dict[str, Any]:
        os.makedirs(self.pending_directory, exist_ok=True)
        os.makedirs(self.results_directory, exist_ok=True)

        request_id = f"cmd_{uuid.uuid4().hex}"
        command_path = os.path.join(self.pending_directory, f"{request_id}.json")
        result_path = os.path.join(self.results_directory, f"{request_id}.json")

        envelope = {
            "requestId": request_id,
            "command": command,
            "skill": skill_name,
            "agentId": self.agent_id,
            "createdAtUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
            "args": kwargs,
        }

        with open(command_path, "w", encoding="utf-8") as handle:
            json.dump(envelope, handle, ensure_ascii=False, indent=2)

        start = time.time()
        while time.time() - start < self.timeout:
            if os.path.exists(result_path):
                with open(result_path, "r", encoding="utf-8") as handle:
                    data = json.load(handle)
                try:
                    os.remove(result_path)
                except OSError:
                    pass
                return data.get("payload", data)
            time.sleep(POLL_INTERVAL)

        raise TimeoutError(
            f"Unity did not respond within {self.timeout} seconds. "
            "The editor may be paused, compiling, or showing a modal dialog."
        )

    def call(self, skill_name: str, verbose: bool = False, **kwargs) -> Dict[str, Any]:
        try:
            kwargs["verbose"] = verbose
            data = self._send_command("skill", skill_name=skill_name, **kwargs)
            return _normalize_response(data)
        except Exception as exc:
            return {
                "success": False,
                "error": str(exc),
                "hint": "Check UnitySkills transport in Window > UnitySkills.",
            }

    def health(self) -> Dict[str, Any]:
        return self._send_command("health")

    def manifest(self) -> Dict[str, Any]:
        return self._send_command("manifest")

    def create_cube(self, x=0, y=0, z=0, name="Cube"):
        return self.call("create_cube", x=x, y=y, z=z, name=name)

    def create_sphere(self, x=0, y=0, z=0, name="Sphere"):
        return self.call("create_sphere", x=x, y=y, z=z, name=name)

    def delete_object(self, name):
        return self.call("delete_object", objectName=name)


def _normalize_response(data: Dict[str, Any]) -> Dict[str, Any]:
    if data.get("status") == "success":
        result = data.get("result", {})
        normalized = {"success": True}
        if isinstance(result, dict):
            normalized.update(result)
        else:
            normalized["result"] = result
        return normalized
    if data.get("status") == "error":
        return {
            "success": False,
            "error": data.get("error", "Unknown error"),
            "message": data.get("message", ""),
        }
    if "success" in data:
        return data
    return {"success": True, "result": data}


def _load_agent_id() -> Optional[str]:
    config_path = os.path.join(os.path.dirname(__file__), "agent_config.json")
    if not os.path.exists(config_path):
        return None
    try:
        with open(config_path, "r", encoding="utf-8") as handle:
            return json.load(handle).get("agentId")
    except Exception:
        return None


def _read_registry() -> Dict[str, Any]:
    reg_path = get_registry_path()
    if not os.path.exists(reg_path):
        return {}
    try:
        with open(reg_path, "r", encoding="utf-8") as handle:
            return json.load(handle) or {}
    except Exception:
        return {}


def _is_instance_alive(info: Dict[str, Any]) -> bool:
    if info.get("transport") != "file":
        return False
    queue_root = info.get("queueRoot")
    if not queue_root:
        return False
    if not os.path.isdir(queue_root):
        return False
    last_active = info.get("last_active", 0)
    return (time.time() - float(last_active)) < STALE_SECONDS


def _get_default_client() -> UnitySkills:
    global _default_client
    if _default_client is None:
        _default_client = UnitySkills()
    return _default_client


_default_client = None
_auto_workflow_enabled = True
_current_workflow_active = False

_workflow_tracked_skills = {
    'gameobject_create', 'gameobject_delete', 'gameobject_rename',
    'gameobject_set_transform', 'gameobject_duplicate', 'gameobject_set_parent',
    'gameobject_set_active', 'gameobject_create_batch', 'gameobject_delete_batch',
    'gameobject_rename_batch', 'gameobject_set_transform_batch',
    'component_add', 'component_remove', 'component_set_property',
    'component_add_batch', 'component_remove_batch', 'component_set_property_batch',
    'material_create', 'material_assign', 'material_set_color', 'material_set_texture',
    'material_set_emission', 'material_set_float', 'material_set_shader',
    'material_create_batch', 'material_assign_batch', 'material_set_colors_batch',
    'light_create', 'light_set_properties', 'light_set_enabled',
    'prefab_create', 'prefab_instantiate', 'prefab_apply', 'prefab_unpack',
    'prefab_instantiate_batch',
    'ui_create_canvas', 'ui_create_panel', 'ui_create_button', 'ui_create_text',
    'ui_create_image', 'ui_create_inputfield', 'ui_create_slider', 'ui_create_toggle',
    'ui_create_batch', 'ui_set_text', 'ui_set_anchor', 'ui_set_rect',
    'script_create', 'script_delete', 'script_create_batch',
    'terrain_create', 'terrain_set_height', 'terrain_set_heights_batch', 'terrain_paint_texture',
    'asset_import', 'asset_delete', 'asset_move', 'asset_duplicate',
    'scene_create', 'scene_save',
}


def set_auto_workflow(enabled: bool):
    global _auto_workflow_enabled
    _auto_workflow_enabled = enabled


def is_auto_workflow_enabled() -> bool:
    return _auto_workflow_enabled


def connect(port: int = None, target: str = None, version: str = None) -> UnitySkills:
    return UnitySkills(port=port, target=target, version=version)


def set_unity_version(version: str):
    global _default_client
    _default_client = UnitySkills(version=version)


def list_instances() -> list:
    data = _read_registry()
    instances = list(data.values())
    instances = [item for item in instances if _is_instance_alive(item)]
    instances.sort(key=lambda item: item.get("last_active", 0), reverse=True)
    return instances


def call_skill(skill_name: str, **kwargs) -> Dict[str, Any]:
    global _current_workflow_active

    should_track = (
        _auto_workflow_enabled
        and skill_name in _workflow_tracked_skills
        and not _current_workflow_active
        and not skill_name.startswith('workflow_')
    )

    if should_track:
        _current_workflow_active = True
        try:
            _get_default_client().call(
                'workflow_task_start',
                tag=skill_name,
                description=f"Auto: {skill_name} - {str(kwargs)[:100]}",
            )
            result = _get_default_client().call(skill_name, **kwargs)
            _get_default_client().call('workflow_task_end')
            return result
        finally:
            _current_workflow_active = False

    return _get_default_client().call(skill_name, **kwargs)


class WorkflowContext:
    def __init__(self, tag: str, description: str = ''):
        self.tag = tag
        self.description = description

    def __enter__(self):
        global _current_workflow_active
        try:
            call_skill('workflow_task_start', tag=self.tag, description=self.description)
            _current_workflow_active = True
        except Exception:
            _current_workflow_active = False
            raise
        return self

    def __exit__(self, exc_type, exc_val, exc_tb):
        global _current_workflow_active
        try:
            call_skill('workflow_task_end')
        finally:
            _current_workflow_active = False
        return False


def workflow_context(tag: str, description: str = '') -> WorkflowContext:
    return WorkflowContext(tag, description)


def call_skill_with_retry(skill_name: str, max_retries: int = 3, retry_delay: float = 2.0, **kwargs) -> Dict[str, Any]:
    for attempt in range(max_retries):
        result = call_skill(skill_name, **kwargs)
        if result.get('success'):
            return result
        if attempt < max_retries - 1:
            time.sleep(retry_delay)
    return result


def get_skills() -> Dict[str, Any]:
    try:
        return _get_default_client().manifest()
    except Exception as exc:
        return {"status": "error", "error": str(exc)}


def health() -> bool:
    try:
        return _get_default_client().health().get("status") == "ok"
    except Exception:
        return False


def is_unity_running() -> bool:
    return health()


def get_server_status() -> Dict[str, Any]:
    try:
        return _get_default_client().health()
    except Exception as exc:
        return {'status': 'offline', 'reason': str(exc)}


def wait_for_unity(timeout: float = 10.0, check_interval: float = 1.0) -> bool:
    start_time = time.time()
    while time.time() - start_time < timeout:
        if is_unity_running():
            return True
        time.sleep(check_interval)
    return False


def create_gameobject(name, primitive_type=None, x=0, y=0, z=0):
    return call_skill('gameobject_create', name=name, primitiveType=primitive_type, x=x, y=y, z=z)


def delete_gameobject(name):
    return call_skill('gameobject_delete', name=name)


def set_color(game_object, r, g, b, a=1):
    return call_skill('material_set_color', name=game_object, r=r, g=g, b=b, a=a)


def create_script(name, template='MonoBehaviour', wait_for_compile=True):
    result = call_skill('script_create', name=name, template=template)
    if result.get('success') and wait_for_compile:
        time.sleep(2)
        wait_for_unity(timeout=10)
    return result


def play():
    return call_skill('editor_play')


def stop():
    return call_skill('editor_stop')


def main():
    import argparse

    parser = argparse.ArgumentParser(
        description='Unity Skills Python CLI',
        usage='python unity_skills.py [options] <skill_name> [param1=value1] ...'
    )
    parser.add_argument('--list', action='store_true', help='List all available skills')
    parser.add_argument('--list-instances', action='store_true', help='List active Unity instances')
    parser.add_argument('--target', type=str, default=None, help='Connect to instance by name or id')
    parser.add_argument('--version', type=str, default=None, dest='unity_version',
                        help='Connect to Unity instance by version (e.g. "6", "2022", "2022.3")')
    parser.add_argument('skill_name', nargs='?', help='Skill name to execute')
    parser.add_argument('params', nargs='*', help='Skill parameters as key=value pairs')

    args = parser.parse_args()

    if args.target or args.unity_version:
        global _default_client
        _default_client = UnitySkills(target=args.target, version=args.unity_version)

    if args.list:
        print(json.dumps(get_skills(), ensure_ascii=False, indent=2))
        return
    if args.list_instances:
        print(json.dumps(list_instances(), ensure_ascii=False, indent=2))
        return

    if not args.skill_name:
        parser.print_help()
        sys.exit(1)

    params = {}
    for arg in args.params:
        if '=' in arg:
            key, value = arg.split('=', 1)
            if value.lower() == 'true':
                value = True
            elif value.lower() == 'false':
                value = False
            else:
                try:
                    value = float(value) if '.' in value else int(value)
                except ValueError:
                    pass
            params[key] = value

    result = call_skill(args.skill_name, **params)
    print(json.dumps(result, ensure_ascii=False, indent=2))


if __name__ == '__main__':
    main()
