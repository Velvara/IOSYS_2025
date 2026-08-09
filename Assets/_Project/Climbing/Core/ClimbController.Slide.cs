using UnityEngine;

namespace Game.Climbing
{
    /// <summary>
    /// ClimbController — entry slide, attach prompt, and the enter animation.
    ///
    /// ENTRY SLIDE (Free-type surfaces only): a grab while FALLING doesn't attach the hands where the
    /// press landed — the body keeps sliding DOWN the wall first. Total distance comes from the entry
    /// speed (slideDistanceByVelocity), progressed over slideDuration along an ease-out curve (fast →
    /// 0). BRAKE (enableSlideBrake): while Use is HELD the frame's advance is scaled by
    /// brakeSpeedFactor and the brake pose shows; releasing returns to full curve speed and the slide
    /// pose — distance skipped while braking is never covered. The wall is followed by probe and the surface's
    /// holds are checked every frame — running out of HOLDS drops into a fall carrying the slide's
    /// speed, but losing just the WALL (an overhang lip) with holds still in reach stops and attaches
    /// AT THE LIP: entry slides always end hanging on, and no QTE ever runs here — only the risk SLIP
    /// (ClimbController.Slip.cs, started by a hand-step grip roll, never by a grab) can drop the
    /// climber via its Use-QTE. Where the slide stops,
    /// NOTHING moves: the body pins in place, every limb stays animation-driven (IK asleep) and the
    /// found hold is only remembered (_handsPending). The first MOVE input wakes the rig for direct
    /// traversal (WakeRigForTraversal) — each hand then attaches PER-LIMB on its own first step,
    /// travelling straight from its animated pose to its own traversal hold (AttachHandIfLoose), and
    /// the feet step in from their bones; the body un-pins across the first step. JUMP instead
    /// reaches BOTH hands to the remembered pair (BeginDeferredReach — BracedReady needs both on
    /// holds). Wall at the feet = braced; air at the feet = free hang, held until the feet find wall
    /// again (see the _postSlideFreeHang gate in UpdatePoseSwitch).
    ///
    /// ATTACH PROMPT: a billboarded world-space marker at the hold Use would grab, shown while free
    /// whenever a candidate exists (per-frame accurate). On a slide entry it re-parents to the
    /// character and rides along until the brake engages (the "you can still hold Use" affordance)
    /// or the slide ends, then hides.
    ///
    /// ENTER ANIMATION: every grab plays the ClimbEnter state (ClimbingLayer). Its clip carries an
    /// OnClimbEnterHold animation event at the "hanging on" frame: a sliding entry freezes the clip
    /// there (ClimbEnterSpeed → 0) — or crossfades to ClimbSlideBrake while braking — resumes when the
    /// slide attaches, and blends into ClimbHang/FreeHang when the clip completes. Calm grabs just
    /// play it through. All three animator pieces (ClimbEnter state, ClimbEnterSpeed multiplier param,
    /// ClimbSlideBrake state) are optional and degrade gracefully until authored — see Start().
    /// </summary>
    public partial class ClimbController
    {
        // ------------------------------------------------------------------ entry slide

        /// <summary>Enters the slide phase (called from Grab INSTEAD of attaching the hands).</summary>
        private void BeginEntrySlide(float distance, float entryVy)
        {
            _sliding = true;
            _slideElapsed = 0f;
            _slideDistSoFar = 0f;
            _slideBasePos = 0f;
            _slideSpeed = 0f;
            _slideTotalDist = distance;
            _slideBraking = false;
            _slideWallNormal = _candidateRot * Vector3.forward;

            _rig.SetMasterWeight(0f);
            _masterWeightTarget = 0f;   // no IK while sliding — the hands ride the (frozen) enter clip

            // Snap the body onto the wall at its current height: probe from the chest toward the wall,
            // falling back to the candidate hold when the probe misses (grabbed at max reach).
            Vector3 chest = transform.position + Vector3.up * detectHeightOffset;
            if (Physics.Raycast(chest + _slideWallNormal * 0.5f, -_slideWallNormal, out RaycastHit hit,
                                slideWallProbe + 0.5f, SlideProbeMask(), QueryTriggerInteraction.Ignore))
            {
                _slideWallNormal = hit.normal;
                transform.position = hit.point + hit.normal * rootForwardOffset - Vector3.up * detectHeightOffset;
            }
            else
            {
                transform.position = _candidatePos + _slideWallNormal * rootForwardOffset - Vector3.up * detectHeightOffset;
            }
            FaceSlideWall();

            // Pose bookkeeping: slides are wall entries (braced-flavoured); the real pose is picked at attach.
            _freeHang = false;
            _bracedWeight = 1f;
            if (_animator != null) _animator.SetBool(_hIsFreeHang, false);
            PlayEnterAnim();

            // The prompt locks onto the character and rides the slide while braking is possible.
            if (_prompt != null && _prompt.gameObject.activeSelf)
            {
                _prompt.SetParent(transform, true);
                _promptOnCharacter = true;
            }

            StartSlideParticles();

            if (logClimbEvents)
                Debug.Log($"[ClimbController] Entry slide on '{_currentSurface.name}': vy {entryVy:0.0} m/s → {distance:0.00} m.");
        }

        /// <summary>Per-frame slide: brake input, distance progression, wall follow, hold/ground checks.</summary>
        private void TickSlide(float dt)
        {
            // The layer fades in on its own — the usual master-IK coupling reads 0 during the slide.
            _enterLayerFloor = Mathf.MoveTowards(_enterLayerFloor, 1f, dt / Mathf.Max(0.01f, ikFadeInDuration));
            if (_animator != null && _climbLayerIndex >= 0)
                _animator.SetLayerWeight(_climbLayerIndex, _enterLayerFloor);

            // Brake: active only WHILE Use is held (the grab press usually still holds it at slide
            // start — releasing restores full speed, so that costs nothing). Pose swaps both ways.
            bool braking = enableSlideBrake && _useAction != null && _useAction.IsPressed();
            if (braking != _slideBraking) SetSlideBraking(braking);

            // The prompt rides the character for the whole slide (braking stays available throughout).
            if (_promptOnCharacter) BillboardPrompt();

            // Advance by integration: the curve defines the UN-BRAKED motion over time; a braked frame
            // just covers brakeSpeedFactor of its advance (that distance is never made up — the slide
            // ends on the same clock, shorter).
            _slideElapsed += dt;
            float u = slideDuration > 0f ? Mathf.Clamp01(_slideElapsed / slideDuration) : 1f;
            float basePos = _slideTotalDist * Mathf.Clamp01(slideProgressCurve.Evaluate(u));
            float delta = Mathf.Max(0f, basePos - _slideBasePos);
            _slideBasePos = basePos;
            if (_slideBraking) delta *= brakeSpeedFactor;
            _slideSpeed = dt > 0f ? delta / dt : 0f;
            _slideDistSoFar += delta;

            // Advance down along the wall plane, then re-stick to the wall by probe.
            Vector3 slideDir = Vector3.ProjectOnPlane(Vector3.down, _slideWallNormal);
            slideDir = slideDir.sqrMagnitude > 1e-4f ? slideDir.normalized : Vector3.down;
            Vector3 pos = transform.position + slideDir * delta;

            Vector3 chest = pos + Vector3.up * detectHeightOffset;
            if (Physics.Raycast(chest + _slideWallNormal * 0.3f, -_slideWallNormal, out RaycastHit hit,
                                slideWallProbe + 0.3f, SlideProbeMask(), QueryTriggerInteraction.Ignore))
            {
                _slideWallNormal = hit.normal;
                pos = hit.point + hit.normal * rootForwardOffset - Vector3.up * detectHeightOffset;
            }
            else if (AnyHoldWithin(transform.position + Vector3.up * detectHeightOffset, slideHoldRadius))
            {
                // ENTRY slides (jump reattach / falling grab) always end HANGING ON unless the holds
                // themselves run out — losing the wall at an overhang lip while holds are still in
                // reach stops the slide AT THE LIP and attaches there (the feet probe will pick free
                // hang if there's no wall below). Only the risk SLIP (ClimbController.Slip.cs) may
                // drop the climber off a lost wall.
                if (logClimbEvents) Debug.Log("[ClimbController] Entry slide: wall ended — attaching at the lip.");
                AttachAfterSlide();
                return;
            }
            else
            {
                EndSlideToFall("wall lost with no holds left");   // slid past the surface entirely
                return;
            }

            // Past the last holds — the hands would have nothing to catch → fall.
            if (!AnyHoldWithin(pos + Vector3.up * detectHeightOffset, slideHoldRadius))
            {
                EndSlideToFall("holds ran out");
                return;
            }

            transform.position = pos;
            FaceSlideWall();

            // Slid all the way to the ground → just land (mirrors reach-bottom).
            if (Physics.Raycast(transform.position + Vector3.up * 0.1f, Vector3.down,
                                reachBottomDistance + 0.1f, mantleSurfaceMask, QueryTriggerInteraction.Ignore))
            {
                if (logClimbEvents) Debug.Log("[ClimbController] Slide reached the ground — stepping off.");
                _motor?.SetVerticalVelocity(0f);
                CleanupSlide();
                FinishRelease(ClimbEndKind.Landed);
                return;
            }

            if (u >= 1f) AttachAfterSlide();
        }

        /// <summary>Brake on/off (tracks the Use button while sliding). Distance-wise TickSlide just
        /// scales its per-frame advance; this handles the POSE swap: brake pose while held, back to
        /// the (frozen) slide frame of the enter clip on release.</summary>
        private void SetSlideBraking(bool on)
        {
            _slideBraking = on;
            if (logClimbEvents)
                Debug.Log($"[ClimbController] Slide brake {(on ? "ON" : "OFF")} at {_slideDistSoFar:0.00} m.");

            if (_animator == null || _climbLayerIndex < 0 || !_entering) return;
            var st = _animator.GetCurrentAnimatorStateInfo(_climbLayerIndex);
            if (on)
            {
                // Remember where the enter clip stands so the return continues from that exact frame
                // (the freeze event may not have fired yet when the brake starts right at slide start).
                if (st.shortNameHash == _hClimbEnter)
                    _enterResumeFixedTime = Mathf.Clamp01(st.normalizedTime) * st.length;
                if (_hasSlideBrakeState)
                    _animator.CrossFadeInFixedTime(_hClimbSlideBrake, 0.15f, _climbLayerIndex);
            }
            else if (st.shortNameHash == _hClimbSlideBrake && _hasClimbEnterState)
            {
                // Back to the slide frame; ClimbEnterSpeed is still 0 if the freeze event has fired,
                // so the clip holds that frame — otherwise it keeps playing toward the event.
                _animator.CrossFadeInFixedTime(_hClimbEnter, 0.15f, _climbLayerIndex, _enterResumeFixedTime);
            }
        }

        /// <summary>The slide ran its distance — NOTHING attaches yet: the body pins where it stopped,
        /// the hands stay animation-driven (the enter clip's remainder plays out), and the found hold
        /// is only REMEMBERED. The first input (move or Jump) runs <see cref="BeginDeferredReach"/>,
        /// which reaches from wherever the animation left the hands to these holds.</summary>
        private void AttachAfterSlide()
        {
            _sliding = false;
            _slideBraking = false;   // pose return happens in ResumeEnterAnim (brake state → enter clip)
            HidePrompt();
            StopSlideParticles();

            // Search for the RIGHT hand's hold at the RIGHT SHOULDER point — hand height (the body
            // hangs rootDownOffset below the hands; chest height picked holds too low) and half a
            // shoulder-width to the side, so the hold PAIR brackets the character symmetrically.
            // A body-centered search could pick a hold LEFT of the character for the right hand, and
            // the left hand then went a full shoulder-width further left — hands (and any later body
            // re-derivation) trailing off to the side.
            Vector3 handTarget = transform.position + Vector3.up * rootDownOffset
                                                    + transform.right * (shoulderWidth * 0.5f);
            if (!FindHoldNear(_currentSurface, handTarget, handTarget + Vector3.up * 10000f, out Vector3 rp, out Quaternion rr))
            {
                EndSlideToFall("no hold at the stop point");
                return;
            }

            _handsPending = true;
            _pendingRightPos = rp;
            _pendingRightRot = rr;
            _rHandAttached = _lHandAttached = false;   // per-limb: each hand attaches on ITS first step

            // Hold-frame bookkeeping the dormant hang still needs (pose switch, facing target): both
            // hands assume the found hold's frame until the real pair resolves at the reach.
            _rhOutward = _lhOutward = rr * Vector3.forward;
            _rhUp = _lhUp = rr * Vector3.up;

            // Pin the body where the slide stopped; only the yaw eases toward the wall facing.
            _attachPinPos = transform.position;
            _attachOffsetHold = true;
            _attachOffsetT = 1f;
            _bracedBodyRot = transform.rotation;
            _freeHangBodyRot = transform.rotation;
            StartBracedTurn();

            bool feetOnWall = SlideFeetWallCheck();
            _postSlideFreeHang = !feetOnWall;
            _freeHang = !feetOnWall || Vector3.Dot(AvgOutward(), Vector3.up) < freeHangEnterDot;
            PlayPose(_freeHang, instant: false);   // sets intent only; the enter clip's remainder owns the layer
            ResumeEnterAnim();

            _rig.SetMasterWeight(0f);
            _masterWeightTarget = 0f;   // IK stays OFF — the animation owns the hands until the reach
            _atTopDirty = true;
            _wasAnyMoving = false;

            if (logClimbEvents)
                Debug.Log($"[ClimbController] Slide stop after {_slideDistSoFar:0.00} m — pinned, hold pending " +
                          $"({(feetOnWall ? "braced" : "free hang (no wall at the feet)")}).");
        }

        /// <summary>JUMP pressed during the dormant hang: BracedReady needs BOTH hands on holds, so
        /// reach both to the remembered pair (from the live bones); the body un-pins and settles
        /// across the reach. (Move input takes the lighter per-limb path — WakeRigForTraversal.)</summary>
        private void BeginDeferredReach()
        {
            if (!_handsPending) return;
            _handsPending = false;
            AttachHands(_pendingRightPos, _pendingRightRot, reachFromCurrent: true);
            _masterWeightTarget = 1f;
            ReleaseAttachOffset(traverseMoveDuration);   // AttachHands re-pins at the same spot; release into decay
            _atTopDirty = true;
            if (logClimbEvents) Debug.Log("[ClimbController] Deferred reach — both hands attaching (Jump).");
        }

        /// <summary>First MOVE input after a slide stop: wake the rig for direct traversal — FBBIK on,
        /// hand effectors seeded on their live bones but WEIGHTLESS, so each hand keeps following the
        /// animation until its own first step attaches it (AttachHandIfLoose) and it travels straight
        /// from its animated pose to its own traversal hold. Feet seed at their bones and step in via
        /// UpdateFeet. The body pin stays until the first successful step releases it.</summary>
        private void WakeRigForTraversal()
        {
            _handsPending = false;

            if (ik != null && !ik.enabled)
            {
                if (!ik.solver.initiated) ik.GetIKSolver().Initiate(ik.transform);
                ik.enabled = true;
            }

            SeedHandEffector(true);
            SeedHandEffector(false);
            _rig.SetEffectorWeight(ClimbEffector.RightHand, 0f);   // weightless until each hand's first step
            _rig.SetEffectorWeight(ClimbEffector.LeftHand, 0f);
            _rig.SetEffectorWeight(ClimbEffector.LeftFoot, 0f);
            _rig.SetEffectorWeight(ClimbEffector.RightFoot, 0f);
            _rig.SetEffectorWeight(ClimbEffector.RootBody, 0f);
            _rHandAttached = _lHandAttached = false;

            _lFootWeight = _rFootWeight = 0f;
            _footCooldown = 0f;
            _lFootHoldIdx = _rFootHoldIdx = -1;
            _standoffPush = 0f;
            _headLookInit = false;
            _legBlend = Vector2.zero;
            _lFootLocked = _rFootLocked = false;
            _footSmoothInit[0] = _footSmoothInit[1] = false;
            _pendulumAccumulator = 0f;
            ApplyGripOffset();

            // Feet also depart from their real animated positions (weight 0 — UpdateFeet steps them in).
            Transform lfB = _animator != null && _animator.isHuman ? _animator.GetBoneTransform(HumanBodyBones.LeftFoot) : null;
            Transform rfB = _animator != null && _animator.isHuman ? _animator.GetBoneTransform(HumanBodyBones.RightFoot) : null;
            Vector3 hipW = HipPosition;
            Vector3 brW = transform.right;
            _rig.SnapToPose(ClimbEffector.LeftFoot,
                lfB != null ? lfB.position : hipW - Vector3.up * footDrop - brW * footSide, transform.rotation);
            _rig.SnapToPose(ClimbEffector.RightFoot,
                rfB != null ? rfB.position : hipW - Vector3.up * footDrop + brW * footSide, transform.rotation);

            ConfigurePendulum();
            _pendulum?.Reset(_rig.HandAverage);

            _rig.SetMasterWeight(0f);
            _masterWeightTarget = 1f;
            _atTopDirty = true;
            if (logClimbEvents) Debug.Log("[ClimbController] Rig awake — per-limb attach as each limb steps.");
        }

        /// <summary>Slide failure exit (past the holds / off the wall): fall, carrying the slide's speed.</summary>
        private void EndSlideToFall(string reason)
        {
            if (logClimbEvents) Debug.Log($"[ClimbController] Slide → fall ({reason}).");
            _motor?.SetVerticalVelocity(-Mathf.Max(_slideSpeed, 0f));
            _regrabCooldownTimer = regrabCooldown;   // don't instantly re-grab the surface just slid off
            CleanupSlide();
            FinishRelease(ClimbEndKind.Fell);   // ran out of wall — a tether catches this
        }

        private void CleanupSlide()
        {
            _sliding = false;
            _slideBraking = false;
            _enterFrozen = false;
            SetEnterSpeed(1f);
            HidePrompt();
            StopSlideParticles();
        }

        /// <summary>Starts the slide FX systems (hand sparks etc.). Re-activates GameObjects that a
        /// Stop Action = Disable turned off after the previous slide.</summary>
        private void StartSlideParticles()
        {
            if (slideParticles == null) return;
            for (int i = 0; i < slideParticles.Length; i++)
            {
                ParticleSystem ps = slideParticles[i];
                if (ps == null) continue;
                if (!ps.gameObject.activeSelf) ps.gameObject.SetActive(true);
                ps.Play(true);
            }
        }

        /// <summary>Stops EMITTING on the slide FX systems; already-live particles die out naturally
        /// (and Stop Action = Disable then turns the object off entirely for free).</summary>
        private void StopSlideParticles()
        {
            if (slideParticles == null) return;
            for (int i = 0; i < slideParticles.Length; i++)
            {
                ParticleSystem ps = slideParticles[i];
                if (ps == null || !ps.gameObject.activeInHierarchy) continue;
                ps.Stop(true, ParticleSystemStopBehavior.StopEmitting);
            }
        }

        /// <summary>True when the surface still has a hold within <paramref name="radius"/> of <paramref name="point"/>.</summary>
        private bool AnyHoldWithin(Vector3 point, float radius)
        {
            var s = _currentSurface;
            if (s == null || !s.HoldsReady) return false;
            Transform st = s.transform;
            var holds = s.Holds;
            float sqr = radius * radius;
            for (int i = 0; i < holds.Count; i++)
                if ((st.TransformPoint(holds[i].LocalPosition) - point).sqrMagnitude <= sqr)
                    return true;
            return false;
        }

        /// <summary>Wall probe at foot height — braced needs wall under the feet; hands-only = free hang.
        /// (The same root+foot two-probe lesson as the rope's rope-alone → wall-rappel transition.)</summary>
        private bool SlideFeetWallCheck()
        {
            Vector3 n = _sliding ? _slideWallNormal : AvgOutward();
            Vector3 origin = transform.position + Vector3.up * slideFeetProbeHeight + n * 0.3f;
            return Physics.Raycast(origin, -n, slideWallProbe + 0.3f, SlideProbeMask(), QueryTriggerInteraction.Ignore);
        }

        /// <summary>climbableLayers with the player's own layer stripped (never probe ourselves).</summary>
        private int SlideProbeMask() => climbableLayers & ~(1 << gameObject.layer);

        private void FaceSlideWall()
        {
            Vector3 intoFlat = Vector3.ProjectOnPlane(-_slideWallNormal, Vector3.up);
            if (intoFlat.sqrMagnitude > 1e-4f)
                transform.rotation = Quaternion.LookRotation(intoFlat.normalized, Vector3.up);
        }

        // ------------------------------------------------------------------ attach prompt

        /// <summary>Free-state prompt: marker at the current grab candidate, per-frame accurate. The
        /// climbing paths own it otherwise (slide parenting / hidden on grab).</summary>
        private void UpdateAttachPrompt()
        {
            if (_isClimbing) return;
            if (!showAttachPrompt || !_hasCandidate) { HidePrompt(); return; }

            EnsurePrompt();
            if (_promptOnCharacter)
            {
                _prompt.SetParent(null, true);
                _promptOnCharacter = false;
            }
            _prompt.gameObject.SetActive(true);
            _prompt.position = _candidatePos + (_candidateRot * Vector3.forward) * attachPromptSurfaceOffset;
            _prompt.localScale = Vector3.one * attachPromptSize;
            BillboardPrompt();
        }

        private void BillboardPrompt()
        {
            if (_prompt == null || _cam == null) return;
            Vector3 toPrompt = _prompt.position - _cam.position;
            if (toPrompt.sqrMagnitude > 1e-6f)
                _prompt.rotation = Quaternion.LookRotation(toPrompt);   // quad front (−Z) faces the camera
        }

        private void EnsurePrompt()
        {
            if (_prompt != null) return;

            GameObject go;
            if (attachPromptPrefab != null)
            {
                go = Instantiate(attachPromptPrefab);
            }
            else
            {
                // Placeholder: a plain white unlit square (billboarded); swap in the icon prefab later.
                go = GameObject.CreatePrimitive(PrimitiveType.Quad);
                Destroy(go.GetComponent<Collider>());
                var mr = go.GetComponent<MeshRenderer>();
                Shader unlit = Shader.Find("Universal Render Pipeline/Unlit");
                if (unlit != null) mr.material = new Material(unlit) { color = Color.white };
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows = false;
            }
            go.name = "ClimbAttachPrompt";
            go.SetActive(false);
            _prompt = go.transform;
        }

        private void HidePrompt()
        {
            if (_prompt == null) return;
            if (_promptOnCharacter)
            {
                _prompt.SetParent(null, true);
                _promptOnCharacter = false;
            }
            _prompt.gameObject.SetActive(false);
        }

        private void OnDestroy()
        {
            if (_prompt != null) Destroy(_prompt.gameObject);
            if (_slipPrompt != null) Destroy(_slipPrompt.gameObject);
        }

        // ------------------------------------------------------------------ enter animation

        /// <summary>Plays the ClimbEnter state on the ClimbingLayer (every grab). Falls back to the hang
        /// pose when the state isn't authored yet (Grab's PlayPose already put the pose there).</summary>
        private void PlayEnterAnim()
        {
            _entering = false;
            _enterFrozen = false;
            if (_animator == null || _climbLayerIndex < 0) return;

            if (_hasClimbEnterState)
            {
                SetEnterSpeed(1f);
                _animator.Play(_hClimbEnter, _climbLayerIndex, 0f);
                _entering = true;
            }
            else if (_sliding)
            {
                _animator.Play(_hClimbHang, _climbLayerIndex, 0f);   // no enter clip yet — show SOMETHING while sliding
            }
        }

        /// <summary>ANIMATION EVENT — place on the ClimbEnter clip at the "hanging on" frame. While
        /// sliding, the clip freezes here (the slide pose) and resumes when the slide attaches; calm
        /// grabs play straight through. A missing event just means the clip never freezes (no soft-lock).</summary>
        public void OnClimbEnterHold()
        {
            if (!_sliding || !_entering || _enterFrozen) return;
            if (_animator == null || _climbLayerIndex < 0) return;
            var st = _animator.GetCurrentAnimatorStateInfo(_climbLayerIndex);
            if (st.shortNameHash == _hClimbEnter)
                _enterResumeFixedTime = Mathf.Clamp01(st.normalizedTime) * st.length;
            _enterFrozen = true;
            SetEnterSpeed(0f);
        }

        /// <summary>Un-freezes the enter clip after the slide; a brake detour crossfades back to the
        /// clip at the frame it froze on, so the remainder plays out before the hang pose blends in.</summary>
        private void ResumeEnterAnim()
        {
            _enterFrozen = false;
            SetEnterSpeed(1f);
            if (!_entering || _animator == null || _climbLayerIndex < 0) return;
            var st = _animator.GetCurrentAnimatorStateInfo(_climbLayerIndex);
            if (st.shortNameHash == _hClimbSlideBrake && _hasClimbEnterState)
                _animator.CrossFadeInFixedTime(_hClimbEnter, 0.15f, _climbLayerIndex, _enterResumeFixedTime);
        }

        /// <summary>Polls the enter clip (calm grabs + the post-slide remainder) and blends into the
        /// proper hang pose when it completes. Any other state taking the layer (ClimbJump, ClimbUp,
        /// a pose transition) ends the tracking gracefully.</summary>
        private void TickEnterAnim()
        {
            if (!_entering || _animator == null || _climbLayerIndex < 0) return;
            if (_animator.IsInTransition(_climbLayerIndex)) return;
            var st = _animator.GetCurrentAnimatorStateInfo(_climbLayerIndex);
            if (st.shortNameHash == _hClimbEnter)
            {
                if (!_enterFrozen && st.normalizedTime >= 1f)
                {
                    _entering = false;
                    _animator.CrossFadeInFixedTime(_freeHang ? _hFreeHang : _hClimbHang, 0.2f, _climbLayerIndex);
                }
            }
            else if (st.shortNameHash != _hClimbSlideBrake)
            {
                _entering = false;
            }
        }

        private void SetEnterSpeed(float v)
        {
            if (_hasEnterSpeedParam && _animator != null) _animator.SetFloat(_hClimbEnterSpeed, v);
        }
    }
}
