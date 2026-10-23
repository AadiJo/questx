using System;
using System.Collections.Generic;
using System.Threading;
using Meta.XR;
using QuestX.Utils;
using UnityEngine;

namespace QuestX.Camera
{
    /// <summary>
    /// Priority of a camera resource request. AprilTag detection passes
    /// <see cref="High"/>; the passthrough video stream passes <see cref="Low"/>.
    /// </summary>
    public enum CameraRequestPriority
    {
        Low,
        High,
    }

    /// <summary>
    /// Single owner of <c>PassthroughCameraAccess.enabled</c> and
    /// <see cref="PassthroughCameraAccess.RequestedResolution"/>. Multiple subsystems
    /// (AprilTag detector, passthrough MJPEG stream) request a desired resolution
    /// through this arbiter; the arbiter applies the highest-priority winner and
    /// notifies subscribers when the effective resolution changes.
    ///
    /// AprilTag detection wins ties. The passthrough stream re-encodes whatever
    /// frames the camera produces.
    ///
    /// All Meta SDK calls are wrapped in try/catch with explicit logging so that
    /// SDK regressions surface as warnings instead of taking down the Unity app.
    /// </summary>
    public class CameraResourceManager
    {
        /// <summary>
        /// The Meta SDK camera object whose lifetime we manage.
        /// </summary>
        private readonly PassthroughCameraAccess cameraAccess;

        /// <summary>
        /// Captured at construction so we can marshal Meta SDK calls onto the Unity main thread.
        /// </summary>
        private readonly SynchronizationContext mainThreadContext;

        /// <summary>
        /// Active reservations keyed by requester id.
        /// </summary>
        private readonly Dictionary<string, Reservation> reservations =
            new Dictionary<string, Reservation>();

        /// <summary>
        /// The resolution most recently applied to the camera; null if the camera is currently disabled.
        /// </summary>
        private Vector2Int? appliedResolution;

        /// <summary>
        /// The resolution selected by the current reservations. This is tracked separately
        /// from <see cref="appliedResolution"/> because applying it may be queued to the
        /// Unity main thread.
        /// </summary>
        private Vector2Int? desiredResolution;

        /// <summary>
        /// The camera <see cref="cameraAccess"/> opens on its next apply.
        /// </summary>
        private PassthroughCameraAccess.CameraPositionType desiredPosition;

        /// <summary>
        /// Monotonically increasing token used to invalidate queued main-thread callbacks
        /// after the reservation set changes.
        /// </summary>
        private long applyGeneration;

        /// <summary>
        /// Whether a callback for the current <see cref="applyGeneration"/> is queued.
        /// </summary>
        private bool applyScheduled;

        /// <summary>
        /// Lock object for thread-safe access to <see cref="reservations"/> and <see cref="appliedResolution"/>.
        /// </summary>
        private readonly object stateLock = new object();

        /// <summary>
        /// Raised on the main thread when the effective resolution changes (or the
        /// camera is enabled/disabled). Argument is null when the camera was disabled.
        /// </summary>
        public event Action<Vector2Int?> OnResolutionChanged;

        /// <summary>
        /// Creates a new camera arbiter. Captures the current
        /// <see cref="SynchronizationContext"/> for main-thread marshalling.
        /// </summary>
        /// <param name="cameraAccess">The Meta SDK camera reference to manage.</param>
        public CameraResourceManager(PassthroughCameraAccess cameraAccess)
        {
            this.cameraAccess = cameraAccess;
            mainThreadContext = SynchronizationContext.Current;
            desiredPosition = cameraAccess.CameraPosition;
        }

        /// <summary>
        /// Chooses which physical camera <see cref="PassthroughCameraAccess"/> opens. The
        /// AprilTag detector's own Camera2 client takes the left camera and moves Meta's access
        /// to the right one, since Camera2 gives each camera to one client at a time. Reopens
        /// the camera on the new side if a reservation holds it open; otherwise closes it.
        /// Main thread only.
        /// </summary>
        public void SetCameraPosition(PassthroughCameraAccess.CameraPositionType position)
        {
            bool reopen;
            lock (stateLock)
            {
                desiredPosition = position;
                reopen = reservations.Count > 0;
                if (!reopen)
                {
                    // Nobody needs Meta's camera, so it can close now (the scene starts it).
                    if (cameraAccess.enabled && cameraAccess.CameraPosition != position)
                    {
                        cameraAccess.enabled = false;
                    }
                    if (!cameraAccess.enabled)
                    {
                        cameraAccess.CameraPosition = position;
                    }
                }
            }
            if (reopen && cameraAccess.CameraPosition != position)
            {
                ReapplyCurrentResolution();
            }
        }

        /// <summary>
        /// Whether <see cref="PassthroughCameraAccess"/> has the camera at
        /// <paramref name="position"/> open, or may still open it from a queued apply.
        /// Main thread only.
        /// </summary>
        public bool IsUsingCamera(PassthroughCameraAccess.CameraPositionType position)
        {
            lock (stateLock)
            {
                return (cameraAccess.enabled && cameraAccess.CameraPosition == position)
                    || (
                        applyScheduled && desiredResolution.HasValue && desiredPosition == position
                    );
            }
        }

        /// <summary>
        /// Returns the resolution currently being applied to the camera, or null
        /// if the camera is disabled (no active reservations).
        /// </summary>
        public Vector2Int? EffectiveResolution
        {
            get
            {
                lock (stateLock)
                {
                    return appliedResolution;
                }
            }
        }

        /// <summary>
        /// Returns true when at least one active reservation has
        /// <see cref="CameraRequestPriority.High"/> priority. Used by the web UI
        /// to display a "Locked by AprilTag" badge in the Camera tab.
        /// </summary>
        public bool IsLockedByHighPriority
        {
            get
            {
                lock (stateLock)
                {
                    foreach (var r in reservations.Values)
                    {
                        if (r.Priority == CameraRequestPriority.High)
                            return true;
                    }
                    return false;
                }
            }
        }

        /// <summary>
        /// Reserve the camera with the given resolution and priority. Returns the
        /// resolution actually applied; this may differ from <paramref name="resolution"/>
        /// if a higher-priority requester has already pinned a different resolution.
        ///
        /// Re-calling <see cref="Reserve"/> with the same <paramref name="requesterId"/>
        /// updates the existing reservation (use this to change resolution while remaining
        /// reserved).
        /// </summary>
        public Vector2Int Reserve(
            string requesterId,
            Vector2Int resolution,
            CameraRequestPriority priority
        )
        {
            Vector2Int winner;
            bool shouldApply;
            long generation;
            lock (stateLock)
            {
                reservations[requesterId] = new Reservation(resolution, priority);
                winner = ResolveWinner();
                shouldApply = QueueDesiredResolutionLocked(winner, out generation);
            }

            if (shouldApply)
            {
                ApplyResolution(winner, generation);
            }
            return winner;
        }

        /// <summary>
        /// Release a previously made reservation. The arbiter recomputes the winning
        /// resolution; if no reservations remain, the camera is disabled.
        /// </summary>
        public void Release(string requesterId)
        {
            Vector2Int? winner;
            bool shouldApply;
            long generation;
            lock (stateLock)
            {
                if (!reservations.Remove(requesterId))
                {
                    return;
                }
                if (reservations.Count == 0)
                {
                    winner = null;
                }
                else
                {
                    winner = ResolveWinner();
                }
                shouldApply = QueueDesiredResolutionLocked(winner, out generation);
            }

            if (shouldApply)
            {
                ApplyResolution(winner, generation);
            }
        }

        /// <summary>
        /// Reapplies the resolution selected by the current reservations. This is a
        /// recovery hook for consumers that have stopped receiving frames after a
        /// transient camera failure. The highest-priority reservation is resolved again,
        /// so a low-priority caller cannot displace an active AprilTag reservation.
        /// </summary>
        public void ReapplyCurrentResolution()
        {
            Vector2Int winner;
            long generation;
            lock (stateLock)
            {
                if (reservations.Count == 0)
                {
                    return;
                }

                winner = ResolveWinner();
                desiredResolution = winner;
                generation = ++applyGeneration;
                applyScheduled = true;
            }

            ApplyResolution(winner, generation);
        }

        /// <summary>
        /// Updates the desired camera state and decides whether a new apply callback is
        /// required. Must be called while holding <see cref="stateLock"/>.
        /// </summary>
        private bool QueueDesiredResolutionLocked(Vector2Int? target, out long generation)
        {
            bool desiredChanged = desiredResolution != target;
            if (desiredChanged)
            {
                desiredResolution = target;
                applyGeneration++;
            }

            // A newer reservation can restore the already-applied resolution before an
            // older callback runs. Invalidating that callback is sufficient; no camera
            // bounce is needed.
            if (appliedResolution == target)
            {
                applyScheduled = false;
                generation = applyGeneration;
                return false;
            }

            if (applyScheduled && !desiredChanged)
            {
                generation = applyGeneration;
                return false;
            }

            // Increment on retry as well as on a changed target so a failed/stale callback
            // can never be mistaken for this request.
            if (!desiredChanged)
            {
                applyGeneration++;
            }

            applyScheduled = true;
            generation = applyGeneration;
            return true;
        }

        /// <summary>
        /// Picks the winning reservation: highest priority wins. On ties, the most
        /// recently inserted entry wins (Dictionary preserves insertion order).
        /// </summary>
        private Vector2Int ResolveWinner()
        {
            CameraRequestPriority bestPriority = CameraRequestPriority.Low;
            Vector2Int best = default;
            bool found = false;
            foreach (var r in reservations.Values)
            {
                if (!found || r.Priority > bestPriority)
                {
                    bestPriority = r.Priority;
                    best = r.Resolution;
                    found = true;
                }
            }
            return best;
        }

        /// <summary>
        /// Applies a new effective resolution to the camera (or disables it when null).
        /// Marshalled to the main thread; SDK calls are wrapped in try/catch so a
        /// failure logs but does not propagate.
        /// </summary>
        private void ApplyResolution(Vector2Int? target, long generation)
        {
            void Apply()
            {
                try
                {
                    // Reserve/Release may run off the Unity thread. Keep the generation
                    // check and the SDK update under the same lock so a callback selected
                    // by an older reservation set cannot overwrite a newer one.
                    lock (stateLock)
                    {
                        if (generation != applyGeneration || desiredResolution != target)
                        {
                            return;
                        }

                        if (target.HasValue)
                        {
                            cameraAccess.enabled = false;
                            cameraAccess.CameraPosition = desiredPosition;
                            cameraAccess.RequestedResolution = target.Value;
                            cameraAccess.enabled = true;
                        }
                        else
                        {
                            cameraAccess.enabled = false;
                        }

                        appliedResolution = target;
                        applyScheduled = false;
                    }

                    if (target.HasValue)
                    {
                        QueuedLogger.Log(
                            $"[CameraResourceManager] Applied resolution {target.Value.x}x{target.Value.y}"
                        );
                    }
                    else
                    {
                        QueuedLogger.Log(
                            "[CameraResourceManager] Disabled camera (no active reservations)"
                        );
                    }

                    try
                    {
                        OnResolutionChanged?.Invoke(target);
                    }
                    catch (Exception subEx)
                    {
                        QueuedLogger.LogError(
                            $"[CameraResourceManager] Subscriber threw on OnResolutionChanged: {subEx.Message}"
                        );
                    }
                }
                catch (Exception ex)
                {
                    lock (stateLock)
                    {
                        if (generation == applyGeneration)
                        {
                            applyScheduled = false;
                        }
                    }

                    QueuedLogger.LogError(
                        $"[CameraResourceManager] Failed to apply resolution {target}: {ex.Message}"
                    );
                }
            }

            try
            {
                if (mainThreadContext == null)
                {
                    Apply();
                }
                else
                {
                    mainThreadContext.Post(_ => Apply(), null);
                }
            }
            catch (Exception ex)
            {
                lock (stateLock)
                {
                    if (generation == applyGeneration)
                    {
                        applyScheduled = false;
                    }
                }
                QueuedLogger.LogError(
                    $"[CameraResourceManager] Failed to schedule resolution {target}: {ex.Message}"
                );
            }
        }

        private readonly struct Reservation
        {
            public Vector2Int Resolution { get; }
            public CameraRequestPriority Priority { get; }

            public Reservation(Vector2Int resolution, CameraRequestPriority priority)
            {
                Resolution = resolution;
                Priority = priority;
            }
        }
    }
}
