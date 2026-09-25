namespace KernelMK.Web.Components.Pages.Explorer;

public static class RemoteEntryName
{
    // Browser-provided file names and folder names must remain a single path segment
    // on both Unix servers and Windows shares (including alternate data streams).
    public static bool IsValid(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= 255 &&
        name is not "." and not ".." &&
        !name.EndsWith('.') && !name.EndsWith(' ') &&
        !name.Any(c => char.IsControl(c) || c is '/' or '\\' or ':' or '<' or '>' or '"' or '|' or '?' or '*');
}
