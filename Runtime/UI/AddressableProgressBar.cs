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
        [Tooltip("Auto-find and bind to active progress tracker")]
        [SerializeField] private bool autoFindTracker = true;

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
