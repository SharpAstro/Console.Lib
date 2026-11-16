// Creates a VirtualTerminal, attaches the inspector, pumps once and exits. System.Console is spelled out because the
// Console.Lib namespace makes a bare Console ambiguous.
using Console.Lib;

var terminal = new VirtualTerminal();
using var inspector = ConsoleDebugInspector.Attach("aotprobe", terminal);
inspector.Pump();
System.Console.WriteLine("probe: done");
return 0;
