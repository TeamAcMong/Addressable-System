using UnityEngine;
using UnityEngine.UI;
using TMPro;
using AddressableManager.Progress;

namespace AddressableManager.UI
{
    /// <summary>
    /// Visual progress bar for addressable loading operations
    /// Auto-updates from IProgressTracker
    /// </summary>
    [AddComponentMenu("Addressable Manager/Progress Bar")]
    [RequireComponent(typeof(CanvasGroup))]
    public class AddressableProgressBar : MonoBehaviour
    {
        [Header("UI References")]
        [Tooltip("Fill image (Image component with Fill type)")]
        [SerializeField] private Image fillImage;

        [Tooltip("Percentage text (optional)")]
        [SerializeField] private TextMeshProUGUI percentText;

        [Tooltip("Status/operation text (optional)")]
        [SerializeField] private TextMeshProUGUI statusText;

        [Tooltip("Download info text (optional)")]
        [SerializeField] private TextMeshProUGUI downloadText;

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

            if (percentText != null)
            {
                percentText.text = $"{_targetFill * 100:F0}%";
            }

            if (_targetFill >= 1f && hideWhenComplete)
            {
                _hideTimer = hideDelay;
            }
        }

        /// <summary>
        /// Set status text
        /// </summary>
        public void SetStatus(string status)
        {
            if (statusText != null)
            {
                statusText.text = status;
            }
        }

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

            if (percentText != null)
                percentText.text = "0%";

            if (statusText != null)
                statusText.text = "";

            if (downloadText != null)
                downloadText.text = "";
        }

        private void OnProgressChanged(ProgressInfo info)
        {
            SetProgress(info.Progress);

            if (statusText != null && !string.IsNullOrEmpty(info.CurrentOperation))
            {
                statusText.text = info.CurrentOperation;
            }

            if (downloadText != null && info.TotalBytes > 0)
            {
                float downloadedMB = info.BytesDownloaded / (1024f * 1024f);
                float totalMB = info.TotalBytes / (1024f * 1024f);
                float speedKBps = info.DownloadSpeed;

                downloadText.text = $"{downloadedMB:F2} MB / {totalMB:F2} MB @ {speedKBps:F0} KB/s";

                if (info.EstimatedTimeRemaining > 0)
                {
                    downloadText.text += $" • ETA: {info.EstimatedTimeRemaining:F0}s";
                }
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
                // Blend from start to mid (0% to 50%)
                return Color.Lerp(startColor, midColor, t * 2f);
            }
            else
            {
                // Blend from mid to end (50% to 100%)
                return Color.Lerp(midColor, endColor, (t - 0.5f) * 2f);
            }
        }

        private void OnDestroy()
        {
            if (_currentTracker != null)
            {
                _currentTracker.OnProgressChanged -= OnProgressChanged;
            }
        }

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
