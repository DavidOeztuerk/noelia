using System.Runtime.CompilerServices;

// The commands are internal: they are a command line, not an API, and anything
// public here would be something a consumer could take a dependency on and then
// be broken by. The test project needs to call them directly all the same —
// running the tool as a process and parsing its stdout would test the formatter
// rather than the analysis.
[assembly: InternalsVisibleTo("Noelia.Infrastructure.Tests")]
