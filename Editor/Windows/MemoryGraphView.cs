using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace AddressableManager.Editor.Windows
{
    /// <summary>
    /// Custom visual element that renders a memory usage graph over time
    /// Displays addressable asset memory consumption with a scrolling timeline
    /// </summary>
    public class MemoryGraphView : VisualElement
    {
        private const int MAX_SAMPLES = 300;  // 5 minutes at 1 sample/second
        private const float GRAPH_HEIGHT = 200f;
        private const float PADDING = 30f;

        private List<MemorySample> _samples = new List<MemorySample>();
        private float _maxMemory = 10f * 1024f * 1024f; // Start at 10MB minimum
        private bool _autoScale = true;

        // Visual styling
        private Color _backgroundColor = new Color(0.15f, 0.15f, 0.15f, 1f);
        private Color _gridColor = new Color(0.3f, 0.3f, 0.3f, 0.5f);
        private Color _graphLineColor = new Color(0.3f, 0.8f, 1f, 1f);
        private Color _graphFillColor = new Color(0.3f, 0.8f, 1f, 0.2f);
        private Color _textColor = new Color(0.8f, 0.8f, 0.8f, 1f);
        private Color _thresholdColor = new Color(1f, 0.4f, 0.2f, 0.6f);

        // Configuration
        private float _warningThreshold = 50f * 1024f * 1024f; // 50MB warning
        private float _criticalThreshold = 100f * 1024f * 1024f; // 100MB critical

        public struct MemorySample
        {
            public DateTime Timestamp;
            public float TotalMemory;      // Total addressable memory
            public float CachedMemory;     // Memory in cache
            public float ActiveMemory;     // Active loaded assets
            public int AssetCount;         // Number of assets
        }

        public bool AutoScale
        {
            get => _autoScale;
            set
            {
                _autoScale = value;
                MarkDirtyRepaint();
            }
        }

        public float WarningThreshold
        {
            get => _warningThreshold;
            set
            {
                _warningThreshold = value;
                MarkDirtyRepaint();
            }
        }

        public float CriticalThreshold
        {
            get => _criticalThreshold;
            set
            {
                _criticalThreshold = value;
                MarkDirtyRepaint();
            }
        }

        public MemoryGraphView()
        {
            style.height = GRAPH_HEIGHT;
            style.backgroundColor = _backgroundColor;
            style.borderBottomWidth = 1;
            style.borderTopWidth = 1;
            style.borderLeftWidth = 1;
            style.borderRightWidth = 1;
            style.borderBottomColor = new Color(0.2f, 0.2f, 0.2f, 1f);
            style.borderTopColor = new Color(0.2f, 0.2f, 0.2f, 1f);
            style.borderLeftColor = new Color(0.2f, 0.2f, 0.2f, 1f);
            style.borderRightColor = new Color(0.2f, 0.2f, 0.2f, 1f);

            generateVisualContent += GenerateVisualContent;
        }

        /// <summary>
        /// Add a new memory sample to the graph
        /// </summary>
        public void AddSample(MemorySample sample)
        {
            _samples.Add(sample);

            // Remove old samples beyond max
            if (_samples.Count > MAX_SAMPLES)
            {
                _samples.RemoveAt(0);
            }

            // Auto-scale max memory if enabled
            if (_autoScale && _samples.Count > 0)
            {
                var maxSample = _samples.Max(s => s.TotalMemory);
                _maxMemory = Math.Max(10f * 1024f * 1024f, maxSample * 1.2f); // 20% padding
            }

            MarkDirtyRepaint();
        }

        /// <summary>
        /// Clear all samples
        /// </summary>
        public void Clear()
        {
            _samples.Clear();
            _maxMemory = 10f * 1024f * 1024f;
            MarkDirtyRepaint();
        }

        /// <summary>
        /// Set graph colors
        /// </summary>
        public void SetColors(Color lineColor, Color fillColor, Color gridColor)
        {
            _graphLineColor = lineColor;
            _graphFillColor = fillColor;
            _gridColor = gridColor;
            MarkDirtyRepaint();
        }

        private void GenerateVisualContent(MeshGenerationContext context)
        {
            var painter = context.painter2D;
            var width = contentRect.width;
            var height = contentRect.height;

            if (width <= 0 || height <= 0) return;

            // Draw background
            DrawBackground(painter, width, height);

            // Draw grid
            DrawGrid(painter, width, height);

            // Draw threshold lines
            DrawThresholds(painter, width, height);

            // Draw graph
            if (_samples.Count >= 2)
            {
                DrawGraph(painter, width, height);
            }

            // Draw axes and labels
            DrawAxes(painter, width, height);
        }

        private void DrawBackground(Painter2D painter, float width, float height)
        {
            painter.fillColor = _backgroundColor;
            painter.BeginPath();
            painter.MoveTo(new Vector2(0, 0));
            painter.LineTo(new Vector2(width, 0));
            painter.LineTo(new Vector2(width, height));
            painter.LineTo(new Vector2(0, height));
            painter.ClosePath();
            painter.Fill();
        }

        private void DrawGrid(Painter2D painter, float width, float height)
        {
            painter.strokeColor = _gridColor;
            painter.lineWidth = 1f;

            // Horizontal grid lines (memory levels)
            int horizontalLines = 5;
            for (int i = 0; i <= horizontalLines; i++)
            {
                float y = PADDING + (height - PADDING * 2) * i / horizontalLines;
                painter.BeginPath();
                painter.MoveTo(new Vector2(PADDING, y));
                painter.LineTo(new Vector2(width - PADDING, y));
                painter.Stroke();
            }

            // Vertical grid lines (time intervals)
            int verticalLines = 10;
            for (int i = 0; i <= verticalLines; i++)
            {
                float x = PADDING + (width - PADDING * 2) * i / verticalLines;
                painter.BeginPath();
                painter.MoveTo(new Vector2(x, PADDING));
                painter.LineTo(new Vector2(x, height - PADDING));
                painter.Stroke();
            }
        }

        private void DrawThresholds(Painter2D painter, float width, float height)
        {
            painter.strokeColor = _thresholdColor;
            painter.lineWidth = 2f;

            // Draw warning threshold
            if (_warningThreshold > 0 && _warningThreshold <= _maxMemory)
            {
                float y = GetYPosition(_warningThreshold, height);
                painter.BeginPath();
                painter.MoveTo(new Vector2(PADDING, y));
                painter.LineTo(new Vector2(width - PADDING, y));
                painter.Stroke();
            }

            // Draw critical threshold with different style
            if (_criticalThreshold > 0 && _criticalThreshold <= _maxMemory)
            {
                painter.strokeColor = new Color(1f, 0.2f, 0.2f, 0.8f);
                painter.lineWidth = 2f;
                float y = GetYPosition(_criticalThreshold, height);
                painter.BeginPath();
                painter.MoveTo(new Vector2(PADDING, y));
                painter.LineTo(new Vector2(width - PADDING, y));
                painter.Stroke();
            }
        }

        private void DrawGraph(Painter2D painter, float width, float height)
        {
            var graphWidth = width - PADDING * 2;
            var graphHeight = height - PADDING * 2;

            // Draw filled area first
            painter.fillColor = _graphFillColor;
            painter.BeginPath();

            for (int i = 0; i < _samples.Count; i++)
            {
                float x = PADDING + (graphWidth * i / (MAX_SAMPLES - 1));
                float y = GetYPosition(_samples[i].TotalMemory, height);

                if (i == 0)
                    painter.MoveTo(new Vector2(x, height - PADDING));

                painter.LineTo(new Vector2(x, y));
            }

            // Close path at bottom
            painter.LineTo(new Vector2(PADDING + graphWidth, height - PADDING));
            painter.ClosePath();
            painter.Fill();

            // Draw line on top
            painter.strokeColor = _graphLineColor;
            painter.lineWidth = 2f;
            painter.BeginPath();

            for (int i = 0; i < _samples.Count; i++)
            {
                float x = PADDING + (graphWidth * i / (MAX_SAMPLES - 1));
                float y = GetYPosition(_samples[i].TotalMemory, height);

                if (i == 0)
                    painter.MoveTo(new Vector2(x, y));
                else
                    painter.LineTo(new Vector2(x, y));
            }

            painter.Stroke();
        }

        private void DrawAxes(Painter2D painter, float width, float height)
        {
            // Note: Text rendering in Painter2D is limited
            // For production, consider using Label overlays for axis labels

            // Draw axes lines
            painter.strokeColor = new Color(0.5f, 0.5f, 0.5f, 1f);
            painter.lineWidth = 2f;

            // Y-axis
            painter.BeginPath();
            painter.MoveTo(new Vector2(PADDING, PADDING));
            painter.LineTo(new Vector2(PADDING, height - PADDING));
            painter.Stroke();

            // X-axis
            painter.BeginPath();
            painter.MoveTo(new Vector2(PADDING, height - PADDING));
            painter.LineTo(new Vector2(width - PADDING, height - PADDING));
            painter.Stroke();
        }

        private float GetYPosition(float memoryValue, float height)
        {
            var graphHeight = height - PADDING * 2;
            var ratio = Mathf.Clamp01(memoryValue / _maxMemory);
            return height - PADDING - (ratio * graphHeight);
        }

        /// <summary>
        /// Get a text summary of the current memory state
        /// </summary>
        public string GetSummary()
        {
            if (_samples.Count == 0)
                return "No data";

            var latest = _samples.Last();
            var mb = latest.TotalMemory / (1024f * 1024f);
            var cachedMb = latest.CachedMemory / (1024f * 1024f);
            var activeMb = latest.ActiveMemory / (1024f * 1024f);

            return $"Total: {mb:F1} MB | Cached: {cachedMb:F1} MB | Active: {activeMb:F1} MB | Assets: {latest.AssetCount}";
        }

        /// <summary>
        /// Get peak memory usage
        /// </summary>
        public float GetPeakMemory()
        {
            if (_samples.Count == 0)
                return 0f;

            return _samples.Max(s => s.TotalMemory);
        }

        /// <summary>
        /// Get average memory usage
        /// </summary>
        public float GetAverageMemory()
        {
            if (_samples.Count == 0)
                return 0f;

            return _samples.Average(s => s.TotalMemory);
        }
    }
}
