using UnityEngine;
using Game.PlayerV2;

public abstract class AimModeBase : MonoBehaviour, IAimMode
{
    [Header("Shared References")]
    public Animator animator;
    public Transform cameraTransform;
    public Unity.Cinemachine.CinemachineThirdPersonFollow cinemachineThirdPersonFollow;
    public Transform playerTransform;

    // ── PlayerV2 controller interfaces (resolved from the player hierarchy) ──
    private IPlayerMotor _playerMotor;
    private ICameraState _cameraState;
    private InputHandler _input;

    protected IPlayerMotor PlayerMotor => _playerMotor ??= GetComponentInParent<IPlayerMotor>();
    protected ICameraState CameraState => _cameraState ??= GetComponentInParent<ICameraState>();

    /// <summary>Camera look is frozen (e.g. external control); aim modes skip rotation/exaggeration.</summary>
    public bool IsCameraFrozen => CameraState != null && CameraState.IsCameraFrozen;

    /// <summary>Player move input (replaces the old StarterAssetsInputs.CurrentMoveInput).</summary>
    protected Vector2 MoveInput
    {
        get
        {
            _input ??= GetComponentInParent<InputHandler>();
            return _input != null ? _input.MoveInput : Vector2.zero;
        }
    }

    [Header("Target Camera Settings")]
    public float camHeight = 1.2f;
    public float camDist = 2.5f;
    public float camSide = 0.5f;
    public float maxVerticalAngle = 75.0f;

    //Default Camera Values
    protected float defaultCamHeight;
    protected float defaultCamDist;
    protected float defaultCamSide;
    //private bool defaultsCaptured = false;

    // animator param names used by all modes for movement blending
    protected readonly int AimMoveXHash = Animator.StringToHash("AimMoveX");
    protected readonly int AimMoveYHash = Animator.StringToHash("AimMoveY");

    protected void ResetCameraToDefaults()
    {
        var follow = cinemachineThirdPersonFollow;
        if (follow == null) return;

        var manager = GameObject.FindFirstObjectByType<AimManager>();
        if (manager == null) return;

        follow.ShoulderOffset = new Vector3(
            follow.ShoulderOffset.x,
            manager.GetDefaultCamHeight(),
            follow.ShoulderOffset.z
        );
        follow.CameraDistance = manager.GetDefaultCamDist();
        follow.CameraSide = manager.GetDefaultCamSide();
    }


    public virtual void EnterMode()
    {
        if (animator) animator.SetBool("IsAiming", true);
        var motor = PlayerMotor;
        if (motor != null) motor.RotateOnMove = false;
    }

    public virtual void UpdateMode()
    {
        if (IsCameraFrozen)
        {
            if (animator != null)
            {
                animator.SetFloat(AimMoveXHash, 0f);
                animator.SetFloat(AimMoveYHash, 0f);
            }
            // Skip rotation updates while camera is frozen
            return;
        }
        // default: rotate toward camera horizontally
        Vector3 forward = cameraTransform.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude > 0.01f)
        {
            Quaternion targetRotation = Quaternion.LookRotation(forward);
            playerTransform.rotation = Quaternion.Slerp(playerTransform.rotation, targetRotation, Time.deltaTime * 10f);
        }
    }

    public virtual void ExitMode()
    {
        // Clear any animator flags common to all aim modes if needed
        if (animator)
        {
            animator.SetBool("IsAiming", false);
            int upperBodyLayer = animator.GetLayerIndex("AimingUpperbody");
            animator.CrossFade("UpperBodyIdle", 0.2f, upperBodyLayer);
        }
        var motor = PlayerMotor;
        if (motor != null) motor.RotateOnMove = true;
    }

    public virtual void OnItemChanged(GameObject newItem) { }
}
