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
        private static int _mainThreadId;

        /// <summary>
        /// Check if current thread is Unity's main thread
        /// </summary>
        public static bool IsMainThread => System.Threading.Thread.CurrentThread.ManagedThreadId == _mainThreadId;

        /// <summary>
        /// Get or create the dispatcher instance
        /// </summary>
        public static UnityMainThreadDispatcher Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = FindObjectOfType<UnityMainThreadDispatcher>();

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

        private void Update()
        {
            // Process all queued actions on main thread
            lock (_lock)
            {
                while (_executionQueue.Count > 0)
                {
                    var action = _executionQueue.Dequeue();
                    try
                    {
                        action?.Invoke();
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"[MainThreadDispatcher] Error executing queued action: {ex.Message}\n{ex.StackTrace}");
                    }
                }
            }
        }

        /// <summary>
        /// Enqueue action to be executed on main thread
        /// </summary>
        /// <param name="action">Action to execute</param>
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

            // Ensure instance exists (will create if needed)
            var _ = Instance;
        }

        /// <summary>
        /// Execute action on main thread and wait for completion
        /// WARNING: This will block the calling thread!
        /// </summary>
        public static void EnqueueAndWait(Action action)
        {
            if (action == null) return;

            // If already on main thread, execute immediately
            if (IsMainThread)
            {
                action();
                return;
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

            // Wait for completion
            lock (completionLock)
            {
                while (!completed)
                {
                    System.Threading.Monitor.Wait(completionLock);
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
