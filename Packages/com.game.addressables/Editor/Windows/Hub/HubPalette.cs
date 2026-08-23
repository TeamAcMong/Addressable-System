using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace AddressableManager.Editor.Windows.Hub
{
    /// <summary>
    /// Ctrl+K: type a few letters, land on a screen.
    /// </summary>
    /// <remarks>
    /// Built last on purpose. It is cheap and risks nothing, but it is the only part of this redesign
    /// that saves nobody at two in the morning — and a palette over bad navigation is lipstick. Over
    /// good navigation it is genuinely faster than scanning fourteen rows, which is why it is here at
    /// all.
    ///
    /// Scoped to navigation. It does not run actions: a palette that can fire "Clear cache" or
    /// "Apply rules" from a fuzzy match on three letters is a way to do something destructive by
    /// accident, and every one of those actions already lives behind a confirmation on the screen
    /// that owns it.
    /// </remarks>
    internal sealed class HubPalette
    {
        private readonly IHubHost _host;
        private readonly VisualElement _root;
        private readonly TextField _query;
        private readonly VisualElement _results;

        private List<IHubSection> _matches = new List<IHubSection>();
        private int _selected;

        /// <summary>Whether the palette is on screen.</summary>
        public bool IsOpen => _root.style.display == DisplayStyle.Flex;

        /// <summary>Build the palette, hidden, and attach it over <paramref name="parent"/>.</summary>
        public HubPalette(IHubHost host, VisualElement parent)
        {
            _host = host;

            _root = new VisualElement { name = "hub-palette" };
            _root.AddToClassList("hub-palette");
            _root.style.display = DisplayStyle.None;

            var panel = new VisualElement();
            panel.AddToClassList("hub-palette-panel");

            _query = new TextField { name = "hub-palette-query" };
            _query.AddToClassList("hub-palette-query");
            panel.Add(_query);

            _results = new VisualElement();
            _results.AddToClassList("hub-palette-results");
            panel.Add(_results);

            var hint = new Label("↑ ↓ to move · Enter to go · Esc to close");
            hint.AddToClassList("hub-palette-hint");
            panel.Add(hint);

            _root.Add(panel);
            parent.Add(_root);

            _query.RegisterValueChangedCallback(evt => Refilter(evt.newValue));

            // TrickleDown so the palette sees the key before the TextField consumes it. Without it,
            // the arrow keys move the caret inside the field instead of moving the selection, and
            // Escape does nothing at all.
            _root.RegisterCallback<KeyDownEvent>(OnKeyDown, TrickleDown.TrickleDown);

            // Clicking the dimmed backdrop closes. Clicking the panel must not - hence the check
            // that the click landed on the backdrop itself rather than bubbling up from a child.
            _root.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.target == _root) Close();
            });
        }

        /// <summary>Show the palette, cleared and focused.</summary>
        public void Open()
        {
            _root.style.display = DisplayStyle.Flex;
            _query.SetValueWithoutNotify(string.Empty);
            Refilter(string.Empty);

            // Focus has to wait a frame: an element that was display:none a moment ago is not yet
            // focusable, and focusing it now silently does nothing - leaving the user typing into
            // whatever had focus before.
            _root.schedule.Execute(() => _query.Focus()).ExecuteLater(0);
        }

        /// <summary>Hide the palette.</summary>
        public void Close() => _root.style.display = DisplayStyle.None;

        private void OnKeyDown(KeyDownEvent evt)
        {
            switch (evt.keyCode)
            {
                case KeyCode.Escape:
                    Close();
                    evt.StopPropagation();
                    break;

                case KeyCode.DownArrow:
                    Move(1);
                    evt.StopPropagation();
                    break;

                case KeyCode.UpArrow:
                    Move(-1);
                    evt.StopPropagation();
                    break;

                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                    Commit();
                    evt.StopPropagation();
                    break;
            }
        }

        private void Move(int delta)
        {
            if (_matches.Count == 0) return;

            _selected = (_selected + delta + _matches.Count) % _matches.Count;
            RenderResults();
        }

        private void Commit()
        {
            if (_matches.Count == 0) return;

            var target = _matches[_selected];
            Close();
            _host.Navigate(target.Id);
        }

        private void Refilter(string query)
        {
            _matches = Match(_host.Sections, query);
            _selected = 0;
            RenderResults();
        }

        /// <summary>
        /// Subsequence matching on the title, then the stage name, then the subtitle.
        /// </summary>
        /// <remarks>
        /// Subsequence rather than substring so "rm" finds "Runtime Monitor" — the whole reason to
        /// type instead of scan. Ranked by where the match landed: a title hit always beats a
        /// subtitle hit, because a palette that puts the screen you named third is one you stop
        /// trusting.
        /// </remarks>
        internal static List<IHubSection> Match(IReadOnlyList<IHubSection> sections, string query)
        {
            var result = new List<IHubSection>();
            if (sections == null) return result;

            if (string.IsNullOrWhiteSpace(query))
            {
                foreach (var section in sections) result.Add(section);
                return result;
            }

            string q = query.Trim().ToLowerInvariant();

            var byTitle = new List<IHubSection>();
            var byStage = new List<IHubSection>();
            var byBlurb = new List<IHubSection>();

            foreach (var section in sections)
            {
                if (IsSubsequence(q, section.Title.ToLowerInvariant()))
                    byTitle.Add(section);
                else if (IsSubsequence(q, PipelineStages.Label(section.Stage).ToLowerInvariant()))
                    byStage.Add(section);
                else if (section.Subtitle != null && section.Subtitle.ToLowerInvariant().Contains(q))
                    byBlurb.Add(section);
            }

            result.AddRange(byTitle);
            result.AddRange(byStage);
            result.AddRange(byBlurb);
            return result;
        }

        /// <summary>True when every character of <paramref name="needle"/> appears in order.</summary>
        private static bool IsSubsequence(string needle, string haystack)
        {
            if (string.IsNullOrEmpty(needle)) return true;
            if (string.IsNullOrEmpty(haystack)) return false;

            int n = 0;
            for (int h = 0; h < haystack.Length && n < needle.Length; h++)
            {
                if (haystack[h] == needle[n]) n++;
            }

            return n == needle.Length;
        }

        private void RenderResults()
        {
            _results.Clear();

            if (_matches.Count == 0)
            {
                var none = new Label("Nothing matches");
                none.AddToClassList("hub-palette-none");
                _results.Add(none);
                return;
            }

            for (int i = 0; i < _matches.Count; i++)
            {
                var section = _matches[i];
                int index = i;

                var row = new VisualElement();
                row.AddToClassList("hub-palette-row");
                if (i == _selected) row.AddToClassList("hub-palette-row--on");

                var title = new Label(section.Title);
                title.AddToClassList("hub-palette-title");
                row.Add(title);

                var stage = new Label(PipelineStages.Label(section.Stage));
                stage.AddToClassList("hub-palette-stage");
                row.Add(stage);

                row.RegisterCallback<PointerDownEvent>(evt =>
                {
                    _selected = index;
                    Commit();
                    evt.StopPropagation();
                });

                _results.Add(row);
            }
        }
    }
}
