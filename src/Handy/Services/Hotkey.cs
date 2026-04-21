using System;
using System.Collections.Generic;

namespace Handy.Services;

/// <summary>
/// Parsed chord like "Ctrl+Alt+Space": a base virtual-key plus required modifier flags.
/// </summary>
public readonly struct Hotkey
{
    [Flags] public enum Mods { None = 0, Ctrl = 1, Alt = 2, Shift = 4, Win = 8 }

    public Mods Required { get; }
    public uint Vk { get; }
    public string Display { get; }

    public Hotkey(Mods required, uint vk, string display)
    {
        Required = required;
        Vk = vk;
        Display = display;
    }

    public bool IsEmpty => Vk == 0;

    public static Hotkey Parse(string chord)
    {
        if (string.IsNullOrWhiteSpace(chord)) return default;

        var parts = chord.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var mods = Mods.None;
        uint vk = 0;

        foreach (var part in parts)
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl": case "control":    mods |= Mods.Ctrl; break;
                case "alt":                     mods |= Mods.Alt; break;
                case "shift":                   mods |= Mods.Shift; break;
                case "win": case "meta": case "super": mods |= Mods.Win; break;
                default:
                    if (KeyMap.TryGetValue(part.ToLowerInvariant(), out var mapped))
                        vk = mapped;
                    else if (part.Length == 1)
                        vk = char.ToUpperInvariant(part[0]); // A-Z, 0-9
                    break;
            }
        }

        return new Hotkey(mods, vk, chord);
    }

    // Upstream-aligned display names → Windows virtual-key codes.
    private static readonly Dictionary<string, uint> KeyMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["space"] = 0x20,
        ["enter"] = 0x0D, ["return"] = 0x0D,
        ["escape"] = 0x1B, ["esc"] = 0x1B,
        ["tab"] = 0x09,
        ["backspace"] = 0x08,
        ["delete"] = 0x2E,
        ["insert"] = 0x2D,
        ["home"] = 0x24, ["end"] = 0x23,
        ["pageup"] = 0x21, ["pagedown"] = 0x22,
        ["up"] = 0x26, ["down"] = 0x28, ["left"] = 0x25, ["right"] = 0x27,
        ["capslock"] = 0x14,
        ["numlock"] = 0x90,
        ["scrolllock"] = 0x91,
        ["printscreen"] = 0x2C,
        ["pause"] = 0x13,
        ["f1"] = 0x70, ["f2"] = 0x71, ["f3"] = 0x72, ["f4"] = 0x73,
        ["f5"] = 0x74, ["f6"] = 0x75, ["f7"] = 0x76, ["f8"] = 0x77,
        ["f9"] = 0x78, ["f10"] = 0x79, ["f11"] = 0x7A, ["f12"] = 0x7B,
    };
}
