namespace Console.Lib;

/// <summary>How much pointer motion a <see cref="VirtualTerminal"/> asks the terminal to report.</summary>
public enum MouseTracking
{
    /// <summary>xterm mode 1003 (any-event) beside 1000/1002: motion is reported with no button held, so hover works.</summary>
    AnyMotion = 0,

    /// <summary>Modes 1000/1002 only: motion is reported while a button is held (a drag), never as hover.</summary>
    ButtonMotion = 1,
}
