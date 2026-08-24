using System;
using System.Collections.Generic;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace GameFramework.Utility
{
    /// <summary>
    /// 追踪 Animator 子节点的相对路径，并在编辑器中同步修正动画曲线绑定。
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Animator))]
#if UNITY_EDITOR
    [ExecuteAlways]
#endif
    public sealed class AnimationPathProtector : MonoBehaviour
    {
#if UNITY_EDITOR
        [Serializable]
        public struct TrackedNode
        {
            public Transform transform;
            public string lastPath;
        }

        [HideInInspector]
        public List<TrackedNode> trackedNodes = new();

        private Animator _cachedAnimator;

        private void OnEnable()
        {
            _cachedAnimator = GetComponent<Animator>();

            if (!Application.isPlaying)
            {
                EditorApplication.hierarchyChanged += OnHierarchyChangedDetected;

                if (trackedNodes.Count == 0)
                {
                    ResetAndSnapshot();
                }
            }
        }

        private void OnDisable()
        {
            EditorApplication.hierarchyChanged -= OnHierarchyChangedDetected;
        }

        private void OnHierarchyChangedDetected()
        {
            if (this == null || Application.isPlaying)
            {
                return;
            }

            bool hasAnyPathChanged = false;
            List<KeyValuePair<string, string>> pathSwaps = new();

            for (int i = 0; i < trackedNodes.Count; i++)
            {
                TrackedNode node = trackedNodes[i];
                if (node.transform == null)
                {
                    continue;
                }

                string currentPath = GetRelativePath(transform, node.transform);
                if (currentPath == null || currentPath == node.lastPath)
                {
                    continue;
                }

                pathSwaps.Add(new KeyValuePair<string, string>(node.lastPath, currentPath));
                node.lastPath = currentPath;
                trackedNodes[i] = node;
                hasAnyPathChanged = true;
            }

            if (hasAnyPathChanged)
            {
                AutoPatchAnimationClips(pathSwaps);
            }

            AddNewChildrenToTracker();
        }

        private void AutoPatchAnimationClips(List<KeyValuePair<string, string>> pathSwaps)
        {
            if (_cachedAnimator == null || _cachedAnimator.runtimeAnimatorController == null)
            {
                return;
            }

            HashSet<AnimationClip> uniqueClips = new(_cachedAnimator.runtimeAnimatorController.animationClips);
            bool anyClipModified = false;

            foreach (AnimationClip clip in uniqueClips)
            {
                string assetPath = AssetDatabase.GetAssetPath(clip);
                if (assetPath.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Undo.RecordObject(clip, "Auto Protect Animation Paths");

                EditorCurveBinding[] floatBindings = AnimationUtility.GetCurveBindings(clip);
                foreach (EditorCurveBinding binding in floatBindings)
                {
                    if (!TryFindPathSwap(binding.path, pathSwaps, out string newPath))
                    {
                        continue;
                    }

                    AnimationCurve curve = AnimationUtility.GetEditorCurve(clip, binding);
                    AnimationUtility.SetEditorCurve(clip, binding, null);
                    EditorCurveBinding newBinding = binding;
                    newBinding.path = newPath;
                    AnimationUtility.SetEditorCurve(clip, newBinding, curve);
                    anyClipModified = true;
                }

                EditorCurveBinding[] objectBindings = AnimationUtility.GetObjectReferenceCurveBindings(clip);
                foreach (EditorCurveBinding binding in objectBindings)
                {
                    if (!TryFindPathSwap(binding.path, pathSwaps, out string newPath))
                    {
                        continue;
                    }

                    ObjectReferenceKeyframe[] keyframes = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                    AnimationUtility.SetObjectReferenceCurve(clip, binding, null);
                    EditorCurveBinding newBinding = binding;
                    newBinding.path = newPath;
                    AnimationUtility.SetObjectReferenceCurve(clip, newBinding, keyframes);
                    anyClipModified = true;
                }
            }

            if (anyClipModified)
            {
                AssetDatabase.SaveAssets();
                Debug.Log("[AnimationPathProtector] 检测到层级变动，已自动同步动画路径。");
            }
        }

        private static bool TryFindPathSwap(
            string currentPath,
            List<KeyValuePair<string, string>> pathSwaps,
            out string newPath)
        {
            foreach (KeyValuePair<string, string> pathSwap in pathSwaps)
            {
                if (currentPath == pathSwap.Key)
                {
                    newPath = pathSwap.Value;
                    return true;
                }
            }

            newPath = null;
            return false;
        }

        public void ResetAndSnapshot()
        {
            trackedNodes.Clear();
            Transform[] allChildren = GetComponentsInChildren<Transform>(true);
            foreach (Transform child in allChildren)
            {
                if (child == transform)
                {
                    continue;
                }

                string path = GetRelativePath(transform, child);
                if (!string.IsNullOrEmpty(path))
                {
                    trackedNodes.Add(new TrackedNode
                    {
                        transform = child,
                        lastPath = path,
                    });
                }
            }
        }

        private void AddNewChildrenToTracker()
        {
            Transform[] allChildren = GetComponentsInChildren<Transform>(true);
            HashSet<Transform> currentTrackedSet = new();
            foreach (TrackedNode node in trackedNodes)
            {
                if (node.transform != null)
                {
                    currentTrackedSet.Add(node.transform);
                }
            }

            foreach (Transform child in allChildren)
            {
                if (child == transform || currentTrackedSet.Contains(child))
                {
                    continue;
                }

                string path = GetRelativePath(transform, child);
                if (!string.IsNullOrEmpty(path))
                {
                    trackedNodes.Add(new TrackedNode
                    {
                        transform = child,
                        lastPath = path,
                    });
                }
            }

            trackedNodes.RemoveAll(node => node.transform == null);
        }

        private static string GetRelativePath(Transform root, Transform target)
        {
            if (root == target || target == null)
            {
                return string.Empty;
            }

            List<string> pathParts = new();
            Transform current = target;
            while (current != null && current != root)
            {
                pathParts.Add(current.name);
                current = current.parent;
            }

            if (current == null)
            {
                return null;
            }

            pathParts.Reverse();
            return string.Join("/", pathParts);
        }
#endif
    }

#if UNITY_EDITOR
    [CustomEditor(typeof(AnimationPathProtector))]
    public sealed class AnimationPathProtectorEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            AnimationPathProtector protector = (AnimationPathProtector)target;

            EditorGUILayout.HelpBox(
                "动画路径实时守护中。调整子节点层级或名称时，将自动同步 Animator 动画曲线路径。",
                MessageType.Info);
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("当前监视的子节点数量", protector.trackedNodes.Count.ToString(), EditorStyles.boldLabel);
            EditorGUILayout.Space();

            if (GUILayout.Button("刷新节点快照", GUILayout.Height(30)))
            {
                Undo.RecordObject(protector, "Refresh Animation Path Snapshot");
                protector.ResetAndSnapshot();
                EditorUtility.SetDirty(protector);
            }
        }
    }
#endif
}
