using UnityEngine;

namespace AddressableManager.Pooling.Adapters
{
    /// <summary>
    /// Shared "was this instance destroyed out from under the pool" check (HANDOFF_TO_SESSION_B.md
    /// P-2). A plain C# <c>obj == null</c> check on a generic <c>T</c> binds to
    /// <see cref="object.Equals(object)"/>, not <see cref="Object"/>'s overridden equality, so a
    /// destroyed <c>GameObject</c> sitting in a pool's free list — e.g. because a
    /// <c>LoadSceneMode.Single</c> load took it with the scene before this pool's own
    /// <c>DontDestroyOnLoad</c> root existed, or before a caller mistakenly parented pooled
    /// instances under a scene-local root — would otherwise read as "not null" here despite being
    /// unusable, and handing it to a caller throws <see cref="MissingReferenceException"/> from
    /// inside whichever callback touches it first.
    /// </summary>
    internal static class PoolInstanceGuard
    {
        public static bool IsDestroyed<T>(T obj) where T : class
        {
            return obj is Object unityObj && unityObj == null;
        }
    }
}
