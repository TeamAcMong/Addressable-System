using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace AddressableManager.Editor.Windows.Hub
{
    /// <summary>
    /// Opens the hub at its smallest legal size and measures whether anything is drawn outside the
    /// space it was given.
    /// </summary>
    /// <remarks>
    /// <b>Why this is not a stylesheet audit.</b> Adding up the declared widths of a row catches the
    /// case where every child names a width, and misses the case that actually shipped: the header's
    /// Profile / Target / Mode chips have no width at all - they are as wide as the build target's
    /// name - so a static reading of the stylesheet reports that row as comfortable while
    /// <c>StandaloneWindows64</c> pushes the last chip off the window. Only a real layout pass knows
    /// how wide a word is.
    ///
    /// <b>An overflow here is silent by default.</b> UI Toolkit's <c>overflow</c> is <c>visible</c>, so
    /// a child wider than its parent is neither clipped nor warned about; it is simply drawn past the
    /// edge, where it cannot be read and a button on it cannot be clicked. That is why this has to be
    /// measured rather than looked at - the window under test looks fine at the size a developer
    /// happens to have it open.
    ///
    /// The probe fails loudly if the layout engine did not run at all. A pass built on a tree where
    /// every rect is zero would be the most convincing false green in this repository.
    /// </remarks>
    public static class HubLayoutProbeCLI
    {
        /// <summary>Rects smaller than this are treated as "not laid out" rather than "fits".</summary>
        private const float MinimumBelievableWidth = 40f;

        /// <summary>Sub-pixel spill is rounding, not a defect.</summary>
        private const float Tolerance = 0.75f;

        /// <summary>
        /// Entry point:
        /// <c>-executeMethod AddressableManager.Editor.Windows.Hub.HubLayoutProbeCLI.ProbeLayout</c>
        /// </summary>
        /// <summary>
        /// Menu entry: run the same measurement inside a live Editor, where the screens have real
        /// content in them.
        /// </summary>
        /// <remarks>
        /// Batchmode can drive the layout engine but not fill it: no catalog is loaded, no play session
        /// exists, no scope holds anything, so every section is measured close to empty. The rows that
        /// break are the ones holding a 90-character asset path or a base URL, and only a real Editor
        /// has those. This is that run - it reports and returns rather than exiting.
        /// </remarks>
        [MenuItem("Window/Addressable Manager/Check layout at every size", priority = 400)]
        public static void CheckLayoutFromMenu()
        {
            int problems = RunChecks();

            if (problems == 0)
            {
                Debug.Log("[HubLayout] SUCCESS: nothing is drawn outside the space it was given");
                return;
            }

            Debug.LogError($"[HubLayout] {problems} element(s) overflow their parent - details above");
        }

        public static void ProbeLayout()
        {
            if (EditorUtility.scriptCompilationFailed)
            {
                Debug.LogError("[HubLayout] FAILURE: script compilation failed");
                EditorApplication.Exit(1);
                return;
            }

            int problems = RunChecks();

            if (problems > 0)
            {
                Debug.LogError($"[HubLayout] FAILURE: {problems} element(s) overflow their parent");
                EditorApplication.Exit(1);
                return;
            }

            Debug.Log("[HubLayout] SUCCESS: nothing is drawn outside the space it was given");
            EditorApplication.Exit(0);
        }

        /// <summary>Measure every section at every size. Returns how many elements overflow.</summary>
        private static int RunChecks()
        {
            var restore = EditorWindow.HasOpenInstances<AddressableManagerHub>();

            var window = EditorWindow.GetWindow<AddressableManagerHub>();
            var originalPosition = window.position;
            window.minSize = new Vector2(620, 420);

            // A sweep, not one size. Overflow is worst at the minimum, but a container that wraps has
            // a second failure mode in the middle of the range: it stops wrapping before its children
            // have room to sit side by side. Checking only the extremes misses exactly that band.
            var sizes = new[]
            {
                new Vector2(620, 420),   // the smallest the window can legally be
                new Vector2(700, 460),
                new Vector2(820, 560),   // where a two-pane split stops wrapping
                new Vector2(1024, 640),
                new Vector2(1440, 820),
            };

            var root = window.rootVisualElement;

            // Prove the text checker can fire before trusting it to report nothing. In batchmode these
            // screens are drawn nearly empty - no catalog, no play session, no scope holding anything -
            // so no string is longer than its box and the check has nothing to bite on. A run that
            // reports zero findings from a checker that cannot fire is not a clean bill of health, it
            // is a checker that was never switched on.
            if (!TextCheckerFires(root))
            {
                Debug.LogError(
                    "[HubLayout] FAILURE: the nowrap/overflow check did not flag a label deliberately " +
                    "given text far wider than its box, so its zero findings mean nothing.");
                return 1;
            }

            Debug.Log("[HubLayout] self-check: the nowrap/overflow checker fires when it should");

            int problems = 0;

            foreach (var size in sizes)
                problems += MeasureAt(window, root, size);

            Debug.Log($"[HubLayout] measured {sizes.Length} sizes x 11 sections");

            // Put the window back the way it was found. This runs in a live Editor now, where the
            // window is something the user has arranged.
            window.position = originalPosition;
            root.style.width = StyleKeyword.Null;
            root.style.height = StyleKeyword.Null;
            ForceLayout(root.panel);

            if (!restore) window.Close();

            return problems;
        }

        /// <summary>Lay the window out at one size and walk every section.</summary>
        private static int MeasureAt(AddressableManagerHub window, VisualElement root, Vector2 size)
        {
            window.position = new Rect(0, 0, size.x, size.y);
            root.style.width = size.x;
            root.style.height = size.y;

            // Drive the layout by hand. Nothing repaints on its own in batchmode, so without this the
            // whole tree measures NaN and every check below would pass by measuring nothing.
            //
            // ValidateLayout is the method the panel calls on each repaint to run Yoga over the tree.
            // It is internal, so this reaches it by reflection - acceptable in a diagnostic that is
            // never compiled into a player, and guarded: if the method is gone on a future Unity the
            // NaN check below reports that rather than passing.
            for (int i = 0; i < 4; i++)
            {
                window.Repaint();
                root.MarkDirtyRepaint();
                ForceLayout(root.panel);
            }

            float rootWidth = root.resolvedStyle.width;
            if (float.IsNaN(rootWidth) || rootWidth < MinimumBelievableWidth)
            {
                // Refused rather than passed. A tree where every rect is NaN satisfies every check
                // below it, which would be the most convincing false green in this repository.
                Debug.LogError(
                    $"[HubLayout] FAILURE: the layout engine did not run - root resolved to " +
                    $"{rootWidth} wide, so nothing below was actually measured.");
                return 1;
            }

            // Every section, not just whichever one the window happened to remember. Ten of the eleven
            // screens would otherwise never be measured, and the one that is measured is the one a
            // developer already has open - which is the screen least likely to be broken.
            int problems = 0;
            int fitted = 0;

            foreach (var section in HubSections.Create())
            {
                window.Navigate(section.Id);

                for (int i = 0; i < 3; i++)
                {
                    window.Repaint();
                    root.MarkDirtyRepaint();
                    ForceLayout(root.panel);
                }

                int before = problems;
                problems += Walk(root, new List<string> { $"{size.x:F0}x{size.y:F0}", section.Id });

                if (problems == before) fitted++;
            }

            Debug.Log($"[HubLayout] {size.x:F0}x{size.y:F0}: {fitted}/11 sections fit");

            return problems;
        }

        /// <summary>
        /// Plant a label that must be flagged, and report whether it was.
        /// </summary>
        /// <remarks>
        /// The element is removed again whatever the outcome, so the window is left as it was found.
        /// </remarks>
        private static bool TextCheckerFires(VisualElement root)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.width = 60;

            var label = new Label(new string('W', 400)) { name = "hub-layout-selftest" };
            label.style.flexGrow = 1;
            label.style.whiteSpace = WhiteSpace.NoWrap;
            row.Add(label);

            root.Add(row);

            for (int i = 0; i < 3; i++)
            {
                root.MarkDirtyRepaint();
                ForceLayout(root.panel);
            }

            // Counted without logging: this is the checker examining itself, and an error in the
            // console here would read as a defect in the window.
            bool fired = Flags(label);

            root.Remove(row);
            ForceLayout(root.panel);
            return fired;
        }

        /// <summary>Would <see cref="CheckTextCanShrink"/> flag this element? Silent.</summary>
        private static bool Flags(VisualElement element)
        {
            if (!(element is TextElement text)) return false;

            var style = element.resolvedStyle;
            if (style.whiteSpace != WhiteSpace.NoWrap) return false;
            if (style.textOverflow == TextOverflow.Ellipsis) return false;
            if (element.style.overflow.value == Overflow.Hidden) return false;
            if (element.parent == null) return false;
            if (element.parent.resolvedStyle.flexDirection != FlexDirection.Row) return false;
            if (style.flexGrow <= 0) return false;

            float needed = text.MeasureTextSize(
                text.text, 0, VisualElement.MeasureMode.Undefined,
                0, VisualElement.MeasureMode.Undefined).x;

            return needed > element.layout.width + 1f;
        }

        /// <summary>Run the panel's layout pass now, rather than waiting for a repaint that never comes.</summary>
        private static void ForceLayout(IPanel panel)
        {
            if (panel == null) return;

            var method = panel.GetType().GetMethod(
                "ValidateLayout",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic);

            method?.Invoke(panel, null);
        }

        /// <summary>Report any child whose box extends past its parent's content box.</summary>
        private static int Walk(VisualElement parent, List<string> path)
        {
            int problems = 0;

            var box = parent.contentRect;
            if (float.IsNaN(box.width)) return 0;

            foreach (var child in parent.Children())
            {
                if (child.resolvedStyle.display == DisplayStyle.None) continue;

                var r = child.layout;
                if (float.IsNaN(r.width)) continue;

                // Against contentRect.xMax, NOT contentRect.width. contentRect is expressed in the
                // parent's own space with its origin pushed in by the parent's padding and border,
                // while layout is measured from the parent's outer edge. Comparing one to the other
                // reports every child of a padded row as overflowing by exactly the padding - which
                // is what the first run of this probe did: eight findings, seven of them the ruler
                // rather than the thing being measured.
                float right = r.xMax - box.xMax;

                // Only horizontal overflow is a defect: vertical overflow inside a ScrollView is what
                // a ScrollView is for, and the sections are scroll views.
                if (right > Tolerance && !InsideScroll(parent))
                {
                    Debug.LogError(
                        $"[HubLayout] {Describe(path, child)} runs {right:F0}px past the right edge " +
                        $"of {Describe(path, parent)} ({r.width:F0}px wide in a {box.width:F0}px box)");
                    problems++;
                }

                problems += CheckTextCanShrink(child, path);

                path.Add(Name(child));
                problems += Walk(child, path);
                path.RemoveAt(path.Count - 1);
            }

            return problems;
        }

        /// <summary>
        /// A label that must not wrap must also be allowed to clip, or it will overflow the moment its
        /// text is longer than its box.
        /// </summary>
        /// <remarks>
        /// This is the check that does not depend on what is on screen right now, and that matters
        /// because batchmode draws these sections nearly empty: no catalog is loaded, no play session
        /// exists, no scope is holding anything. Measuring boxes proves the empty state fits. It says
        /// nothing about the state that actually gets reported - a 90-character asset path, a base URL,
        /// a build target called StandaloneWindows64.
        ///
        /// <c>white-space: nowrap</c> with neither <c>overflow: hidden</c> nor
        /// <c>text-overflow: ellipsis</c> is that failure written into the stylesheet: the element is
        /// forbidden from wrapping and permitted to spill, so long text is drawn straight through
        /// whatever sits to its right. Flagged wherever the element also sits in a row, where there is
        /// something to the right to be drawn through.
        /// </remarks>
        private static int CheckTextCanShrink(VisualElement element, List<string> path)
        {
            if (!(element is TextElement)) return 0;

            var style = element.resolvedStyle;
            if (style.whiteSpace != WhiteSpace.NoWrap) return 0;

            bool clips = style.textOverflow == TextOverflow.Ellipsis
                         || element.style.overflow.value == Overflow.Hidden;
            if (clips) return 0;

            // Only in a row. In a column an over-wide label has nothing beside it to damage, and the
            // parent's own overflow check above already covers the edge of the window.
            var parent = element.parent;
            if (parent == null || parent.resolvedStyle.flexDirection != FlexDirection.Row) return 0;

            // Only the label that is MEANT to absorb the row's spare width. nowrap is UI Toolkit's
            // default, so without this the check fires on every caption, badge and key in the window -
            // two hundred findings, none of them actionable, which is how a warning teaches people to
            // stop reading warnings. An element with flex-grow is the one the layout hands the long
            // string to, and it is the one that has something to its right to run over.
            if (style.flexGrow <= 0) return 0;

            // A label with room to spare is not a finding. This is about elements already using every
            // pixel they were given, where the next character has nowhere to go.
            var text = (TextElement)element;
            float needed = text.MeasureTextSize(
                text.text, 0, VisualElement.MeasureMode.Undefined,
                0, VisualElement.MeasureMode.Undefined).x;

            if (needed <= element.layout.width + 1f) return 0;

            Debug.LogError(
                $"[HubLayout] {Describe(path, element)} is nowrap with no overflow:hidden, in a row - " +
                "long text will be drawn over whatever is to its right");

            return 1;
        }

        private static bool InsideScroll(VisualElement element)
        {
            for (var e = element; e != null; e = e.parent)
                if (e is ScrollView) return true;

            return false;
        }

        private static string Name(VisualElement e)
        {
            if (!string.IsNullOrEmpty(e.name)) return e.name;

            foreach (var cls in e.GetClasses())
                return "." + cls;

            return e.GetType().Name;
        }

        private static string Describe(List<string> path, VisualElement e)
        {
            var sb = new StringBuilder();
            foreach (string p in path)
            {
                sb.Append(p);
                sb.Append('/');
            }

            sb.Append(Name(e));
            return sb.ToString();
        }
    }
}
