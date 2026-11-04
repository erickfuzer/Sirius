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
    public required IBrush RunBackground { get; init; }
    public required IBrush RunInk { get; init; }

    public static readonly Palette Claro = new(
        "claro", ThemeVariant.Light,
        B("#E8E9EC"), B("#FFFFFF"), B("#FFFFFF"), B("#16171A"), B("#5C5F67"), B("#6F727A"),
        B("#1216171A"), B("#8B2FC9"), B("#0B7A75"), B("#1F5FAF"), B("#2F7A3B"), B("#B45309"),
        B("#D93A00"), B("#33D93A00"), B("#4716171A"),
        B("#16171A"), B("#F5F5F3"), B("#B4B7BD"), B("#24FFFFFF"),
        BoxShadows.Parse("0 1 2 0 #0F141620, 0 14 36 0 #14141620"),
        BoxShadows.Parse("0 18 48 0 #38141620"))
    {
        RunBackground = B("#FF5A1F"),
        RunInk = B("#16171A"),
    };

    public static readonly Palette Grafite = new(
        "grafite", ThemeVariant.Dark,
        B("#0C0D0F"), B("#17181B"), B("#222327"), B("#F2F2F0"), B("#A6A9B0"), B("#8E9198"),
        B("#14FFFFFF"), B("#C792EA"), B("#5FD1C4"), B("#82AAFF"), B("#9FD5A0"), B("#F5A97F"),
        B("#FF6A2B"), B("#47FF6A2B"), B("#80000000"),
        B("#F2F2F0"), B("#16171A"), B("#55585F"), B("#1A16171A"),
        BoxShadows.Parse("0 0 0 1 #0DFFFFFF"),
        BoxShadows.Parse("0 18 48 0 #8C000000"))
    {
        RunBackground = B("#C93500"),
        RunInk = B("#FFFFFF"),
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
