using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace AddressableManager.Progress
{
    /// <summary>
    /// Composite tracker that aggregates progress from multiple child trackers
    /// Useful for batch operations or multi-stage loading
    /// </summary>
    public class CompositeProgressTracker : IProgressTracker
    {
        private readonly List<IProgressTracker> _childTrackers;
        private readonly List<float> _weights;
        private bool _isComplete;

        public event Action<ProgressInfo> OnProgressChanged;

        public float CurrentProgress
        {
            get
            {
                if (_childTrackers.Count == 0) return 0f;

                float totalProgress = 0f;
                float totalWeight = _weights.Sum();

                for (int i = 0; i < _childTrackers.Count; i++)
                {
                    float weight = _weights[i];
                    float progress = _childTrackers[i].CurrentProgress;
                    totalProgress += progress * weight;
                }

                return totalWeight > 0 ? totalProgress / totalWeight : 0f;
            }
        }

        public bool IsComplete => _isComplete || _childTrackers.All(t => t.IsComplete);

        public CompositeProgressTracker()
        {
            _childTrackers = new List<IProgressTracker>();
            _weights = new List<float>();
        }

        /// <summary>
        /// Add a child tracker with optional weight (default = 1)
        /// </summary>
        public void AddTracker(IProgressTracker tracker, float weight = 1f)
        {
            if (tracker == null)
            {
                Debug.LogError("[CompositeProgressTracker] Cannot add null tracker");
                return;
            }

            _childTrackers.Add(tracker);
            _weights.Add(weight);

            // Subscribe to child progress
            tracker.OnProgressChanged += OnChildProgressChanged;
        }

        /// <summary>
        /// Remove a child tracker
        /// </summary>
        public void RemoveTracker(IProgressTracker tracker)
        {
            int index = _childTrackers.IndexOf(tracker);
            if (index >= 0)
            {
                tracker.OnProgressChanged -= OnChildProgressChanged;
                _childTrackers.RemoveAt(index);
                _weights.RemoveAt(index);
            }
        }

        public void UpdateProgress(ProgressInfo info)
        {
            // Composite tracker doesn't update directly
            // It aggregates from children
            Debug.LogWarning("[CompositeProgressTracker] Use child trackers to update progress");
        }

        public void Complete()
        {
            if (_isComplete) return;

            _isComplete = true;

            var finalInfo = new ProgressInfo(1f, "All operations complete");
            OnProgressChanged?.Invoke(finalInfo);
        }

        public void Reset()
        {
            _isComplete = false;

            foreach (var tracker in _childTrackers)
            {
                tracker.Reset();
            }
        }

        private void OnChildProgressChanged(ProgressInfo info)
        {
            // Aggregate and forward
            var aggregatedInfo = new ProgressInfo
            {
                Progress = CurrentProgress,
                CurrentOperation = info.CurrentOperation,
                BytesDownloaded = 0, // Could aggregate if needed
                TotalBytes = 0,
                DownloadSpeed = 0,
                EstimatedTimeRemaining = 0
            };

            OnProgressChanged?.Invoke(aggregatedInfo);

            // Auto-complete if all children complete
            if (IsComplete && !_isComplete)
            {
                Complete();
            }
        }

        /// <summary>
        /// Get all child trackers
        /// </summary>
        public IReadOnlyList<IProgressTracker> GetChildTrackers() => _childTrackers.AsReadOnly();
    }
}
