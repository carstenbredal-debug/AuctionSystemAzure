namespace AuctionSystem.Web.Services;

// Which password-gated sections are unlocked in THIS browser tab/app instance. Static = lives until the
// page is refreshed or the tab closed, so each section prompts once per session. The real enforcement is
// server-side (SECTION_PASSWORD_{SECTION} app settings) — this only drives the UI gates.
public static class SectionUnlockState
{
    private static readonly HashSet<string> Unlocked = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsUnlocked(string section) => Unlocked.Contains(section);
    public static void MarkUnlocked(string section) => Unlocked.Add(section);
}
