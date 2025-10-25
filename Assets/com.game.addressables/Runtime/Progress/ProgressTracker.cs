using System;
using UnityEngine;

namespace AddressableManager.Progress
{
    /// <summary>
    /// Concrete implementation of progress tracker with Observer Pattern
    /// </summary>
    public class ProgressTracker : IProgressTracker
    {
        private ProgressInfo _currentInfo;
        private bool _isComplete;

        public event Action<ProgressInfo> OnProgressChanged;

        public float CurrentProgress => _currentInfo.Progress;
        public bool IsComplete => _isComplete;

        public ProgressTracker()
        {
            Reset();
        }

        public void UpdateProgress(ProgressInfo info)
        {
            if (_isComplete)
            {
                Debug.LogWarning("[ProgressTracker] Cannot update completed tracker");
                return;
            }

            _currentInfo = info;

            // Clamp progress
            _currentInfo.Progress = Mathf.Clamp01(_currentInfo.Progress);

            // Invoke event
            OnProgressChanged?.Invoke(_currentInfo);

            // Auto-complete if progress reaches 1
            if (_currentInfo.Progress >= 1f)
            {
                Complete();
            }
        }

        public void Complete()
        {
            if (_isComplete) return;

            _isComplete = true;
            _currentInfo.Progress = 1f;
            _currentInfo.CurrentOperation = "Complete";

            OnProgressChanged?.Invoke(_currentInfo);
        }

        public void Reset()
        {
            _currentInfo = new ProgressInfo(0f, "Initializing");
            _isComplete = false;
        }
    }
}
