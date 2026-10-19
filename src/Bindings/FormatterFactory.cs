using Godot;
using System;
using System.Collections.Generic;
using System.Linq.Expressions;

namespace GIoC.Bindings;

/// <summary>
/// Registry of pre-compiled value formatters used by the Links binding system.
/// Register custom formatters in your Bootloader:
///   FormatterFactory.Register("MyFmt", x => $"Value: {x}");
/// </summary>
public static class FormatterFactory
{
    private static readonly Dictionary<string, Func<object, object>> _formatters = new()
    {
        ["None"] = null,

        // String
        ["ToString"]  = x => x?.ToString(),
        ["ToUpper"]   = x => x?.ToString()?.ToUpper(),
        ["ToLower"]   = x => x?.ToString()?.ToLower(),
        ["Trim"]      = x => x?.ToString()?.Trim(),

        // Numeric
        ["Multiply100"] = x => Convert.ToDouble(x) * 100,
        ["Divide100"]   = x => Convert.ToDouble(x) / 100.0,
        ["Multiply10"]  = x => Convert.ToDouble(x) * 10,
        ["Divide10"]    = x => Convert.ToDouble(x) / 10.0,
        ["Round"]       = x => Math.Round(Convert.ToDouble(x)),
        ["Floor"]       = x => Math.Floor(Convert.ToDouble(x)),
        ["Ceiling"]     = x => Math.Ceiling(Convert.ToDouble(x)),
        ["Abs"]         = x => Math.Abs(Convert.ToDouble(x)),
        ["Negate"]      = x => -Convert.ToDouble(x),

        // Percentage
        ["ToPercent"]      = x => $"{Convert.ToDouble(x) * 100}%",
        ["ToPercentWhole"] = x => $"{Math.Round(Convert.ToDouble(x) * 100)}%",
        ["FromPercent"]    = x => Convert.ToDouble(x) / 100.0,

        // Boolean
        ["ToBool"]          = x => Convert.ToBoolean(x),
        ["Invert"]          = x => !Convert.ToBoolean(x),
        ["ToYesNo"]         = x => Convert.ToBoolean(x) ? "Yes" : "No",
        ["ToOnOff"]         = x => Convert.ToBoolean(x) ? "On" : "Off",
        ["ToEnabledDisabled"] = x => Convert.ToBoolean(x) ? "Enabled" : "Disabled",

        // Number formatting
        ["TwoDecimals"]   = x => $"{Convert.ToDouble(x):F2}",
        ["OneDecimal"]    = x => $"{Convert.ToDouble(x):F1}",
        ["ThreeDecimals"] = x => $"{Convert.ToDouble(x):F3}",
        ["NoDecimals"]    = x => $"{Convert.ToDouble(x):F0}",
        ["Currency"]      = x => $"${Convert.ToDouble(x):F2}",
        ["CurrencyWhole"] = x => $"${Convert.ToDouble(x):F0}",

        // String formatting
        ["PadLeft3"]  = x => x?.ToString()?.PadLeft(3, '0'),
        ["PadLeft5"]  = x => x?.ToString()?.PadLeft(5, '0'),
        ["PadRight10"] = x => x?.ToString()?.PadRight(10),

        // Game — health
        ["HealthFormat"]  = x => $"HP: {x}/100",
        ["HealthPercent"] = x => $"HP: {Convert.ToInt32(x)}%",
        ["HealthBar"] = x =>
        {
            var hp = Convert.ToInt32(x);
            var bars = hp / 10;
            return new string('█', bars) + new string('░', 10 - bars);
        },
        ["HealthWithColor"] = x =>
        {
            var hp = Convert.ToInt32(x);
            var color = hp > 50 ? "green" : hp > 20 ? "yellow" : "red";
            return $"[color={color}]{hp}[/color]";
        },

        // Game — misc
        ["DamageFormat"] = x => $"-{x} DMG",
        ["LevelFormat"]  = x => $"Lvl {x}",
        ["XPFormat"]     = x => $"{x} XP",
        ["CoinFormat"]   = x => $"{x} coins",
        ["ScoreFormat"]  = x => $"Score: {x}",

        // Time
        ["TimeMMSS"] = x =>
        {
            var s = Convert.ToDouble(x);
            return $"{(int)(s / 60):00}:{(int)(s % 60):00}";
        },
        ["TimeHHMMSS"] = x =>
        {
            var t = Convert.ToDouble(x);
            return $"{(int)(t / 3600):00}:{(int)((t % 3600) / 60):00}:{(int)(t % 60):00}";
        },

        // Conditional
        ["HighLow"]  = x => Convert.ToDouble(x) > 50 ? "High" : "Low",
        ["PassFail"] = x => Convert.ToDouble(x) >= 60 ? "Pass" : "Fail",

        // RichTextLabel color tags
        ["ColorGreen"]  = x => $"[color=green]{x}[/color]",
        ["ColorRed"]    = x => $"[color=red]{x}[/color]",
        ["ColorYellow"] = x => $"[color=yellow]{x}[/color]",
        ["ColorBlue"]   = x => $"[color=blue]{x}[/color]",

        // Clamping
        ["Clamp0to100"] = x => Math.Clamp(Convert.ToDouble(x), 0, 100),
        ["Clamp0to1"]   = x => Math.Clamp(Convert.ToDouble(x), 0, 1),

        // Godot types
        ["Vector2ToString"] = x => x is Vector2 v2 ? $"({v2.X:F1}, {v2.Y:F1})" : x?.ToString(),
        ["Vector3ToString"] = x => x is Vector3 v3 ? $"({v3.X:F1}, {v3.Y:F1}, {v3.Z:F1})" : x?.ToString(),
        ["ColorToHex"]      = x => x is Color c ? c.ToHtml() : x?.ToString(),
        ["StringToColor"]   = x =>
        {
            var s = x?.ToString() ?? "";
            if (s.StartsWith('#')) return Color.FromString(s, Colors.White);
            uint hash = (uint)s.GetHashCode();
            float h = (hash & 0xFFFFu) / (float)0xFFFFu;
            return Color.FromHsv(h, 0.70f, 0.90f);
        },

        // Resource loaders
        ["StringToTexture"]     = x => (Texture2D)GD.Load(x?.ToString() ?? ""),
        ["StringToMaterial"]    = x => (Material)GD.Load(x?.ToString() ?? ""),
        ["StringToPackedScene"] = x => (PackedScene)GD.Load(x?.ToString() ?? ""),
        ["StringToAudioStream"] = x => (AudioStream)GD.Load(x?.ToString() ?? ""),
    };

    private static readonly Dictionary<string, Func<object, object>> _custom = new();

    public static Func<object, object> Get(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name == "None") return null;
        if (_formatters.TryGetValue(name, out var f)) return f;
        if (_custom.TryGetValue(name, out var c)) return c;
        return null;
    }

    public static void Register(string name, Func<object, object> formatter) =>
        _custom[name] = formatter;

    public static void RegisterTyped<TIn, TOut>(string name, Expression<Func<TIn, TOut>> expression)
    {
        var compiled = expression.Compile();
        _custom[name] = obj => compiled((TIn)obj);
    }

    public static IEnumerable<string> GetAllFormatterNames()
    {
        foreach (var k in _formatters.Keys) yield return k;
        foreach (var k in _custom.Keys) yield return k;
    }

    public static bool Has(string name) =>
        _formatters.ContainsKey(name) || _custom.ContainsKey(name);

    public static int Count => _formatters.Count + _custom.Count;
}
