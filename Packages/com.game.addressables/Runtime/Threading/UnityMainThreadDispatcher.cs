using System;
using System.Collections.Generic;
using UnityEngine;

namespace AddressableManager.Threading
{
    /// <summary>
    /// Dispatcher for executing actions on Unity's main thread
    /// Automatically created and managed - users don't need to interact directly
    /// </summary>
    [DefaultExecutionOrder(-1000)]
    public class UnityMainThreadDispatcher : MonoBehaviour
    {
        private static UnityMainThreadDispatcher _instance;
        private static readonly Queue<Action> _executionQueue = new Queue<Action>();
        private static readonly object _lock = new object();

        // Scratch list the drain swaps into, so queued actions run OUTSIDE _lock. Reused rather than
        // reallocated per frame; only ever touched by Update() on the main thread.
        private static readonly List<Action> _drainBuffer = new List<Action>();

        private static int _mainThreadId;

        /// <summary>
        /// Check if current thread is Unity's main thread.
        /// </summary>
        /// <remarks>
        /// Latched by <see cref="LatchMainThread"/> at <c>SubsystemRegistration</c>, NOT by
        /// <see cref="Awake"/>. It used to be latched only in <c>Awake</c>, which made this property
        /// return <b>false on the main thread</b> for the whole window before any dispatcher
        /// GameObject existed — <c>_mainThreadId</c> was still its default <c>0</c>, which no real
        /// thread ever has. Every caller that branches on this got the answer backwards in that
        /// window: <see cref="EnqueueAndWait"/> took its "marshal and block" path <em>on the main
        /// thread</em> and then waited for an <see cref="Update"/> that could only ever run on the
        /// thread it had just blocked (a hard hang with no log), and <see cref="Enqueue"/> reached
        /// the <see cref="Instance"/> getter from worker threads. Latching from a
        /// <c>RuntimeInitializeOnLoadMethod</c> — which Unity always runs on the main thread, before
        /// any user code — means the window does not exist.
        /// </remarks>
        public static bool IsMainThread => System.Threading.Thread.CurrentThread.ManagedThreadId == _mainThreadId;

        /// <summary>
        /// True when a dispatcher exists that can actually drain the queue. Anything queued while
        /// this is false stays queued.
        /// </summary>
        private static bool HasPump => _instance != null;

        /// <summary>
        /// Latch the main thread id and clear per-session static state.
        /// </summary>
        /// <remarks>
        /// Runs before <see cref="EnsureInstance"/> and before any user code. The queue and instance
        /// resets are the domain-reload-disabled half: with <em>Reload Domain = off</em> these
        /// statics survive play-mode exit, so without this the next session's first
        /// <see cref="Update"/> drains the previous session's leftover actions — closures over
        /// objects from a dead session — and <c>_mainThreadId</c> keeps a stale id.
        /// <c>OnDestroy</c> nulls <c>_instance</c> but never ran for the queue.
        /// Same reasoning, and the same hook, as <c>AddressableRuntime.Init</c>.
        /// </remarks>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void LatchMainThread()
        {
            _mainThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;

            lock (_lock)
            {
                _executionQueue.Clear();
            }

            _instance = null;
        }

        /// <summary>
        /// Create the dispatcher up front, on the main thread, so no worker thread ever has to.
        /// </summary>
        /// <remarks>
        /// <see cref="Instance"/> calls <c>FindAnyObjectByType</c>, <c>new GameObject</c> and
        /// <c>AddComponent</c> — all main-thread-only. <see cref="Enqueue"/> used to touch it after
        /// queueing, so the <b>first</b> worker-thread <c>Enqueue</c> in a process threw
        /// <c>UnityException</c> out of a method whose entire purpose is being callable from a worker
        /// thread; every off-thread path in the package (<c>ThreadSafeAssetLoader</c>,
        /// <c>ThreadSafeCacheManager</c>'s release marshal, <c>AddressablePoolManager</c>'s
        /// main-thread hop, <c>SmartAssetHandle</c>'s finalizer) went through it. Creating it here
        /// costs one inactive GameObject per session and removes that failure entirely.
        /// <c>BeforeSceneLoad</c> rather than <c>SubsystemRegistration</c>: GameObject creation is
        /// not safe at the earlier hook.
        /// </remarks>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void EnsureInstance()
        {
            var _ = Instance;
        }

        /// <summary>
        /// Get or create the dispatcher instance
        /// </summary>
        public static UnityMainThreadDispatcher Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = FindAnyObjectByType<UnityMainThreadDispatcher>();

                    if (_instance == null)
                    {
                        var go = new GameObject("[UnityMainThreadDispatcher]");
                        _instance = go.AddComponent<UnityMainThreadDispatcher>();
                        DontDestroyOnLoad(go);
                    }
                }
                return _instance;
            }
        }

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }

            _instance = this;
            _mainThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
            DontDestroyOnLoad(gameObject);
        }

        /// <summary>
        /// Drain the queue on the main thread.
        /// </summary>
        /// <remarks>
        /// Actions are moved into <c>_drainBuffer</c> under <c>_lock</c> and invoked <b>after</b> it
        /// is released. Invoking them while holding the lock made every worker-thread
        /// <see cref="Enqueue"/> block for the whole drain, and turned any queued action that waited
        /// on a worker (an <see cref="EnqueueAndWait"/> from the other direction, a
        /// <c>Task.Wait</c>) into a deadlock: the worker needed <c>_lock</c> to enqueue and the main
        /// thread would not give it up until the action it was waiting on returned.
        /// </remarks>
        private void Update()
        {
            lock (_lock)
            {
                if (_executionQueue.Count == 0) return;

                while (_executionQueue.Count > 0)
                {
                    _drainBuffer.Add(_executionQueue.Dequeue());
                }
            }

            try
            {
                for (int i = 0; i < _drainBuffer.Count; i++)
                {
                    try
                    {
                        _drainBuffer[i]?.Invoke();
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"[MainThreadDispatcher] Error executing queued action: {ex.Message}\n{ex.StackTrace}");
                    }
                }
            }
            finally
            {
                // Cleared in a finally so a queued action that throws something the catch above does
                // not handle cannot leave the buffer populated and replay it next frame.
                _drainBuffer.Clear();
            }
        }

        /// <summary>
        /// Enqueue action to be executed on main thread. Safe to call from any thread.
        /// </summary>
        /// <param name="action">Action to execute</param>
        /// <remarks>
        /// Only ever touches <see cref="Instance"/> when already on the main thread. The getter runs
        /// <c>FindAnyObjectByType</c>/<c>new GameObject</c>/<c>AddComponent</c>, which throw
        /// <c>UnityException</c> off the main thread — so the old unconditional <c>var _ = Instance</c>
        /// at the end of this method made the first worker-thread call in a process throw, leaving
        /// the action queued and the caller's continuation never completed.
        /// <see cref="EnsureInstance"/> now creates the dispatcher during startup, so the normal case
        /// is that one already exists; the error below covers the case where a worker thread gets
        /// here before <c>BeforeSceneLoad</c> has run.
        /// </remarks>
        public static void Enqueue(Action action)
        {
            if (action == null) return;

            // If already on main thread, execute immediately
            if (IsMainThread)
            {
                action();
                return;
            }

            // Otherwise queue for next Update()
            lock (_lock)
            {
                _executionQueue.Enqueue(action);
            }

            if (!HasPump)
            {
                Debug.LogError(
                    "[MainThreadDispatcher] An action was queued from a worker thread before any " +
                    "dispatcher existed, so nothing is draining the queue yet. It will run on the " +
                    "first Update() after one is created. A dispatcher is normally created during " +
                    "startup (RuntimeInitializeOnLoadMethod, BeforeSceneLoad); reaching this means " +
                    "the worker thread ran first, or this is edit mode, where no Update() ever runs.");
            }
        }

        /// <summary>
        /// How long <see cref="EnqueueAndWait"/> will block before it gives up and throws. Generous
        /// on purpose — it exists to convert an unbounded hang into a diagnosable failure, not to
        /// police how long legitimate main-thread work may take.
        /// </summary>
        private const int EnqueueAndWaitTimeoutMs = 30_000;

        /// <summary>
        /// Execute action on main thread and wait for completion.
        /// WARNING: This will block the calling thread!
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// Called from a worker thread with no dispatcher to drain the queue — the wait could never
        /// end. Previously this blocked forever instead.
        /// </exception>
        /// <exception cref="TimeoutException">
        /// The main thread did not run the action within <see cref="EnqueueAndWaitTimeoutMs"/>.
        /// </exception>
        /// <remarks>
        /// The two guards below replace an unconditional <c>Monitor.Wait</c> with no timeout. That
        /// wait deadlocked the main thread outright before <see cref="IsMainThread"/> was latched
        /// eagerly: the main thread saw <c>IsMainThread == false</c> (id still <c>0</c>), took this
        /// path, queued the action, and then blocked waiting for the <see cref="Update"/> that only
        /// it could run. <see cref="LatchMainThread"/> fixes that case at the root — the main thread
        /// now always takes the inline branch above. These guards cover what is left: a worker
        /// thread waiting on a pump that does not exist or never ticks (edit mode, teardown, a
        /// paused player).
        /// </remarks>
        public static void EnqueueAndWait(Action action)
        {
            if (action == null) return;

            // If already on main thread, execute immediately
            if (IsMainThread)
            {
                action();
                return;
            }

            if (!HasPump)
            {
                throw new InvalidOperationException(
                    "[MainThreadDispatcher] EnqueueAndWait() was called from a worker thread " +
                    $"(id {System.Threading.Thread.CurrentThread.ManagedThreadId}) with no dispatcher " +
                    "to run the queue, so the wait could never complete. Nothing was queued. A " +
                    "dispatcher is created during startup in play mode; in edit mode no Update() " +
                    "runs at all, so marshalling to the main thread cannot work there.");
            }

            // Otherwise queue and wait
            var completed = false;
            var completionLock = new object();

            Enqueue(() =>
            {
                try
                {
                    action();
                }
                finally
                {
                    lock (completionLock)
                    {
                        completed = true;
                        System.Threading.Monitor.Pulse(completionLock);
                    }
                }
            });

            // Wait for completion, bounded — see the remarks.
            var deadline = DateTime.UtcNow.AddMilliseconds(EnqueueAndWaitTimeoutMs);
            lock (completionLock)
            {
                while (!completed)
                {
                    var remaining = deadline - DateTime.UtcNow;
                    if (remaining <= TimeSpan.Zero || !System.Threading.Monitor.Wait(completionLock, remaining))
                    {
                        if (completed) break;

                        throw new TimeoutException(
                            "[MainThreadDispatcher] EnqueueAndWait() gave up after " +
                            $"{EnqueueAndWaitTimeoutMs / 1000}s: the action was queued but the main " +
                            "thread never ran it. The main thread is blocked, or its Update() is not " +
                            "ticking (edit mode, teardown, or a stalled player). The action stays " +
                            "queued and will still run if the main thread recovers.");
                    }
                }
            }
        }

        private void OnDestroy()
        {
            if (_instance == this)
            {
                _instance = null;
            }
        }
    }
}
