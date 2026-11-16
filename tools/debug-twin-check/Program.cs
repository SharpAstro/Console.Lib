// Compiles only against Debug-built libraries. ConsoleDebugInspector and DebugInspectorCore now compile into
// Release as well (inert unless the process opts in with DIR_INSPECTOR=1 and DIR_INSPECTOR_TOKEN), so they no
// longer prove which package was restored; this check still guards that a Debug consumer restores the twin.
// System.Console is spelled out because the Console.Lib namespace makes a bare Console ambiguous.
using DIR.Lib.Diagnostics;

System.Console.WriteLine(typeof(global::Console.Lib.ConsoleDebugInspector).FullName);
System.Console.WriteLine(typeof(DebugInspectorCore).FullName);
