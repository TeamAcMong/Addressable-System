using UnityEngine.UIElements;

namespace AddressableManager.Editor.Windows.Hub
{
    /// <summary>
    /// The two ways a health state is painted: as a filled dot, or as coloured text.
    /// </summary>
    /// <remarks>
    /// These were one thing, and that was the bug. <c>hub-state--*</c> sets a background colour, a
    /// border colour and a text colour together, because it was written for the 8x8 dots on the rail,
    /// which have to be filled. Applied to a <see cref="Label"/> the background half turns the label
    /// into a solid rectangle - and since the class sets the text to the SAME colour, the text
    /// disappears into it. The rail's stage badge and the blocker's title both shipped that way: a
    /// solid amber block where a word should be.
    ///
    /// Every section that ever coloured a label had already met this and worked around it locally, by
    /// clearing <c>style.backgroundColor</c> inline right after applying the class - five copies of the
    /// same two lines, one of which also cleared <c>borderTopColor</c> and not the other three borders.
    /// Six workarounds for one missing distinction is the signal that the distinction is what was
    /// missing.
    ///
    /// So there are two class families now. <see cref="Fill"/> paints something whose whole body is the
    /// signal; <see cref="Text"/> paints something whose body is a word.
    /// </remarks>
    internal static class HubStyle
    {
        /// <summary>Paint an element whose body IS the signal - a dot, a chip, a bar.</summary>
        public static void Fill(VisualElement element, HealthState state)
        {
            if (element == null) return;

            Clear(element);
            element.AddToClassList(SectionHealth.StyleClassFor(state));
        }

        /// <summary>Paint an element whose body is text. Colour only; never a background.</summary>
        public static void Text(VisualElement element, HealthState state)
        {
            if (element == null) return;

            Clear(element);
            element.AddToClassList(SectionHealth.TextClassFor(state));
        }

        /// <summary>
        /// Remove both families, so an element can be repainted from either without the previous
        /// class surviving underneath.
        /// </summary>
        /// <remarks>
        /// The rail repaints on a timer and a section's health can change between ticks, so an element
        /// is painted many times over its life. Removing only the family being applied would leave the
        /// other one resolved as well, which is how an element ends up both hollow and filled.
        /// </remarks>
        private static void Clear(VisualElement element)
        {
            foreach (var cls in SectionHealth.AllStyleClasses)
                element.RemoveFromClassList(cls);

            foreach (var cls in SectionHealth.AllTextClasses)
                element.RemoveFromClassList(cls);
        }
    }
}
