using System.Collections.Generic;
using UnityEngine;

namespace TikTokLiveGame
{
    [RequireComponent(typeof(Camera))]
    public sealed class ClubCameraController : MonoBehaviour
    {
        private readonly Vector3 defaultPosition = new(0f, 9.2f, 17.2f);
        private readonly Vector3 focusWideBasePosition = new(0f, 7.2f, 14.2f);
        private readonly Vector3 defaultLookAt = new(0f, 1.25f, -1.2f);
        private Transform focusTarget;
        private float focusUntil;
        private bool focusVip;
        private bool focusWide;
        private bool focusFireworks;
        private bool focusCrowdWelcome;
        private bool focusNpc;
        private const float NpcFocusSeconds = 3f;
        private const float NpcFocusGap = 8f;
        private float nextNpcFocusAt;
        private int npcFocusCursor;
        private float focusStartedAt;
        private float focusDuration;
        private readonly Queue<WelcomeRequest> welcomeQueue = new();
        private bool welcomeOverflow;
        private Vector3 currentLookAt;
        private int directorShot;
        private int directorCycle;
        private float directorShotStartBeat;
        private bool wasFocusing;
        private PlayerManager playerManager;
        private bool HasActiveFocus => Time.time < focusUntil && (focusTarget != null || focusCrowdWelcome);

        private void Awake()
        {
            currentLookAt = defaultLookAt;
            directorShotStartBeat = ClubBeatClock.Beat;
            playerManager = FindFirstObjectByType<PlayerManager>();
            nextNpcFocusAt = Time.time + NpcFocusGap;
        }

        public void Focus(PlayerActor actor, float seconds, bool vip, bool wide = false)
        {
            if (actor == null || !actor.gameObject.activeInHierarchy) return;
            if (actor.IsNpc && (HasActiveFocus || welcomeQueue.Count > 0 || welcomeOverflow)) return;
            focusNpc = actor.IsNpc;
            focusTarget = actor.transform;
            focusUntil = Time.time + Mathf.Clamp(seconds, 2f, 12f);
            focusVip = vip;
            focusWide = wide;
            focusFireworks = false;
            focusCrowdWelcome = false;
            focusStartedAt = Time.time;
            focusDuration = Mathf.Clamp(seconds, 2f, 12f);
            nextNpcFocusAt = focusUntil + NpcFocusGap;
        }

        public void FocusFireworks(PlayerActor actor, float seconds = 6f)
        {
            if (actor == null || actor.IsNpc) return;
            focusNpc = false;
            focusTarget = actor.transform;
            focusDuration = Mathf.Clamp(seconds, 5.5f, 8f);
            focusStartedAt = Time.time;
            focusUntil = focusStartedAt + focusDuration;
            focusVip = false;
            focusWide = false;
            focusFireworks = true;
            focusCrowdWelcome = false;
            nextNpcFocusAt = focusUntil + NpcFocusGap;
        }

        public void QueueWelcome(PlayerActor actor, float seconds = 2f)
        {
            if (actor == null || actor.IsNpc) return;
            if (HasActiveFocus && !focusNpc && focusTarget == actor.transform) return;
            foreach (WelcomeRequest queued in welcomeQueue)
                if (queued.Actor == actor) return;
            WelcomeRequest request = new(actor, Mathf.Clamp(seconds, 2f, 3f));
            if ((!HasActiveFocus || focusNpc) && welcomeQueue.Count == 0 && !welcomeOverflow)
            {
                StartWelcome(request);
                return;
            }
            if (welcomeQueue.Count < 3) welcomeQueue.Enqueue(request);
            else welcomeOverflow = true;
        }

        private void StartWelcome(WelcomeRequest request)
        {
            if (request.Actor == null || request.Actor.IsNpc) return;
            focusNpc = false;
            focusTarget = request.Actor.transform;
            focusDuration = request.Seconds;
            focusStartedAt = Time.time;
            focusUntil = focusStartedAt + focusDuration;
            focusVip = false;
            focusWide = false;
            focusFireworks = false;
            focusCrowdWelcome = false;
            nextNpcFocusAt = focusUntil + NpcFocusGap;
        }

        private void StartCrowdWelcome()
        {
            if (!TryGetViewerBounds(out _)) return;
            focusNpc = false;
            focusTarget = null;
            focusDuration = 2.5f;
            focusStartedAt = Time.time;
            focusUntil = focusStartedAt + focusDuration;
            focusVip = false;
            focusWide = false;
            focusFireworks = false;
            focusCrowdWelcome = true;
            nextNpcFocusAt = focusUntil + NpcFocusGap;
        }

        private void StartNextWelcomeIfReady()
        {
            if (HasActiveFocus && !focusNpc) return;
            while (welcomeQueue.Count > 0)
            {
                WelcomeRequest request = welcomeQueue.Dequeue();
                if (request.Actor == null || request.Actor.IsNpc) continue;
                StartWelcome(request);
                return;
            }
            if (!welcomeOverflow) return;
            welcomeOverflow = false;
            StartCrowdWelcome();
        }

        private void LateUpdate()
        {
            StartNextWelcomeIfReady();
            TryStartNpcFocus();
            bool focusing = HasActiveFocus;
            Vector3 desiredPosition = defaultPosition;
            Vector3 desiredLookAt = defaultLookAt;
            float desiredFov = 45f;
            float handheldPitch = 0f;
            float handheldYaw = 0f;
            float handheldRoll = 0f;
            if (focusing)
            {
                wasFocusing = true;
                if (focusCrowdWelcome)
                {
                    if (TryGetViewerBounds(out Bounds crowdBounds))
                    {
                        desiredPosition = new Vector3(crowdBounds.center.x, 10.5f, crowdBounds.max.z + 13.5f);
                        desiredLookAt = crowdBounds.center + Vector3.up * 1.1f;
                        desiredFov = 50f;
                    }
                    else
                    {
                        focusCrowdWelcome = false;
                        focusUntil = 0f;
                    }
                }
                else
                {
                Vector3 target = focusTarget.position + Vector3.up * 1.1f;
                if (focusFireworks)
                {
                    float elapsed = Time.time - focusStartedAt;
                    float reveal = Smooth(Mathf.InverseLerp(1.15f, 2.05f, elapsed));
                    Vector3 closePosition = target + new Vector3(0f, 3.2f, 7.25f);
                    Vector3 widePosition = new(target.x * 0.15f, 8.05f, 17.1f);
                    Vector3 closeLookAt = target + Vector3.up * 0.35f;
                    Vector3 wideLookAt = new(target.x * 0.24f, 3.15f, -2.25f);
                    desiredPosition = Vector3.Lerp(closePosition, widePosition, reveal);
                    desiredLookAt = Vector3.Lerp(closeLookAt, wideLookAt, reveal);
                    desiredFov = Mathf.Lerp(34f, 54f, reveal);
                }
                else if (focusWide)
                {
                    // Move closer and follow only part of the target's motion.
                    // The gift sender stays prominent while visibly crossing the stage.
                    desiredPosition = focusWideBasePosition + new Vector3(target.x * 0.55f, 0.05f, -2.2f);
                    desiredLookAt = Vector3.Lerp(defaultLookAt, target + Vector3.up * 0.2f, 0.78f);
                    desiredFov = 34f;
                }
                else
                {
                    desiredPosition = target + (focusVip
                        ? new Vector3(0f, 3f, 6.4f)
                        : new Vector3(0f, 3.25f, 7.2f));
                    desiredLookAt = target + Vector3.up * 0.35f;
                    desiredFov = focusVip ? 31f : 34f;
                }
                }
            }
            else
            {
                float beat = ClubBeatClock.Beat;
                if (wasFocusing)
                {
                    // Return to the stable establishing shot after every focus.
                    // Jumping directly from a close-up to the overhead crane was
                    // visually tiring during a busy live session.
                    directorShot = 0;
                    directorShotStartBeat = beat;
                    wasFocusing = false;
                }

                if (TryGetAmbientBounds(out Bounds crowdBounds))
                {
                    float shotBeats = DirectorShotBars(directorShot) * 4f;
                    while (beat - directorShotStartBeat >= shotBeats)
                    {
                        directorShotStartBeat += shotBeats;
                        directorShot++;
                        if (directorShot >= 8)
                        {
                            directorShot = 0;
                            directorCycle++;
                        }
                        shotBeats = DirectorShotBars(directorShot) * 4f;
                    }

                    float progress = Mathf.Clamp01((beat - directorShotStartBeat) / Mathf.Max(1f, shotBeats));
                    ConfigureDirectorShot(directorShot, directorCycle, progress, crowdBounds, ref desiredPosition, ref desiredLookAt, ref desiredFov);
                }
                else
                {
                    // Keep an empty floor on the establishing shot.
                    directorShot = 0;
                    directorShotStartBeat = beat;
                }

                // No handheld shake in comfort mode. The dancers and lights
                // already provide enough motion for a lively frame.
            }
            float responsiveness = focusing ? 3.8f : 2.25f;
            float blend = 1f - Mathf.Exp(-responsiveness * Time.deltaTime);
            transform.position = Vector3.Lerp(transform.position, desiredPosition, blend);
            currentLookAt = Vector3.Lerp(currentLookAt, desiredLookAt, blend);
            Quaternion targetRotation = Quaternion.LookRotation(currentLookAt - transform.position) *
                Quaternion.Euler(handheldPitch, handheldYaw, handheldRoll);
            transform.rotation = Quaternion.Slerp(transform.rotation, targetRotation, blend);
            Camera camera = GetComponent<Camera>();
            camera.fieldOfView = Mathf.Lerp(camera.fieldOfView, desiredFov, blend);
        }

        private bool TryGetViewerBounds(out Bounds bounds)
        {
            if (playerManager == null) playerManager = FindFirstObjectByType<PlayerManager>();
            bounds = default;
            return playerManager != null && playerManager.TryGetViewerBounds(out bounds);
        }

        private bool TryGetAmbientBounds(out Bounds bounds)
        {
            if (playerManager == null) playerManager = FindFirstObjectByType<PlayerManager>();
            bounds = default;
            return playerManager != null && playerManager.TryGetCrowdBounds(out bounds);
        }

        private void TryStartNpcFocus()
        {
            if (HasActiveFocus || Time.time < nextNpcFocusAt || welcomeQueue.Count > 0 || welcomeOverflow) return;
            if (playerManager == null) playerManager = FindFirstObjectByType<PlayerManager>();
            PlayerActor npc = playerManager?.NextNpcForCamera(ref npcFocusCursor);
            if (npc != null) Focus(npc, NpcFocusSeconds, false);
        }

        private static float DirectorShotBars(int shot)
        {
            if (shot == 5) return 4f;
            if (shot == 6) return 4.5f;
            return 3.5f;
        }

        private static float Smooth(float value) => value * value * (3f - 2f * value);

        private readonly struct WelcomeRequest
        {
            public PlayerActor Actor { get; }
            public float Seconds { get; }

            public WelcomeRequest(PlayerActor actor, float seconds)
            {
                Actor = actor;
                Seconds = seconds;
            }
        }

        private static void ConfigureDirectorShot(
            int shot,
            int cycle,
            float progress,
            Bounds crowd,
            ref Vector3 position,
            ref Vector3 lookAt,
            ref float fov)
        {
            float t = Smooth(progress);
            float side = cycle % 2 == 0 ? -1f : 1f;
            float centerX = crowd.center.x;
            // Fit the shot to the crowd supplied by the caller.
            float halfWidth = crowd.extents.x + 0.45f;
            float left = centerX - halfWidth;
            float right = centerX + halfWidth;
            float front = crowd.max.z;
            float back = crowd.min.z;
            if (front - back < 1.2f) back = front - 0.8f;
            float middle = (front + back) * 0.5f;
            float sweepX = Mathf.Lerp(left, right, side < 0f ? t : 1f - t);
            float mirroredSweepX = centerX * 2f - sweepX;
            float gentleSweepX = Mathf.Lerp(centerX, sweepX, 0.62f);
            float gentleMirroredSweepX = Mathf.Lerp(centerX, mirroredSweepX, 0.62f);
            float cameraFront = front + 9.5f;
            switch (shot)
            {
                case 0: // Establish the crowd before scanning its rows.
                    position = Vector3.Lerp(new Vector3(centerX, 9.4f, cameraFront + 2.2f), new Vector3(centerX, 8.7f, cameraFront + 1.2f), t);
                    lookAt = new Vector3(centerX, 0.95f, middle - 0.25f);
                    fov = Mathf.Lerp(54f, 50f, t);
                    break;
                case 1: // Sweep every dancer across the front rows.
                    position = new Vector3(gentleSweepX * 0.58f, 5.1f, cameraFront - 0.25f);
                    lookAt = new Vector3(gentleSweepX, 1.15f, Mathf.Lerp(front, middle, 0.2f));
                    fov = 43f;
                    break;
                case 2: // Sweep the middle rows from the opposite direction.
                    position = new Vector3(gentleMirroredSweepX * 0.56f, 5.8f, cameraFront + 0.2f);
                    lookAt = new Vector3(gentleMirroredSweepX, 1.1f, middle);
                    fov = 44f;
                    break;
                case 3: // Higher scan dedicated to dancers in the back rows.
                    position = new Vector3(gentleSweepX * 0.46f, 7.5f, cameraFront + 1.1f);
                    lookAt = new Vector3(gentleSweepX, 1.05f, back + 0.35f);
                    fov = 45f;
                    break;
                case 4: // Track across the fixed Top 1-2-3 lineup, not the DJ.
                    if (back < -5f)
                    {
                        // Floor viewers must not pull this shot away from the podium.
                        float podiumCenterX = front < -5f ? centerX : 0f;
                        position = Vector3.Lerp(
                            new Vector3(podiumCenterX - side * 2.4f, 4.85f, 7.65f),
                            new Vector3(podiumCenterX + side * 2.4f, 4.55f, 6.75f),
                            t
                        );
                        lookAt = new Vector3(podiumCenterX, 2.05f, -6.05f);
                        fov = Mathf.Lerp(47f, 45f, t);
                    }
                    else
                    {
                        position = new Vector3(Mathf.Lerp(centerX, mirroredSweepX, 0.72f), 4.9f, cameraFront - 0.45f);
                        lookAt = new Vector3(mirroredSweepX, 1.15f, Mathf.Lerp(front, back, 0.72f));
                        fov = 41f;
                    }
                    break;
                case 5: // Gentle same-side dolly; never crosses the whole floor.
                    position = Vector3.Lerp(
                        new Vector3(centerX + side * (halfWidth + 1.8f), 5.8f, front + 9.1f),
                        new Vector3(centerX + side * (halfWidth * 0.58f + 1.5f), 6.3f, front + 10.1f),
                        t);
                    lookAt = new Vector3(
                        centerX + side * Mathf.Lerp(halfWidth * 0.24f, -halfWidth * 0.12f, t),
                        1.2f,
                        middle);
                    fov = Mathf.Lerp(47f, 45f, t);
                    break;
                case 6: // Title Reveal shot: Camera lowers and tilts up to show the background text
                    position = Vector3.Lerp(
                        new Vector3(centerX, 5.5f, cameraFront + 1.0f),
                        new Vector3(centerX + side * 1.2f, 3.5f, cameraFront - 1.5f),
                        t);
                    lookAt = Vector3.Lerp(
                        new Vector3(centerX, 1.5f, middle),
                        new Vector3(centerX - side * 0.5f, 1.5f, -12.5f), // Giữ nguyên trục Y, không ngước lên để tránh vượt trần
                        t);
                    fov = Mathf.Lerp(46f, 48f, t); // Giảm FOV để khung hình tập trung hơn, không bị lòi mép đen
                    break;
                default: // Finish on the opposite half so no side is favored.
                    position = Vector3.Lerp(
                        new Vector3(centerX - side * (halfWidth + 0.8f), 5.15f, front + 8.4f),
                        new Vector3(centerX - side * 1.8f, 4.75f, front + 7.4f),
                        t);
                    lookAt = new Vector3(centerX - side * halfWidth * 0.42f, 1.3f, middle + 0.35f);
                    fov = Mathf.Lerp(46f, 42f, t);
                    break;
            }
        }

    }
}
