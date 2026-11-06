using System.Diagnostics;

namespace StreamArcTV.UI;

/// Opens a trailer address in the default browser.
public static class Trailer
{
    public static void Open(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) { Dialogs.Toast("The provider has no trailer for this title."); return; }
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { Dialogs.Toast("Couldn't open the trailer."); }
    }
}
