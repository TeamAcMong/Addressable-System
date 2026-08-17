using UnityEngine;
using UnityEngine.UI;
using AddressableManager.Progress;
#if TMP_PRESENT
using TMPro;
#endif

namespace AddressableManager.UI
{
    /// <summary>
    /// Visual progress bar for addressable loading operations
    /// Auto-updates from IProgressTracker.
    ///
    /// TextMeshPro fields are wired in only when the project compiles with
    /// the TMP_PRESENT define (set by the asmdef via versionDefines so it's
    /// active whenever com.unity.textmeshpro 3.0.0+ is present).
    /// Falls back to plain UnityEngine.UI.Text otherwise so the component
    /// is always usable.
    /// </summary>
    [AddComponentMenu("Addressable Manager/Progress Bar")]
    [RequireComponent(typeof(CanvasGroup))]
    public class AddressableProgressBar : MonoBehaviour
    {
        [Header("UI References")]
        [Tooltip("Fill image (Image component with Fill type)")]
        [SerializeField] private Image fillImage;

#if TMP_PRESENT
        [Tooltip("Percentage text (optional, TMP)")]
        [SerializeField] private TextMeshProUGUI percentText;

        [Tooltip("Status/operation text (optional, TMP)")]
        [SerializeField] private TextMeshProUGUI statusText;

        [Tooltip("Download info text (optional, TMP)")]
        [SerializeField] private TextMeshProUGUI downloadText;
#else
        [Tooltip("Percentage text (optional)")]
        [SerializeField] private Text percentText;

        [Tooltip("Status/operation text (optional)")]
        [SerializeField] private Text statusText;

        [Tooltip("Download info text (optional)")]
        [SerializeField] private Text downloadText;
#endif

        [Header("Settings")]
        // There was an `autoFindTracker` toggle here, defaulted to true and never read by anything.
        // No auto-find code was ever written: binding has always been explicit, through
        // BindToTracker. An inspector checkbox that promises behaviour the component does not have
        // is worse than no checkbox, so it is gone rather than wired up — wiring it would mean
        // inventing a "find the active tracker" rule, and which tracker is active is a question only
        // the game can answer.

        [Tooltip("Smooth fill animation")]
        [SerializeField] private bool smoothFill = true;

        [Tooltip("Fill animation speed")]
        [Range(1f, 20f)]
        [SerializeField] private float fillSpeed = 10f;

        [Tooltip("Hide when complete")]
        [SerializeField] private bool hideWhenComplete = true;

        [Tooltip("Delay before hiding (seconds)")]
        [Range(0f, 5f)]
        [SerializeField] private float hideDelay = 0.5f;

        [Header("Visual Feedback")]
        [Tooltip("Change color based on progress")]
        [SerializeField] private bool gradientColors = true;

        [SerializeField] private Color startColor = new Color(1f, 0.3f, 0.3f); // Red at 0%
        [SerializeField] private Color midColor = new Color(1f, 0.8f, 0.2f);   // Yellow at 50%
        [SerializeField] private Color endColor = new Color(0.3f, 1f, 0.3f);   // Green at 100%

        // State
        private IProgressTracker _currentTracker;
        private CanvasGroup _canvasGroup;
        private float _targetFill = 0f;
        private float _currentFill = 0f;
        private float _hideTimer = -1f;

        private void Awake()
        {
            _canvasGroup = GetComponent<CanvasGroup>();

            if (fillImage == null)
            {
                Debug.LogError("[AddressableProgressBar] Fill Image not assigned!", this);
            }
        }

        private void Update()
        {
            // Smooth fill animation
            if (smoothFill && Mathf.Abs(_currentFill - _targetFill) > 0.001f)
            {
                _currentFill = Mathf.Lerp(_currentFill, _targetFill, Time.deltaTime * fillSpeed);
                UpdateFillImage();
            }

            // Hide timer
            if (_hideTimer >= 0f)
            {
                _hideTimer -= Time.deltaTime;
                if (_hideTimer <= 0f)
                {
                    Hide();
                    _hideTimer = -1f;
                }
            }
        }

        /// <summary>
        /// Bind to a progress tracker
        /// </summary>
        public void BindToTracker(IProgressTracker tracker)
        {
            // Unbind from previous tracker
            if (_currentTracker != null)
            {
                _currentTracker.OnProgressChanged -= OnProgressChanged;
            }

            _currentTracker = tracker;

            if (_currentTracker != null)
            {
                _currentTracker.OnProgressChanged += OnProgressChanged;
                Show();
            }
        }

        /// <summary>
        /// Manually set progress (0-1)
        /// </summary>
        public void SetProgress(float progress)
        {
            _targetFill = Mathf.Clamp01(progress);

            if (!smoothFill)
            {
                _currentFill = _targetFill;
                UpdateFillImage();
            }

            SetText(percentText, $"{_targetFill * 100:F0}%");

            if (_targetFill >= 1f && hideWhenComplete)
            {
                _hideTimer = hideDelay;
            }
        }

        /// <summary>
        /// Show a CDN download in real bytes — task 3.11.
        /// </summary>
        /// <remarks>
        /// Wire it up as the IProgress target of a download:
        /// <code>
        /// var progress = new Progress&lt;DownloadProgress&gt;(bar.SetDownloadProgress);
        /// await CdnManager.DownloadAsync(request, progress, cancellation.Token);
        /// </code>
        ///
        /// Why this exists next to <see cref="SetProgress(float)"/> rather than replacing it: a
        /// fraction is all you can show for a load, but for a download a fraction alone is a poor
        /// UI. "47%" of an unknown total tells a player nothing about whether to wait; "12 of 80 MB
        /// at 1.4 MB/s, about 50s left" does.
        ///
        /// THE TWO CASES THAT LOOK LIKE BUGS AND ARE NOT
        /// While Addressables is still working out the size, TotalBytes is 0 — reported here as
        /// "calculating" rather than as 0% or a division by zero. And an ETA of -1 means unknown,
        /// which is shown as a blank rather than "0s remaining", because a progress bar claiming
        /// zero seconds for a minute is worse than one admitting it does not know yet.
        /// </remarks>
        public void SetDownloadProgress(AddressableManager.Cdn.DownloadProgress progress)
        {
            if (!progress.IsSizeKnown)
            {
                // No fraction to show yet. The bar is left where it is rather than snapped to 0,
                // which would make a resumed download appear to restart.
                SetText(statusText, "Calculating download size...");
                SetText(percentText, string.Empty);
                return;
            }

            SetProgress(progress.Percent);

            string transferred = $"{FormatBytes(progress.DownloadedBytes)} / {FormatBytes(progress.TotalBytes)}";
            string speed = progress.BytesPerSecond > 0
                ? $"  {FormatBytes((long)progress.BytesPerSecond)}/s"
                : string.Empty;
            string eta = progress.EtaSeconds >= 0
                ? $"  {FormatDuration(progress.EtaSeconds)} left"
                : string.Empty;

            SetText(statusText, transferred + speed + eta);
        }

        /// <summary>
        /// Bytes as a human-readable size.
        /// </summary>
        /// <remarks>
        /// Binary units, matching what platform storage UIs show, so "80 MB" here and "80 MB" in
        /// the OS settings screen mean the same thing to a player deciding whether they have room.
        /// </remarks>
        public static string FormatBytes(long bytes)
        {
            if (bytes < 0) return "?";
            if (bytes < 1024) return $"{bytes} B";

            double kb = bytes / 1024.0;
            if (kb < 1024) return $"{kb:F0} KB";

            double mb = kb / 1024.0;
            if (mb < 1024) return $"{mb:F1} MB";

            return $"{mb / 1024.0:F2} GB";
        }

        /// <summary>Seconds as a short duration.</summary>
        private static string FormatDuration(double seconds)
        {
            if (seconds < 1) return "moments";
            if (seconds < 60) return $"{seconds:F0}s";

            double minutes = seconds / 60.0;
            if (minutes < 60) return $"{minutes:F0}m";

            return $"{minutes / 60.0:F1}h";
        }

        /// <summary>
        /// Set status text
        /// </summary>
        public void SetStatus(string status) => SetText(statusText, status);

        /// <summary>
        /// Show progress bar
        /// </summary>
        public void Show()
        {
            if (_canvasGroup != null)
            {
                _canvasGroup.alpha = 1f;
                _canvasGroup.blocksRaycasts = true;
            }

            gameObject.SetActive(true);
            _hideTimer = -1f;
        }

        /// <summary>
        /// Hide progress bar
        /// </summary>
        public void Hide()
        {
            if (_canvasGroup != null)
            {
                _canvasGroup.alpha = 0f;
                _canvasGroup.blocksRaycasts = false;
            }

            gameObject.SetActive(false);
        }

        /// <summary>
        /// Reset progress bar
        /// </summary>
        public void Reset()
        {
            _targetFill = 0f;
            _currentFill = 0f;
            _hideTimer = -1f;
            UpdateFillImage();

            SetText(percentText, "0%");
            SetText(statusText, string.Empty);
            SetText(downloadText, string.Empty);
        }

        private void OnProgressChanged(ProgressInfo info)
        {
            SetProgress(info.Progress);

            if (!string.IsNullOrEmpty(info.CurrentOperation))
            {
                SetText(statusText, info.CurrentOperation);
            }

            if (info.TotalBytes > 0)
            {
                float downloadedMB = info.BytesDownloaded / (1024f * 1024f);
                float totalMB = info.TotalBytes / (1024f * 1024f);
                float speedKBps = info.DownloadSpeed;

                string text = $"{downloadedMB:F2} MB / {totalMB:F2} MB @ {speedKBps:F0} KB/s";

                if (info.EstimatedTimeRemaining > 0)
                {
                    text += $" • ETA: {info.EstimatedTimeRemaining:F0}s";
                }

                SetText(downloadText, text);
            }
        }

        private void UpdateFillImage()
        {
            if (fillImage != null)
            {
                fillImage.fillAmount = _currentFill;

                if (gradientColors)
                {
                    fillImage.color = GetGradientColor(_currentFill);
                }
            }
        }

        private Color GetGradientColor(float t)
        {
            if (t < 0.5f)
            {
                return Color.Lerp(startColor, midColor, t * 2f);
            }
            return Color.Lerp(midColor, endColor, (t - 0.5f) * 2f);
        }

        private void OnDestroy()
        {
            if (_currentTracker != null)
            {
                _currentTracker.OnProgressChanged -= OnProgressChanged;
            }
        }

#if TMP_PRESENT
        private static void SetText(TextMeshProUGUI label, string value)
        {
            if (label != null) label.text = value;
        }
#else
        private static void SetText(Text label, string value)
        {
            if (label != null) label.text = value;
        }
#endif

        #region Editor Helpers

#if UNITY_EDITOR
        private void OnValidate()
        {
            // Auto-find fill image if not set
            if (fillImage == null)
            {
                fillImage = GetComponentInChildren<Image>();
            }

            // Auto-find canvas group
            if (_canvasGroup == null)
            {
                _canvasGroup = GetComponent<CanvasGroup>();
            }
        }
#endif

        #endregion
    }
}
