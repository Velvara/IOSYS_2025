using System.IO;
using UnityEditor;
using UnityEngine;

namespace Game.EditorTools
{
    /// <summary>
    /// Bakes a FORWARD-playing reversed copy of an AnimationClip. Unity does not fire animation
    /// events on clips played at negative speed, so a "backwards" motion driven by speed -1 can't
    /// carry sync events. This makes a real reversed clip (every curve time-flipped) that plays at
    /// positive speed and therefore CAN hold events — e.g. the RopeAlone down clip derived from the
    /// up clip, with an OnRopeAloneSync event at each both-hands frame.
    ///
    /// Usage: select the source clip in the Project window (works on an FBX sub-clip too) →
    /// right-click → Animation → Create Reversed Clip. The copy is written next to the source as
    /// "&lt;name&gt;_Reversed.anim", with loop/import settings preserved.
    /// </summary>
    public static class AnimationClipReverser
    {
        [MenuItem("Assets/Animation/Create Reversed Clip", true)]
        private static bool Validate() => Selection.activeObject is AnimationClip;

        [MenuItem("Assets/Animation/Create Reversed Clip")]
        private static void ReverseSelected()
        {
            var source = Selection.activeObject as AnimationClip;
            if (source == null) return;

            AnimationClip reversed = Reverse(source);

            string srcPath = AssetDatabase.GetAssetPath(source);          // may be the FBX for a sub-clip
            string dir = Path.GetDirectoryName(srcPath);
            string path = AssetDatabase.GenerateUniqueAssetPath(
                (dir + "/" + source.name + "_Reversed.anim").Replace('\\', '/'));

            AssetDatabase.CreateAsset(reversed, path);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Selection.activeObject = reversed;
            EditorGUIUtility.PingObject(reversed);
            Debug.Log($"[AnimationClipReverser] Created reversed clip: {path}");
        }

        /// <summary>Returns a new clip whose every curve (incl. humanoid muscle curves) plays the
        /// source's motion in reverse at positive speed.</summary>
        public static AnimationClip Reverse(AnimationClip source)
        {
            var clip = new AnimationClip
            {
                frameRate = source.frameRate,
                legacy = source.legacy,
                wrapMode = source.wrapMode,
                name = source.name + "_Reversed"
            };

            float length = source.length;

            // Float / transform / humanoid-muscle curves.
            foreach (var b in AnimationUtility.GetCurveBindings(source))
            {
                AnimationCurve src = AnimationUtility.GetEditorCurve(source, b);
                Keyframe[] keys = src.keys;
                var newKeys = new Keyframe[keys.Length];
                for (int i = 0; i < keys.Length; i++)
                {
                    Keyframe k = keys[keys.Length - 1 - i];   // reverse key order
                    k.time = length - k.time;                 // flip time axis
                    // Time reversal flips slope sign and swaps in/out tangents + weights.
                    float inT = k.inTangent, outT = k.outTangent;
                    k.inTangent = -outT;
                    k.outTangent = -inT;
                    float inW = k.inWeight, outW = k.outWeight;
                    k.inWeight = outW;
                    k.outWeight = inW;
                    k.weightedMode = SwapWeighted(k.weightedMode);
                    newKeys[i] = k;
                }
                var newCurve = new AnimationCurve(newKeys)
                {
                    preWrapMode = src.postWrapMode,
                    postWrapMode = src.preWrapMode
                };
                AnimationUtility.SetEditorCurve(clip, b, newCurve);
            }

            // Object-reference curves (sprite swaps etc.) — reverse their times too.
            foreach (var b in AnimationUtility.GetObjectReferenceCurveBindings(source))
            {
                ObjectReferenceKeyframe[] keys = AnimationUtility.GetObjectReferenceCurve(source, b);
                var newKeys = new ObjectReferenceKeyframe[keys.Length];
                for (int i = 0; i < keys.Length; i++)
                {
                    ObjectReferenceKeyframe k = keys[keys.Length - 1 - i];
                    k.time = length - k.time;
                    newKeys[i] = k;
                }
                AnimationUtility.SetObjectReferenceCurve(clip, b, newKeys);
            }

            // Preserve loop/root settings (Loop Time, loop pose, cycle offset, etc.), fixing the range
            // to the freshly-built 0..length curves.
            AnimationClipSettings settings = AnimationUtility.GetAnimationClipSettings(source);
            settings.startTime = 0f;
            settings.stopTime = length;
            AnimationUtility.SetAnimationClipSettings(clip, settings);

            // If the source already had events, mirror their times (usually none — you add them after).
            AnimationEvent[] events = AnimationUtility.GetAnimationEvents(source);
            if (events != null && events.Length > 0)
            {
                for (int i = 0; i < events.Length; i++)
                    events[i].time = length - events[i].time;
                AnimationUtility.SetAnimationEvents(clip, events);
            }

            return clip;
        }

        private static WeightedMode SwapWeighted(WeightedMode m) =>
            m == WeightedMode.In ? WeightedMode.Out :
            m == WeightedMode.Out ? WeightedMode.In : m;
    }
}
