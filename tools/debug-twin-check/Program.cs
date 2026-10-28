// Compiles only against Debug-built libraries: ConsoleDebugInspector is #if DEBUG in Console.Lib and
// DebugInspectorCore is #if DEBUG in DIR.Lib, so the Release packages have neither type.
// System.Console is spelled out because the Console.Lib namespace makes a bare Console ambiguous.
using DIR.Lib.Diagnostics;

System.Console.WriteLine(typeof(global::Console.Lib.ConsoleDebugInspector).FullName);
System.Console.WriteLine(typeof(DebugInspectorCore).FullName);
