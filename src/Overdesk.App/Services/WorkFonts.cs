using Avalonia.Media;

namespace Overdesk.App.Services;

/// <summary>One font of the catalog: shown by <see cref="Name"/>, loaded by <see cref="Family"/>.</summary>
internal sealed record WorkFont(string Name, string Category, string Family)
{
    public FontFamily Font { get; } = new(Family);
}

/// <summary>
/// The fonts Overdesk ships with: open-source families from Google Fonts (SIL Open Font License or Apache 2.0, see the
/// license files next to the font files) plus the app's own Inter. Works store font names, never file names.
/// </summary>
internal static class WorkFonts
{
    private const string Base = "avares://Overdesk.App/Assets/Fonts#";

    public static readonly string[] Categories = { "Serif", "Sans", "Handwriting", "Display", "Mono", "Game & fantasy" };

    public static readonly IReadOnlyList<WorkFont> All = new WorkFont[]
    {
        // Serif
        new("Playfair Display", "Serif", Base + "Playfair Display"),
        new("Cormorant Garamond", "Serif", Base + "Cormorant Garamond Light"),
        new("Lora", "Serif", Base + "Lora"),
        new("EB Garamond", "Serif", Base + "EB Garamond"),
        new("DM Serif Display", "Serif", Base + "DM Serif Display"),
        new("Young Serif", "Serif", Base + "Young Serif"),
        new("Italiana", "Serif", Base + "Italiana"),
        new("Marcellus", "Serif", Base + "Marcellus"),
        new("Cinzel", "Serif", Base + "Cinzel"),
        // Sans
        new("Inter", "Sans", "fonts:Inter#Inter"),
        new("Poppins", "Sans", Base + "Poppins"),
        new("Space Grotesk", "Sans", Base + "Space Grotesk Light"),
        new("Exo 2", "Sans", Base + "Exo 2"),
        new("Oswald", "Sans", Base + "Oswald"),
        // Handwriting
        new("Caveat", "Handwriting", Base + "Caveat"),
        new("Dancing Script", "Handwriting", Base + "Dancing Script"),
        new("Pacifico", "Handwriting", Base + "Pacifico"),
        new("Shadows Into Light", "Handwriting", Base + "Shadows Into Light"),
        new("Architects Daughter", "Handwriting", Base + "Architects Daughter"),
        new("Amatic SC", "Handwriting", Base + "Amatic SC"),
        new("Permanent Marker", "Handwriting", Base + "Permanent Marker"),
        new("Lobster", "Handwriting", Base + "Lobster"),
        // Display
        new("Abril Fatface", "Display", Base + "Abril Fatface"),
        new("Bebas Neue", "Display", Base + "Bebas Neue"),
        new("Righteous", "Display", Base + "Righteous"),
        new("Bungee", "Display", Base + "Bungee"),
        new("Audiowide", "Display", Base + "Audiowide"),
        new("Cinzel Decorative", "Display", Base + "Cinzel Decorative"),
        // Mono
        new("JetBrains Mono", "Mono", Base + "JetBrains Mono"),
        new("IBM Plex Mono", "Mono", Base + "IBM Plex Mono"),
        new("Special Elite", "Mono", Base + "Special Elite"),
        new("VT323", "Mono", Base + "VT323"),
        // Game & fantasy
        new("Press Start 2P", "Game & fantasy", Base + "Press Start 2P"),
        new("Silkscreen", "Game & fantasy", Base + "Silkscreen"),
        new("Orbitron", "Game & fantasy", Base + "Orbitron"),
        new("MedievalSharp", "Game & fantasy", Base + "MedievalSharp"),
        new("UnifrakturCook", "Game & fantasy", Base + "UnifrakturCook"),
        new("Creepster", "Game & fantasy", Base + "Creepster"),
        new("Rye", "Game & fantasy", Base + "Rye"),
    };

    public static WorkFont Find(string? name)
        => All.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)) ?? All.First(f => f.Name == "Inter");

    public static FontFamily Resolve(string? name) => Find(name).Font;
}
