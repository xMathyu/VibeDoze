namespace VibeDoze.Core;

/// <summary>
/// "Mantener activa", the Caffeine way: after a minute without input it presses F15 (no keyboard has it and no
/// app uses it). Windows takes it as someone at the keyboard, so the screen doesn't dim or lock, and Teams, Slack…
/// keep showing you as available.
/// </summary>
internal sealed class ActivityKeeper
{
    /// <summary>Under the shortest screen timeout (1 min) and well under Teams' "away" (5 min).</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(59);

    private DateTime _pressedUtc = DateTime.MinValue;
    private bool _failureLogged;

    /// <summary>Last keyboard or mouse input of the user: our own key presses left out.</summary>
    public DateTime UserInputUtc { get; private set; } = DateTime.MinValue;

    /// <summary>Last F15 press (or attempt).</summary>
    public DateTime PressedUtc => _pressedUtc;

    /// <summary>Called every tick. <paramref name="press"/>: Mantener activa is on and someone may see the screen.</summary>
    public void Tick(DateTime now, bool press)
    {
        var last = Native.LastInputUtc();
        // Our key press becomes the last input too: only an input at another moment is the user's.
        if ((last - _pressedUtc).Duration() > TimeSpan.FromSeconds(1)) UserInputUtc = last;
        if (!press || last == DateTime.MinValue || now - last < Interval || now - _pressedUtc < Interval) return;

        _pressedUtc = now; // also paces the retries when Windows refuses it
        if (Native.PressKey(Native.VK_F15))
        {
            _failureLogged = false;
        }
        else if (!_failureLogged)
        {
            _failureLogged = true; // locked session: Windows takes no input until it's unlocked
            Log.Warn("Mantener activa: Windows no aceptó la tecla F15 (¿sesión bloqueada?)");
        }
    }
}
