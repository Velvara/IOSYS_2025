using UnityEngine;
using System.Collections;

[RequireComponent(typeof(Rigidbody), typeof(Collider))]
public class HookshotTip : MonoBehaviour
{
    private ShootAim shootAim;
    private Hookshot hookshot;
    private HookshotRope activeRope;
    private UVScroller scroller;

    [HideInInspector] public bool HasCollided = false;

    private Rigidbody rb;
    private Vector3 targetPoint;
    private bool isReturning = false;
    public float failsafeSecs = 5f;
    public bool finishedReturning;

    // Called immediately after tip is spawned
    public void Init(ShootAim aim, Hookshot sourceHookshot, Vector3 targetPos)
    {
        finishedReturning = false;
        shootAim = aim;
        hookshot = sourceHookshot;
        targetPoint = targetPos;

        rb = GetComponent<Rigidbody>();
        if (rb == null) rb = gameObject.AddComponent<Rigidbody>();

        rb.useGravity = false;
        rb.isKinematic = false;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

        // Start movement coroutine
        StartCoroutine(MoveToTarget());

        StartCoroutine(FailsafeTimeout());
    }

    private IEnumerator MoveToTarget()
    {
        while (!HasCollided && !isReturning)
        {
            rb.MovePosition(Vector3.MoveTowards(rb.position, targetPoint, hookshot.hookshotSpeed * Time.fixedDeltaTime));

            // If reached max distance without collision, return
            if (Vector3.Distance(rb.position, targetPoint) < 0.05f)
            {
                StartReturn();
                yield break;
            }

            yield return new WaitForFixedUpdate();
        }
    }

    private void OnCollisionEnter(Collision collision)
    {
        if (HasCollided) return;
        HasCollided = true;

        activeRope.OnHookshotHit();

        bool valid = shootAim.CheckTargetValidity(collision.collider.gameObject);

        if (valid)
        {
            //Debug.Log("HookshotTip: Hit valid target");

            // Move forward slightly for depth
            transform.position += transform.forward * hookshot.tipAttachDepth;

            // Freeze tip in place
            rb.isKinematic = true;

            // ---- Start animator / IK drag in AimManager ----
            if (AimManager.Instance != null)
                AimManager.Instance.StartHookshotDrag(this.transform);

            // ---- Start movement drag in the HookshotDragMode instance ----
            HookshotDragMode dragMode = AimManager.Instance.hookshotDragMode;
            if (dragMode != null)
            {
                dragMode.BeginDrag(hookshot, this.transform); // pass tip's Transform
                AimManager.Instance.SetActiveMode(dragMode);
            }
        }

        else
        {
            //Debug.Log("HookshotTip: Hit invalid target");

            if (hookshot.invalidFX != null)
                Instantiate(hookshot.invalidFX, transform.position, Quaternion.identity);

            StartReturn();
        }
    }

    public void AttachRope(HookshotRope rope)
    {
        activeRope = rope;
        scroller = rope.GetComponent<UVScroller>();
    }

    public void StartReturn()
    {
        if (isReturning) return;
        isReturning = true;
        HasCollided = true;
        rb.isKinematic = false; // allow movement

        StartCoroutine(ReturnToHook());
    }

    private IEnumerator ReturnToHook()
    {
        Rigidbody rb = GetComponent<Rigidbody>();
        if (rb == null) yield break; // Safety check

        while (Vector3.Distance(transform.position, hookshot.hookSlot.position) > 0.05f)
        {
            rb.MovePosition(Vector3.MoveTowards(transform.position, hookshot.hookSlot.position, hookshot.hookReturnSpeed * Time.fixedDeltaTime));
            yield return new WaitForFixedUpdate();
        }

        // STOP animator/IK drag
        if (AimManager.Instance != null)
        {
            AimManager.Instance.StopHookshotDrag();
            AimManager.Instance.characterStateManager?.UnlockCharacter();
        }

        // Reactivate dummy tip
        if (hookshot.dummyTip != null)
            hookshot.dummyTip.gameObject.SetActive(true);

        // Allow ShootAim to fire again
        //shootAim.NotifyHookshotReturned();

        finishedReturning = true;

        Destroy(gameObject);

        if (activeRope != null)
            Destroy(activeRope.gameObject);
    }

    private IEnumerator FailsafeTimeout()
    {
        if (hookshot == null) yield break;

        // 1. time to reach max distance
        float travelSecs = hookshot.shotDistance / Mathf.Max(hookshot.hookshotSpeed, 0.01f);

        // 2. time to return
        float returnSecs = hookshot.shotDistance / Mathf.Max(hookshot.hookReturnSpeed, 0.01f);

        // 3. time to drag player
        float dragSecs = hookshot.shotDistance / Mathf.Max(hookshot.dragSpeed, 0.01f);

        // 4. choose timeout
        float seconds = travelSecs + Mathf.Max(returnSecs, dragSecs) + failsafeSecs;

        yield return new WaitForSeconds(seconds);

        if (!finishedReturning)
        {
            Debug.LogWarning($"HookshotTip failsafe triggered after {seconds:F2} seconds.");
            Destroy(gameObject);
        }
    }

    private void OnDestroy()
    {
        if (shootAim != null)
        {
            shootAim.NotifyHookshotReturned();
        }
    }
}


