using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Game.EditorTools
{
    /// <summary>
    /// Hierarchy right-click helpers, under GameObject ▸ Iosys Tools.
    ///
    /// <b>Wrap to Prefab</b> — gives every selected object an empty parent named
    /// <c>P_&lt;name&gt;</c>, created at the object's world position/rotation and slotted into the
    /// object's old parent at its old sibling index. The child therefore lands at local
    /// (0,0,0)/identity with its world transform untouched, and the new empty is a clean prefab root
    /// to hang components off (e.g. a ClimbableSurface over its visual + EditorOnly bake children).
    ///
    /// <b>Unwrap from Prefab</b> — the exact reverse: moves the wrapper's children back up into the
    /// wrapper's parent (same sibling position, world transforms preserved) and deletes the wrapper.
    /// Works whether you select the <c>P_</c> wrapper itself or the child inside it.
    ///
    /// Both operations are a single undo step.
    /// </summary>
    public static class IosysHierarchyTools
    {
        private const string Prefix = "P_";
        private const string WrapPath = "GameObject/Iosys Tools/Wrap to Prefab";
        private const string UnwrapPath = "GameObject/Iosys Tools/Unwrap from Prefab";

        // GameObject/ menu items are the only ones that appear in the hierarchy context menu, and
        // Unity invokes them ONCE PER SELECTED OBJECT. Everything here batches over the whole
        // selection, so every invocation but the first is dropped (see RunsOnce).
        private const int MenuPriority = 10;

        // ---------------------------------------------------------------- Wrap

        [MenuItem(WrapPath, true, MenuPriority)]
        private static bool ValidateWrap() => Selection.transforms.Length > 0;

        [MenuItem(WrapPath, false, MenuPriority)]
        private static void Wrap(MenuCommand command)
        {
            if (!RunsOnce(command)) return;

            // Selection.transforms already drops objects whose parent is also selected, so wrapping
            // a parent and its child in one go can't fight over the same reparent.
            Transform[] targets = Selection.transforms;
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Wrap to Prefab");

            var created = new List<Object>(targets.Length);
            foreach (Transform t in targets)
            {
                if (!CanRestructure(t)) continue;

                var wrapper = new GameObject(Prefix + t.name);
                Undo.RegisterCreatedObjectUndo(wrapper, "Wrap to Prefab");

                Transform w = wrapper.transform;
                w.SetParent(t.parent, false);                    // fresh object — no undo record needed
                w.SetPositionAndRotation(t.position, t.rotation);
                w.localScale = Vector3.one;                      // adds no scale of its own; the child keeps its own
                w.SetSiblingIndex(t.GetSiblingIndex());           // wrapper takes the child's place in the list

                Undo.SetTransformParent(t, w, "Wrap to Prefab");  // preserves the child's world transform

                // The wrapper already sits exactly on the child, so the reparent leaves local values
                // at ~0/identity — snap them so the inspector reads clean instead of showing float noise.
                Undo.RecordObject(t, "Wrap to Prefab");
                t.localPosition = Vector3.zero;
                t.localRotation = Quaternion.identity;

                created.Add(wrapper);
            }

            Undo.CollapseUndoOperations(group);
            if (created.Count > 0) Selection.objects = created.ToArray();   // wrappers selected: ready for components
        }

        // -------------------------------------------------------------- Unwrap

        [MenuItem(UnwrapPath, true, MenuPriority + 1)]
        private static bool ValidateUnwrap()
        {
            // Silent: Unity re-runs validation every time the menu is drawn, so a logging pass here
            // would spam the console on every right-click.
            foreach (Transform t in Selection.transforms)
                if (ResolveWrapper(t, false) != null) return true;
            return false;
        }

        [MenuItem(UnwrapPath, false, MenuPriority + 1)]
        private static void Unwrap(MenuCommand command)
        {
            if (!RunsOnce(command)) return;

            // Selecting several children of the SAME wrapper must dissolve it once, not once per child.
            var wrappers = new List<Transform>();
            foreach (Transform t in Selection.transforms)
            {
                Transform w = ResolveWrapper(t, true);
                if (w != null && !wrappers.Contains(w)) wrappers.Add(w);
            }
            if (wrappers.Count == 0)
            {
                Debug.LogWarning("[Iosys] Unwrap: nothing to unwrap — select a \"" + Prefix +
                                 "\" wrapper, or an object that sits inside one.");
                return;
            }

            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Unwrap from Prefab");

            var freed = new List<Object>();
            foreach (Transform wrapper in wrappers)
            {
                Transform newParent = wrapper.parent;
                int index = wrapper.GetSiblingIndex();

                var children = new List<Transform>(wrapper.childCount);
                for (int i = 0; i < wrapper.childCount; i++) children.Add(wrapper.GetChild(i));

                foreach (Transform c in children)
                {
                    Undo.SetTransformParent(c, newParent, "Unwrap from Prefab");   // world transform preserved
                    c.SetSiblingIndex(index++);                                    // land where the wrapper was
                    freed.Add(c.gameObject);
                }

                Undo.DestroyObjectImmediate(wrapper.gameObject);
            }

            Undo.CollapseUndoOperations(group);
            if (freed.Count > 0) Selection.objects = freed.ToArray();
        }

        /// <summary>
        /// The wrapper to dissolve for a selected object: the object itself when it looks like one,
        /// otherwise its parent. A wrapper must be an EMPTY holder — deleting it is only safe when it
        /// carries nothing but its Transform, so one that has picked up components (a ClimbableSurface,
        /// a collider…) is refused rather than silently destroyed along with its data.
        /// Returns null when nothing qualifies. <paramref name="log"/> is off for menu validation,
        /// which Unity re-runs constantly.
        /// </summary>
        private static Transform ResolveWrapper(Transform t, bool log)
        {
            Transform candidate = LooksLikeWrapper(t) ? t
                                : (t.parent != null && LooksLikeWrapper(t.parent)) ? t.parent
                                : null;
            if (candidate == null) return null;
            if (!CanRestructure(candidate, log)) return null;

            // Every child has to be movable too, or we'd delete the wrapper out from under one.
            for (int i = 0; i < candidate.childCount; i++)
                if (!CanRestructure(candidate.GetChild(i), log)) return null;

            Component[] components = candidate.GetComponents<Component>();
            if (components.Length > 1)
            {
                if (log)
                    Debug.LogWarning($"[Iosys] Unwrap: '{candidate.name}' is not an empty wrapper — it has " +
                                     $"{components.Length - 1} component(s) that would be destroyed with it. " +
                                     "Remove them first if you really want it dissolved.", candidate);
                return null;
            }
            return candidate;
        }

        private static bool LooksLikeWrapper(Transform t) => t.name.StartsWith(Prefix) && t.childCount > 0;

        // --------------------------------------------------------------- Shared

        /// <summary>
        /// Whether this object may be reparented. Prefab instances can gain new children, but their
        /// insides can't be restructured — only an outermost instance ROOT can be moved. Same for the
        /// root of an open prefab stage. <paramref name="log"/> is off for menu validation.
        /// </summary>
        private static bool CanRestructure(Transform t, bool log = true)
        {
            GameObject go = t.gameObject;
            if (!go.scene.IsValid()) return false;   // a project asset, not a scene object

            PrefabStage stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null && go == stage.prefabContentsRoot)
            {
                if (log)
                    Debug.LogWarning($"[Iosys] '{go.name}' is the root of the open prefab — it can't be " +
                                     "reparented from inside prefab mode. Skipped.", go);
                return false;
            }

            if (PrefabUtility.IsPartOfPrefabInstance(go) && !PrefabUtility.IsOutermostPrefabInstanceRoot(go))
            {
                if (log)
                    Debug.LogWarning($"[Iosys] '{go.name}' lives inside a prefab instance — Unity does not allow " +
                                     "restructuring one from the scene. Unpack it, or edit the prefab. Skipped.", go);
                return false;
            }
            return true;
        }

        /// <summary>
        /// Unity fires a GameObject/ menu item once for every selected object, with the context set to
        /// each in turn. These commands act on the whole selection at once, so only the pass whose
        /// context is the first selected object is allowed through (the menu-bar invocation has no
        /// context and always runs).
        /// </summary>
        private static bool RunsOnce(MenuCommand command)
        {
            if (!(command.context is GameObject go)) return true;
            GameObject[] selection = Selection.gameObjects;
            return selection.Length <= 1 || go == selection[0];
        }
    }
}
