using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using AddressableManager.UI;

namespace AddressableManager.Editor.Inspectors
{
    [CustomEditor(typeof(AddressableProgressBar))]
    public class AddressableProgressBarInspector : UnityEditor.Editor
    {
        private AddressableProgressBar _progressBar;
        private float _testProgress = 0f;
        private bool _isAnimating = false;

        public override VisualElement CreateInspectorGUI()
        {
            _progressBar = target as AddressableProgressBar;

            var root = new VisualElement();
            root.style.paddingTop = 5;

            // Header
            CreateHeader(root);

            // Default inspector
            var defaultInspector = new IMGUIContainer(() =>
            {
                DrawDefaultInspector();
            });
            root.Add(defaultInspector);

            // Testing section
            if (EditorApplication.isPlaying)
            {
                CreateTestingSection(root);
            }
            else
            {
                CreateSetupGuide(root);
            }

            return root;
        }

        private void CreateHeader(VisualElement root)
        {
            var header = new VisualElement();
            header.style.backgroundColor = new Color(0.3f, 0.6f, 0.9f);
            header.style.paddingTop = 10;
            header.style.paddingBottom = 10;
            header.style.paddingLeft = 10;
            header.style.paddingRight = 10;
            header.style.marginBottom = 10;

            var title = new Label("Addressable Progress Bar");
            title.style.fontSize = 14;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.color = Color.white;

            var subtitle = new Label("Visual feedback for asset loading operations");
            subtitle.style.fontSize = 11;
            subtitle.style.color = new Color(0.9f, 0.9f, 0.9f);
            subtitle.style.marginTop = 3;

            header.Add(title);
            header.Add(subtitle);
            root.Add(header);
        }

        private void CreateTestingSection(VisualElement root)
        {
            var section = new VisualElement();
            section.style.backgroundColor = new Color(0.25f, 0.25f, 0.25f);
            section.style.paddingTop = 10;
            section.style.paddingBottom = 10;
            section.style.paddingLeft = 10;
            section.style.paddingRight = 10;
            section.style.marginTop = 10;
            section.style.borderTopLeftRadius = 4;
            section.style.borderTopRightRadius = 4;
            section.style.borderBottomLeftRadius = 4;
            section.style.borderBottomRightRadius = 4;

            var title = new Label("Testing Controls (Play Mode)");
            title.style.fontSize = 12;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.marginBottom = 5;
            section.Add(title);

            // Progress slider
            var progressSlider = new Slider("Test Progress", 0f, 1f);
            progressSlider.value = _testProgress;
            progressSlider.RegisterValueChangedCallback(evt =>
            {
                _testProgress = evt.newValue;
                _progressBar.SetProgress(_testProgress);
            });
            section.Add(progressSlider);

            // Test buttons
            var buttonRow = new VisualElement();
            buttonRow.style.flexDirection = FlexDirection.Row;
            buttonRow.style.marginTop = 10;

            var showBtn = new Button(() => _progressBar.Show());
            showBtn.text = "Show";
            showBtn.style.flexGrow = 1;
            showBtn.style.marginRight = 5;
            buttonRow.Add(showBtn);

            var hideBtn = new Button(() => _progressBar.Hide());
            hideBtn.text = "Hide";
            hideBtn.style.flexGrow = 1;
            hideBtn.style.marginRight = 5;
            buttonRow.Add(hideBtn);

            var resetBtn = new Button(() =>
            {
                _progressBar.Reset();
                _testProgress = 0f;
                progressSlider.value = 0f;
            });
            resetBtn.text = "Reset";
            resetBtn.style.flexGrow = 1;
            buttonRow.Add(resetBtn);

            section.Add(buttonRow);

            // Animate button
            var animateBtn = new Button(() => AnimateProgress());
            animateBtn.text = _isAnimating ? "Stop Animation" : "Animate 0% → 100%";
            animateBtn.style.marginTop = 5;
            section.Add(animateBtn);

            // Status test
            var statusField = new TextField("Test Status");
            statusField.value = "Loading assets...";
            statusField.RegisterValueChangedCallback(evt =>
            {
                _progressBar.SetStatus(evt.newValue);
            });
            statusField.style.marginTop = 10;
            section.Add(statusField);

            root.Add(section);
        }

        private void CreateSetupGuide(VisualElement root)
        {
            var section = new VisualElement();
            section.style.backgroundColor = new Color(0.25f, 0.25f, 0.25f);
            section.style.paddingTop = 10;
            section.style.paddingBottom = 10;
            section.style.paddingLeft = 10;
            section.style.paddingRight = 10;
            section.style.marginTop = 10;
            section.style.borderTopLeftRadius = 4;
            section.style.borderTopRightRadius = 4;
            section.style.borderBottomLeftRadius = 4;
            section.style.borderBottomRightRadius = 4;

            var title = new Label("Setup Guide");
            title.style.fontSize = 12;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.marginBottom = 5;
            section.Add(title);

            var guide = new Label(
                "1. Assign a Fill Image (Image component with Fill type)\n" +
                "2. Optionally assign text components for feedback\n" +
                "3. Enter Play Mode to test the progress bar\n" +
                "4. Bind to an IProgressTracker in your code:\n" +
                "   progressBar.BindToTracker(tracker);"
            );
            guide.style.fontSize = 10;
            guide.style.color = new Color(0.8f, 0.8f, 0.8f);
            guide.style.whiteSpace = WhiteSpace.Normal;
            guide.style.paddingLeft = 5;
            section.Add(guide);

            root.Add(section);
        }

        private void AnimateProgress()
        {
            if (_isAnimating)
            {
                EditorApplication.update -= UpdateAnimation;
                _isAnimating = false;
            }
            else
            {
                _testProgress = 0f;
                _isAnimating = true;
                EditorApplication.update += UpdateAnimation;
            }
        }

        private void UpdateAnimation()
        {
            _testProgress += Time.deltaTime * 0.2f; // 5 seconds to complete

            if (_testProgress >= 1f)
            {
                _testProgress = 1f;
                _isAnimating = false;
                EditorApplication.update -= UpdateAnimation;
            }

            _progressBar.SetProgress(_testProgress);
            Repaint();
        }

        private void OnDisable()
        {
            if (_isAnimating)
            {
                EditorApplication.update -= UpdateAnimation;
                _isAnimating = false;
            }
        }
    }
}
