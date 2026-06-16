# Player Live Values

> Fill in the live prefab / scene-instance value after each colon.
> Write `default` if it matches the hint in parentheses. Only numeric
> (int/float) and boolean fields are listed, per request.

---

## ThirdPersonController
RunSpeed:               5
SprintSpeed:            12
StealthSpeed:           2
FatiguedSpeed:          1.2
SpeedChangeRate:        10
RotationSmoothTime:     0.12
JumpHeight:             1.2
Gravity:                -15
JumpTimeout:            0.3
FallTimeout:            0.15
GroundedOffset:         -0.14
GroundedRadius:         0.28
GroundLayers:           Default
TopClamp:               70
BottomClamp:            -30
CameraAngleOverride:    0
LockCameraPosition:     false

## Cinemachine rig (the vcam following CinemachineCameraTarget)
Rig type:               CinemachineThirdPersonFollow
ShoulderOffset:         1,0,0
CameraDistance:         4
CameraSide:             0.5
# These are the DEFAULT/non-aiming values the camera rests at.

---

## AimModeBase  (shared base — set PER aim mode instance below)
# Each aim mode (ThrowAim / ShootAim / ScanAim / HookshotDragMode) has its
# own copy of these. Log them per mode if they differ.
camHeight:              (default 1.2)
camDist:                (default 2.5)
camSide:                (default 0.5)
maxVerticalAngle:       (default 75.0)

## AimManager
dragCamResetSpeed:      (default 0.5)
enableHeadLook:         (default true)
enableShootHandIK:      (default true)
headLookWeight:         (default 1.0)   [range 0-1]
handIKWeight:           (default 1.0)   [range 0-1]
enableShootShoulderAim: (default true)
shoulderAimWeight:      (default 1.0)
shoulderRotationSpeed:  1
cameraTransitionDuration: 1

## TrajectoryPredictor
resolution:             (default 30)
timeStep:               (default 0.1)
Collision Layers: Default, TransparentFX, Water, UI, Trunk, LightSource, Fungus, Organic Dead

## ThrowAim   (+ AimModeBase fields above)
throwForce:             (default 15.0)
throwForceLookMultiplier: (default 1.5)
# AimModeBase for ThrowAim:
camHeight:0.21
camDist:1.22
camSide:0.9
maxVerticalAngle:75

## ShootAim   (+ AimModeBase fields above)
lookUpMultiplier:       10
# AimModeBase for ShootAim:
camHeight:0.21
camDist:0.2
camSide:0.7
maxVerticalAngle:75

## ScanAim   (no own numeric/bool fields — AimModeBase only)
# AimModeBase for ScanAim:
camHeight:0
camDist:1.95
camSide:0.74
maxVerticalAngle:75

## HookshotDragMode   (+ AimModeBase fields above)
dragSpeed:              12
stopDistance:           2
dragRotationAmount:     (default 1.0)   [range 0-1]
failsafeDelay:          (default 1.0)
cameraDragLag:          (default 5.0)
rotationSpeed:          (default 5.0)
# AimModeBase for HookshotDragMode:
camHeight:0
camDist:8
camSide:0.5
maxVerticalAngle:75

---

## PlayerStamina   (list only values overridden from these defaults, or write "all defaults")
StaminaDrainRate:           5
StaminaRecoveryRate:        5
StaminaRecoveryRateFloorPct:10   [range 1-100]
MaxStaDrainThreshold:       15
RestMaxPenaltyRate:         1
RestMaxStaHardFloor:        50
HungerMaxStaHardFloor:      25
ThirstDrainRate:            0.24
ThirstRestoreRate:          25
HungerDrainRate:            0.144
HungerMaxPenaltyCap:        25
HungerMaxDrainRate:         0.25
HungerRestoreRate:          25
HungerMaxRestoreRate:       25
DebugStaminaDrainRate:      (default 50)
DebugHungerDrainRate:       (default 50)
DebugThirstDrainRate:       (default 50)

---

## Animator
Animator Controller asset name + path: C:\Vel\Unity Projects\IOSYS_2025\Assets\Imported\Starter Assets\Runtime\ThirdPersonController\Character\Animations\StarterAssetsThirdPerson.controller
# Confirm parameter list (name : type). Expected locomotion contract:
Speed:        float
Jump:         bool
Grounded:     bool
FreeFall:     bool
MotionSpeed:  float
Sprint:       bool
Fatigued:     bool
Stealth:      bool
# Aim-system params on the same controller (confirm they exist):
IsAiming:           bool
AimMoveX:           float
AimMoveY:           float
IsScanning:         bool
IsShootingAiming:   bool
IsHookshotDragging: bool
Fire:               trigger
Throw:              trigger
# List any other parameters not shown above:
#Animator Layers
Layer 0: "Base Layer", Weight: 1, Mask: none, Blending: Override, IK Pass: false, Sync: false
Layer 1: "AimingLowerBody", Weight: 1, Mask: LowerBodyMask, Blending: Override, IK Pass: false, Sync: false
Layer 2: "AimingUpperBody", Weight: 1, Mask: UpperBodyMask, Blending: Override, IK Pass: true, Sync: false
Layer 3: "HookshotDragLayer", Weight: 0, Mask: HookshotDragMask, Blending: Override, IK PASS: false, Sync: false
