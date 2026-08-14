using System.Runtime.CompilerServices;

// The Editor test assembly reaches a small number of internals — CatalogReader.ResolveLoader and
// the internal setter on CatalogBundle.DependentEntryCount among them.
//
// The alternative was widening those members to public, which would put reflection plumbing and a
// mutable count into the package's supported surface for the sake of a test. Test visibility is the
// narrower change: nothing outside this package gains access, and the members stay internal in the
// documentation and in IntelliSense for anyone consuming the package.
[assembly: InternalsVisibleTo("AddressableManager.Tests.Editor")]
