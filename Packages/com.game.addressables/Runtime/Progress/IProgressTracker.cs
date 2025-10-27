using System;

namespace AddressableManager.Progress
{
    /// <summary>
    /// Progress information for asset loading operations
    /// </summary>
    public struct ProgressInfo
    {
        public float Progress;          // 0-1
        public string CurrentOperation; // Description of current operation
        public long BytesDownloaded;    // Bytes downloaded so far
        public long TotalBytes;         // Total bytes to download
        public float DownloadSpeed;     // KB/s
        public float EstimatedTimeRemaining; // Seconds

        public ProgressInfo(float progress, string operation = "")
        {
            Progress = progress;
            CurrentOperation = operation;
            BytesDownloaded = 0;
            TotalBytes = 0;
            DownloadSpeed = 0;
            EstimatedTimeRemaining = 0;
        }
    }

    /// <summary>
    /// Interface for tracking loading progress with Observer Pattern
    /// </summary>
    public interface IProgressTracker
    {
        /// <summary>
        /// Event fired when progress updates
        /// </summary>
        event Action<ProgressInfo> OnProgressChanged;

        /// <summary>
        /// Current progress (0-1)
        /// </summary>
        float CurrentProgress { get; }

        /// <summary>
        /// Whether operation is complete
        /// </summary>
        bool IsComplete { get; }

        /// <summary>
        /// Update progress
        /// </summary>
        void UpdateProgress(ProgressInfo info);

        /// <summary>
        /// Mark as complete
        /// </summary>
        void Complete();

        /// <summary>
        /// Reset tracker
        /// </summary>
        void Reset();
    }
}
