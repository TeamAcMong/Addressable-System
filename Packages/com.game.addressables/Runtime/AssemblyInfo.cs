using System.Runtime.CompilerServices;

// The Editor test assembly reaches AssetLoaderRegistry, which is internal because nothing outside
// this package should be enumerating live loaders — but the registry is exactly the kind of thing
// that has to be tested rather than reasoned about: weak references, a lock, and a snapshot taken
// so a scope disposing itself mid-walk cannot deadlock.
//
// Test visibility instead of widening the API: nothing a consumer sees changes.
[assembly: InternalsVisibleTo("AddressableManager.Tests.Editor")]
