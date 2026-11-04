using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;

namespace Sirius.Theming;

public sealed record Palette(
    string Name, ThemeVariant Variant,
    IBrush Ground, IBrush Sheet, IBrush Pop, IBrush Ink, IBrush Muted, IBrush Faint,
    IBrush Tint, IBrush Keyword, IBrush Type, IBrush Function, IBrush String, IBrush Number,
    IBrush Accent, IBrush Selection, IBrush Veil,
    IBrush CapsuleBackground, IBrush CapsuleInk, IBrush CapsuleMuted, IBrush CapsuleMark,
    BoxShadows SheetShadow, BoxShadows PopShadow)
{
    public required IBrush OnAccent { get; init; }
    public required IBrush RunBackground { get; init; }
    public required IBrush RunInk { get; init; }
    public required IBrush Error { get; init; }
    public required IBrush DiffAdded { get; init; }
    public required IBrush DiffRemoved { get; init; }
    public required IBrush TerminalError { get; init; }
    public required IBrush TerminalWarning { get; init; }

    public static readonly Palette Claro = new(
        "claro", ThemeVariant.Light,
        B("#E8E9EC"), B("#FFFFFF"), B("#FFFFFF"), B("#16171A"), B("#5C5F67"), B("#6F727A"),
        B("#1216171A"), B("#8B2FC9"), B("#0B7A75"), B("#1F5FAF"), B("#2F7A3B"), B("#B45309"),
        B("#3F5F8F"), B("#333F5F8F"), B("#4716171A"),
        B("#16171A"), B("#F5F5F3"), B("#B4B7BD"), B("#24FFFFFF"),
        BoxShadows.Parse("0 1 2 0 #0F141620, 0 14 36 0 #14141620"),
        BoxShadows.Parse("0 18 48 0 #38141620"))
    {
        OnAccent = B("#FFFFFF"),
        RunBackground = B("#C9D6EA"),
        RunInk = B("#16171A"),
        Error = B("#C62828"),
        DiffAdded = B("#262F7A3B"),
        DiffRemoved = B("#26C62828"),
        TerminalError = B("#FF6B6B"),
        TerminalWarning = B("#F5C65B"),
    };

    public static readonly Palette Grafite = new(
        "grafite", ThemeVariant.Dark,
        B("#0D0F13"), B("#16181D"), B("#21242B"), B("#ECEEF2"), B("#A3A8B3"), B("#8A8F9A"),
        B("#14FFFFFF"), B("#C792EA"), B("#5FD1C4"), B("#82AAFF"), B("#9FD5A0"), B("#F5A97F"),
        B("#9DB8E0"), B("#409DB8E0"), B("#80000000"),
        // A cápsula clara era forte demais no escuro: tom mais baixo, com leve transparência.
        B("#E0C8CCD4"), B("#16181D"), B("#3F4249"), B("#1A16181D"),
        BoxShadows.Parse("0 0 0 1 #0DFFFFFF"),
        BoxShadows.Parse("0 18 48 0 #8C000000"))
    {
        OnAccent = B("#0D0F13"),
        RunBackground = B("#3D5A85"),
        RunInk = B("#FFFFFF"),
        Error = B("#FF6B6B"),
        DiffAdded = B("#3A9FD5A0"),
        DiffRemoved = B("#3AFF6B6B"),
        TerminalError = B("#8F1414"),
        TerminalWarning = B("#5F4300"),
    };

    public static Palette Current { get; private set; } = Claro;

    public static void Apply(string name)
    {
        Current = name == Grafite.Name ? Grafite : Claro;
        var app = Application.Current;
        if (app is null)
            return;

        app.RequestedThemeVariant = Current.Variant;
        var r = app.Resources;
        r["Ground"] = Current.Ground;
        r["Sheet"] = Current.Sheet;
        r["Pop"] = Current.Pop;
        r["Ink"] = Current.Ink;
        r["Muted"] = Current.Muted;
        r["Faint"] = Current.Faint;
        r["Tint"] = Current.Tint;
        r["Accent"] = Current.Accent;
        r["Selection"] = Current.Selection;
        r["OnAccent"] = Current.OnAccent;
        r["Error"] = Current.Error;
        r["Veil"] = Current.Veil;
        r["CapsuleBackground"] = Current.CapsuleBackground;
        r["CapsuleInk"] = Current.CapsuleInk;
        r["CapsuleMuted"] = Current.CapsuleMuted;
        r["CapsuleMark"] = Current.CapsuleMark;
        r["RunBackground"] = Current.RunBackground;
        r["RunInk"] = Current.RunInk;
    }

    private static IBrush B(string color) => new ImmutableSolidColorBrush(Color.Parse(color));
}
