using UnityEngine;

namespace Game.Climbing
{
    /// <summary>
    /// ClimbController — SLIP: the risk-paint grip roll and its slide-down QTE.
    ///
    /// Every hand step on a FREE-type surface rolls Random.value against the risk of the hold the
    /// hand is RELEASING (its painted ClimbRiskClass → the surface's green/blue/red percentage;
    /// Black resolves through the surface's Black Fallback). A failed roll aborts the step and the
    /// climber slides DOWN the wall (constant slipSpeed, capped at slipMaxDistance, wall-followed by
    /// probe exactly like the entry slide, IK fading out so the limbs ride the climb pose).
    ///
    /// THE QTE: at a random point of the slide (slipPromptRange, scheduled so the whole window fits
    /// before the max distance) the Use-icon prompt appears for slipPromptWindow seconds. Use INSIDE
    /// the window = the slide stops and control returns through the entry slide's pin + deferred
    /// per-limb attach (AttachAfterSlide — same dormant hang, move/Jump wake it). Use BEFORE the
    /// prompt, missing the window, or sliding the full distance = FALL, carrying the slip speed and
    /// arming slipReattachCooldown (longer than the normal regrab cooldown — the exposed "time of
    /// reattach"). Losing the wall or running out of holds falls too; sliding into the ground just
    /// lands. Free-hang steps also roll, but with no wall below the fall is immediate (no QTE).
    /// </summary>
    public partial class ClimbController
    {
        // ------------------------------------------------------------------ the roll

        /// <summary>Called by TryStepHand/TryStepHandArc after a target hold is found, before the
        /// step starts: rolls against the RELEASED hold's risk. True = the roll failed and the slip
        /// (or immediate free-hang fall) has taken over — the caller must not start the step.</summary>
        private bool TrySlipOnStep(bool rightHand, Vector3 handPos)
        {
            if (!enableSlip || _slipping || _sliding || _releasing || _climbJumping) return false;
            if (_currentSurface == null || _currentSurface.Type != ClimbType.Free) return false;
            if (!(rightHand ? _rHandAttached : _lHandAttached)) return false;   // a loose (post-slide) hand isn't ON a hold yet

            float risk = ReleasedHoldRisk(handPos);
            if (risk <= 0f || Random.value >= risk) return false;

            if (logClimbEvents)
                Debug.Log($"[ClimbController] Grip roll FAILED on the {(rightHand ? "right" : "left")} hand's hold " +
                          $"(risk {risk:P0}) — slipping.");
            BeginSlip();
            return true;
        }

        /// <summary>Risk (0..1) of the hold nearest the released hand — that IS the hold being left
        /// (hands sit on holds). No hold within reach of the hand = unpainted fallback risk.</summary>
        private float ReleasedHoldRisk(Vector3 handPos)
        {
            var s = _currentSurface;
            if (s == null || !s.HoldsReady) return 0f;

            Transform st = s.transform;
            var holds = s.Holds;
            int idx = -1;
            float best = 0.35f * 0.35f;   // the hand's hold is at the effector — anything farther isn't it
            for (int i = 0; i < holds.Count; i++)
            {
                float d = (st.TransformPoint(holds[i].LocalPosition) - handPos).sqrMagnitude;
                if (d < best) { best = d; idx = i; }
            }
            return idx >= 0 ? s.Risk01(idx) : ClimbRiskSettings.Instance.Risk01(ClimbRiskClass.Black);
        }

        // ------------------------------------------------------------------ the slide + QTE

        private void BeginSlip()
        {
            _slipping = true;
            _slipElapsed = 0f;
            _slipDistSoFar = 0f;
            _slipPromptShown = false;
            _slipResolved = false;
            _slideWallNormal = AvgOutward();   // shared wall-follow normal, re-probed every tick

            // Schedule the prompt so the whole window ALWAYS fits before the slide's max distance.
            float maxDur = slipSpeed > 0f ? slipMaxDistance / slipSpeed : 0f;
            float latest = Mathf.Max(0.05f, maxDur - slipPromptWindow - 0.05f);
            _slipPromptTime = Mathf.Clamp(Random.Range(slipPromptRange.x, slipPromptRange.y) * latest, 0.05f, latest);

            // No wall under the chest (free hang / overhang lip) → nothing to slide on: fall outright.
            Vector3 chest = transform.position + Vector3.up * detectHeightOffset;
            if (!Physics.Raycast(chest + _slideWallNormal * 0.5f, -_slideWallNormal, out RaycastHit hit,
                                 slideWallProbe + 0.5f, SlideProbeMask(), QueryTriggerInteraction.Ignore))
            {
                EndSlipToFall("no wall to slide on");
                return;
            }
            _slideWallNormal = hit.normal;

            // The limbs leave their holds onto the animated climb pose: IK fades out in TickSlip,
            // the pose layer stays up via the floor (same trick as the entry slide's attach).
            _masterWeightTarget = 0f;
            _enterLayerFloor = 1f;
            _attachOffsetHold = false;   // any live slide-attach pin dies — the body is moving again
            _attachOffsetT = 1f;
            _handsPending = false;

            StartSlideParticles();
        }

        /// <summary>Per-frame slip: IK fade-out, QTE timeline, wall-follow descent, hold/ground checks.</summary>
        private void TickSlip(float dt)
        {
            // Hands blend off the holds onto the climb pose (fast fade, not a pop).
            _rig.SetMasterWeight(Mathf.MoveTowards(_rig.MasterWeight, 0f, dt / 0.12f));
            _rig.Tick(dt);
            if (_animator != null && _climbLayerIndex >= 0)
                _animator.SetLayerWeight(_climbLayerIndex, 1f);

            _slipElapsed += dt;

            // QTE timeline. (Use presses arrive via OnUseInput → OnSlipUsePressed.)
            if (!_slipPromptShown && _slipElapsed >= _slipPromptTime)
            {
                _slipPromptShown = true;
                ShowSlipPrompt();
            }
            if (_slipPromptShown && !_slipResolved && _slipElapsed > _slipPromptTime + slipPromptWindow)
            {
                EndSlipToFall("recovery window missed");
                return;
            }
            if (_slipPrompt != null && _slipPrompt.gameObject.activeSelf) BillboardSlipPrompt();

            // Descend along the wall plane, then re-stick by probe (the entry slide's follow).
            float delta = Mathf.Min(slipSpeed * dt, slipMaxDistance - _slipDistSoFar);
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
            else
            {
                EndSlipToFall("wall lost");   // slid past the surface's lower edge / an overhang lip
                return;
            }

            if (!AnyHoldWithin(pos + Vector3.up * detectHeightOffset, slideHoldRadius))
            {
                EndSlipToFall("holds ran out");
                return;
            }

            transform.position = pos;
            FaceSlideWall();
            _slipDistSoFar += delta;

            // Slid into the ground → just land (no fall, no cooldown — mirrors the entry slide).
            if (Physics.Raycast(transform.position + Vector3.up * 0.1f, Vector3.down,
                                reachBottomDistance + 0.1f, mantleSurfaceMask, QueryTriggerInteraction.Ignore))
            {
                if (logClimbEvents) Debug.Log("[ClimbController] Slip reached the ground — stepping off.");
                _motor?.SetVerticalVelocity(0f);
                StopSlideParticles();
                FinishRelease(ClimbEndKind.Landed);
                return;
            }

            if (_slipDistSoFar >= slipMaxDistance)
                EndSlipToFall("slid the full distance");   // safety — the window check normally fires first
        }

        /// <summary>Use pressed while slipping (a performed callback is always a fresh press —
        /// a button still held from before the slip never re-fires).</summary>
        private void OnSlipUsePressed()
        {
            if (_slipResolved) return;
            if (!_slipPromptShown)
            {
                _slipResolved = true;
                EndSlipToFall("Use pressed before the prompt");
                return;
            }
            // Prompt visible and inside the window (the window-expiry fall runs in TickSlip).
            _slipResolved = true;
            RecoverFromSlip();
        }

        /// <summary>QTE hit: stop where we are and re-enter control through the entry slide's
        /// pin + deferred per-limb attach (dormant hang; move/Jump wake it — dev-verified feel).</summary>
        private void RecoverFromSlip()
        {
            if (logClimbEvents)
                Debug.Log($"[ClimbController] Slip recovery at {_slipDistSoFar:0.00} m — back in control.");
            HideSlipPrompt();
            _slipping = false;
            _slideDistSoFar = _slipDistSoFar;   // the shared attach/fall paths read the slide fields
            _slideSpeed = slipSpeed;            // carried by EndSlideToFall if the attach finds no hold
            AttachAfterSlide();
            if (!_isClimbing)                   // the stop point had no hold — that recovery IS a slip fall
                _regrabCooldownTimer = slipReattachCooldown;
        }

        /// <summary>Slip failure exit: fall carrying the slip speed, with the slip's own (longer)
        /// reattach cooldown armed.</summary>
        private void EndSlipToFall(string reason)
        {
            if (logClimbEvents) Debug.Log($"[ClimbController] Slip → fall ({reason}).");
            HideSlipPrompt();
            StopSlideParticles();
            _slipping = false;
            _motor?.SetVerticalVelocity(-slipSpeed);
            FinishRelease(ClimbEndKind.Fell);   // the grip failed — a tether catches this

            _regrabCooldownTimer = slipReattachCooldown;   // after FinishRelease — nothing may shorten it
        }

        // ------------------------------------------------------------------ prompt

        private void ShowSlipPrompt()
        {
            if (_slipPrompt == null)
            {
                GameObject go;
                if (slipPromptPrefab != null)
                {
                    go = Instantiate(slipPromptPrefab);
                }
                else
                {
                    // Placeholder: plain white unlit square (same as the attach prompt's placeholder).
                    go = GameObject.CreatePrimitive(PrimitiveType.Quad);
                    Destroy(go.GetComponent<Collider>());
                    var mr = go.GetComponent<MeshRenderer>();
                    Shader unlit = Shader.Find("Universal Render Pipeline/Unlit");
                    if (unlit != null) mr.material = new Material(unlit) { color = Color.white };
                    mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    mr.receiveShadows = false;
                }
                go.name = "ClimbSlipPrompt";
                _slipPrompt = go.transform;
            }
            _slipPrompt.SetParent(transform, false);
            _slipPrompt.localPosition = slipPromptOffset;
            _slipPrompt.localScale = Vector3.one * slipPromptSize;
            _slipPrompt.gameObject.SetActive(true);
            BillboardSlipPrompt();
        }

        private void BillboardSlipPrompt()
        {
            if (_slipPrompt == null || _cam == null) return;
            Vector3 to = _slipPrompt.position - _cam.position;
            if (to.sqrMagnitude > 1e-6f)
                _slipPrompt.rotation = Quaternion.LookRotation(to);   // quad front (−Z) faces the camera
        }

        private void HideSlipPrompt()
        {
            if (_slipPrompt == null) return;
            _slipPrompt.SetParent(null, true);
            _slipPrompt.gameObject.SetActive(false);
        }
    }
}
