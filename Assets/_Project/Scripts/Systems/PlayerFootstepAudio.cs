using UnityEngine;

namespace Game.PlayerV2
{
    /// <summary>
    /// Receives the OnFootstep / OnLand animation events baked into the locomotion clips
    /// (Walk_N, Run_N, jump/land, etc.) and plays the matching audio. Ported from the
    /// StarterAssets ThirdPersonController so footstep/landing sound is preserved, but kept
    /// as its own component so the controller doesn't own audio.
    ///
    /// Must live on the same GameObject as the Animator (Unity delivers animation events
    /// to components on the animated GameObject).
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public class PlayerFootstepAudio : MonoBehaviour
    {
        [Tooltip("Footstep sounds; one is chosen at random per footstep event.")]
        [SerializeField] private AudioClip[] _footstepAudioClips;

        [Tooltip("Played on the OnLand animation event.")]
        [SerializeField] private AudioClip _landingAudioClip;

        [Range(0f, 1f)]
        [SerializeField] private float _footstepAudioVolume = 0.5f;

        private CharacterController _controller;

        private void Awake()
        {
            _controller = GetComponent<CharacterController>();
        }

        // Called by the animation event in the walk/run clips.
        private void OnFootstep(AnimationEvent animationEvent)
        {
            if (animationEvent.animatorClipInfo.weight <= 0.5f) return;
            if (_footstepAudioClips == null || _footstepAudioClips.Length == 0) return;

            int index = Random.Range(0, _footstepAudioClips.Length);
            AudioSource.PlayClipAtPoint(_footstepAudioClips[index],
                transform.TransformPoint(_controller.center), _footstepAudioVolume);
        }

        // Called by the animation event in the land clip.
        private void OnLand(AnimationEvent animationEvent)
        {
            if (animationEvent.animatorClipInfo.weight <= 0.5f) return;
            if (_landingAudioClip == null) return;

            AudioSource.PlayClipAtPoint(_landingAudioClip,
                transform.TransformPoint(_controller.center), _footstepAudioVolume);
        }
    }
}
