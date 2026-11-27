using System.Runtime.CompilerServices;

// The pure-logic parts (edit tree, usage parsing, session store, hook installer) are internal; the test
// project exercises them directly, without a running Visual Studio.
[assembly: InternalsVisibleTo("ClaudeCodeVS.Tests")]
