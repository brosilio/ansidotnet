using System;

namespace AnsiDotNet;

/// <summary>Best-effort capability detection from the environment, following NO_COLOR / FORCE_COLOR conventions.</summary>
public static class TerminalCapabilities
{
    public static ColorMode DetectColorMode() => DetectColorMode(!Console.IsOutputRedirected);

    public static ColorMode DetectColorMode(bool isTerminal)
    {
        string? force = Env("FORCE_COLOR");
        if (force is not null)
        {
            return force switch
            {
                "0" or "false" => ColorMode.None,
                "2" => ColorMode.Indexed256,
                "3" => ColorMode.TrueColor,
                _ => ColorMode.Basic16,
            };
        }
        if (Env("NO_COLOR") is not null || !isTerminal)
            return ColorMode.None;

        string term = Env("TERM") ?? "";
        if (term == "dumb")
            return ColorMode.None;

        string colorTerm = Env("COLORTERM") ?? "";
        if (colorTerm is "truecolor" or "24bit")
            return ColorMode.TrueColor;

        if (Env("WT_SESSION") is not null || Env("KITTY_WINDOW_ID") is not null || Env("WEZTERM_EXECUTABLE") is not null)
            return ColorMode.TrueColor;

        switch (Env("TERM_PROGRAM"))
        {
            case "iTerm.app":
            case "WezTerm":
            case "vscode":
            case "ghostty":
                return ColorMode.TrueColor;
            case "Apple_Terminal":
                return ColorMode.Indexed256;
        }

        if (term.Contains("direct") || term.Contains("kitty") || term.Contains("alacritty") ||
            term.Contains("foot") || term.Contains("ghostty") || term.Contains("wezterm"))
            return ColorMode.TrueColor;

        if (term.Contains("256"))
            return ColorMode.Indexed256;

        if (OperatingSystem.IsWindows())
            return Environment.OSVersion.Version.Build >= 14931 ? ColorMode.TrueColor : ColorMode.Basic16;

        return ColorMode.Basic16;
    }

    internal static TerminalSize EnvironmentSize()
    {
        int w = int.TryParse(Env("COLUMNS"), out var c) && c > 0 ? c : 80;
        int h = int.TryParse(Env("LINES"), out var l) && l > 0 ? l : 24;
        return new TerminalSize(w, h);
    }

    private static string? Env(string name)
    {
        string? v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrEmpty(v) ? null : v;
    }
}
