using System;

namespace AddressableManager.Editor.Cdn
{
    /// <summary>
    /// Marks a static method that contributes project-specific rules to <see cref="SettingsContract"/>.
    /// </summary>
    /// <remarks>
    /// The method must be static and shaped exactly like this:
    ///
    /// <code>
    /// [SettingsRuleProvider]
    /// static IEnumerable&lt;SettingsRule&gt; MyProjectRules(AddressableAssetSettings settings)
    /// {
    ///     foreach (var group in settings.groups)
    ///     {
    ///         if (group == null || !group.Name.StartsWith("Icons")) continue;
    ///
    ///         var schema = group.GetSchema&lt;BundledAssetGroupSchema&gt;();
    ///         yield return new SettingsRule(
    ///             id:              $"project:{group.Name}:BundleMode",
    ///             description:     $"Icon group '{group.Name}' must stay split by label.",
    ///             readCurrent:     () =&gt; schema?.BundleMode.ToString() ?? "(no schema)",
    ///             expectedDisplay: "PackTogetherByLabel",
    ///             isSatisfied:     () =&gt; schema?.BundleMode == BundledAssetGroupSchema.BundlePackingMode.PackTogetherByLabel,
    ///             fix:             null,          // null = manual; the validator will not synthesize one
    ///             isGroupScoped:   true,
    ///             groupName:       group.Name);
    ///     }
    /// }
    /// </code>
    ///
    /// A wrong signature is reported as an error and the method is skipped - it is never silently
    /// ignored, because a rule that quietly fails to register reads as a passing rule.
    ///
    /// WHAT THIS IS FOR. The built-in contract deliberately does not check BundleMode, BuildPath,
    /// LoadPath or StaticContent: a Local group needs different values than a Remote one, and that is
    /// not something a validator can decide on a project's behalf. What was missing was not another
    /// built-in rule but somewhere for a project to say what ITS groups must look like, so those checks
    /// live in the same report, the same "Fix All", and the same CI gate as the built-in ones instead of
    /// in a parallel tool per project.
    ///
    /// TWO THINGS TO KNOW BEFORE SUPPLYING A NON-NULL <c>fix</c>. It runs unattended - CdnSetupCLI
    /// invokes Fix() in batchmode for every unsatisfied rule that has one, then saves. And ids must be
    /// unique across the whole contract; a collision with a built-in id is refused with an error rather
    /// than silently replacing anything. Prefix ids with something project-specific.
    /// </remarks>
    [AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
    public sealed class SettingsRuleProviderAttribute : Attribute
    {
    }
}
