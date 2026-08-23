namespace AddressableManager.Editor.Windows.Hub
{
    /// <summary>
    /// The five steps content goes through on its way from the project to a player's device.
    /// </summary>
    /// <remarks>
    /// This is the navigation spine of <see cref="AddressableManagerHub"/>, and it is deliberately
    /// NOT a grouping by subsystem.
    ///
    /// The package's tooling used to be arranged by which module owned a feature: a CDN window, a
    /// rules window, a dashboard, a layout viewer. That arrangement asks the user to already know
    /// the answer to "which module owns this?" before they can find anything - and every real task
    /// crossed two or three of those windows. Ordering by the pipeline instead means the arrangement
    /// answers a question the user actually has ("how far does my content get?") rather than one
    /// only a maintainer has.
    ///
    /// The order is load-bearing: each stage can only be reached if the ones before it are sound, so
    /// the rail's colour shows where content stops today. That makes "what is broken" answerable
    /// before anything is clicked.
    /// </remarks>
    public enum PipelineStage
    {
        /// <summary>Settings, profiles and the contract between them. Nothing builds until this is sound.</summary>
        Configure = 0,

        /// <summary>Layout rules: what gets an address, a label and a group.</summary>
        Author = 1,

        /// <summary>Producing bundles and a catalog.</summary>
        Build = 2,

        /// <summary>Getting the built output somewhere a player can reach it.</summary>
        Publish = 3,

        /// <summary>What the running player actually does with it.</summary>
        Run = 4,
    }

    /// <summary>Display helpers for <see cref="PipelineStage"/>.</summary>
    public static class PipelineStages
    {
        /// <summary>Every stage, in pipeline order. The order of this array is the order of the rail.</summary>
        public static readonly PipelineStage[] All =
        {
            PipelineStage.Configure,
            PipelineStage.Author,
            PipelineStage.Build,
            PipelineStage.Publish,
            PipelineStage.Run,
        };

        /// <summary>The label shown on the rail.</summary>
        public static string Label(PipelineStage stage)
        {
            switch (stage)
            {
                case PipelineStage.Configure: return "Configure";
                case PipelineStage.Author:    return "Author";
                case PipelineStage.Build:     return "Build";
                case PipelineStage.Publish:   return "Publish";
                case PipelineStage.Run:       return "Run";
                default:                      return stage.ToString();
            }
        }
    }
}
