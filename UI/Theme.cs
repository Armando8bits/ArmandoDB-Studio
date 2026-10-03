using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Highlighting;
using Microsoft.Win32;

namespace MySmdb;

public enum ThemeChoice { System, Light, Dark }

/// <summary>
/// Tema de la aplicación. Los controles estándar los pinta el tema Fluent de WPF;
/// los colores propios (pestañas, barra de estado, cuadrícula, editor...) salen de una paleta
/// publicada como recursos "Brush.*", que se usan con DynamicResource / SetResourceReference.
/// </summary>
public static class Theme
{
    // clave → (claro, oscuro)
    private static readonly Dictionary<string, (string Light, string Dark)> Palette = new()
    {
        ["Brush.StatusBar"] = ("#FFF29D", "#3E3E42"),
        ["Brush.StatusBarText"] = ("#000000", "#F1F1F1"),
        ["Brush.Splitter"] = ("#D6DBE9", "#3F3F46"),
        ["Brush.TabStrip"] = ("#EEEEF2", "#2D2D30"),
        ["Brush.TabStripBorder"] = ("#CCCEDB", "#3F3F46"),
        ["Brush.TabStripPinned"] = ("#DCE4F2", "#26323F"),
        ["Brush.TabSelected"] = ("#FFFFFF", "#1E1E1E"),
        ["Brush.TabAccent"] = ("#007ACC", "#3794FF"),
        ["Brush.TabInactiveAccent"] = ("#A9A9A9", "#6A6A6A"),
        ["Brush.SecondaryText"] = ("#696969", "#A0A0A0"),
        ["Brush.GridBackground"] = ("#FFFFFF", "#1E1E1E"),
        ["Brush.GridLines"] = ("#DCDCDC", "#3A3A3A"),
        ["Brush.GridAlternate"] = ("#F1F5FB", "#262A33"),
        ["Brush.EditorBackground"] = ("#FFFFFF", "#1E1E1E"),
        ["Brush.EditorForeground"] = ("#000000", "#DCDCDC"),
        ["Brush.EditorLineNumbers"] = ("#2B91AF", "#858585"),
        ["Brush.EditorSelection"] = ("#ADD6FF", "#264F78"),
        ["Brush.ErrorText"] = ("#B22222", "#F48771"),
        ["Brush.OkText"] = ("#1E7B34", "#6CCB7A"),
        ["Brush.PanelBackground"] = ("#F3F5F9", "#252526"),
        ["Brush.PanelBorder"] = ("#D6DBE9", "#3F3F46"),
        // Conexiones de producción
        ["Brush.ProdAccent"] = ("#C42B1C", "#E5534B"),
        ["Brush.ProdText"] = ("#C42B1C", "#FF7B72"),
        ["Brush.ProdStatusBar"] = ("#C42B1C", "#8E1F17"),
        ["Brush.ProdStatusBarText"] = ("#FFFFFF", "#FFFFFF"),
    };

    // Colores del resaltado SQL (nombres de la definición TSQL de AvalonEdit): (claro, oscuro)
    private static readonly Dictionary<string, (string Light, string Dark)> SyntaxPalette = new()
    {
        ["Comment"] = ("#008000", "#57A64A"),
        ["Char"] = ("#FF0000", "#D69D85"),
        ["Keywords"] = ("#0000FF", "#569CD6"),
    };

    private static bool _listening;

    public static ThemeChoice Choice { get; private set; } = ThemeChoice.System;

    /// <summary>true si lo que se ve es oscuro (elegido, o heredado de Windows).</summary>
    public static bool IsDark { get; private set; }

    /// <summary>Se dispara tras aplicar un tema, para lo que no se actualiza solo (editores, colorizadores).</summary>
    public static event Action? Changed;

    public static ThemeChoice Parse(string? value) =>
        Enum.TryParse(value, ignoreCase: true, out ThemeChoice choice) ? choice : ThemeChoice.System;

    public static void Apply(ThemeChoice choice)
    {
        var app = Application.Current;
        Choice = choice;
        IsDark = choice == ThemeChoice.Dark || (choice == ThemeChoice.System && WindowsUsesDarkTheme());

#pragma warning disable WPF0001 // ThemeMode está marcado como experimental en WPF.
        app.ThemeMode = choice switch
        {
            ThemeChoice.Light => ThemeMode.Light,
            ThemeChoice.Dark => ThemeMode.Dark,
            _ => ThemeMode.System,
        };
#pragma warning restore WPF0001

        foreach (var (key, colors) in Palette)
            app.Resources[key] = Frozen(IsDark ? colors.Dark : colors.Light);

        var sql = HighlightingManager.Instance.GetDefinition("TSQL");
        if (sql != null)
        {
            foreach (var (name, colors) in SyntaxPalette)
            {
                var color = sql.GetNamedColor(name);
                if (color is { IsFrozen: false })
                    color.Foreground = new SimpleHighlightingBrush(ParseColor(IsDark ? colors.Dark : colors.Light));
            }
        }

        // En "Según Windows", seguir los cambios de tema del sistema mientras la aplicación está abierta.
        if (!_listening)
        {
            _listening = true;
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (e.Category == UserPreferenceCategory.General && Choice == ThemeChoice.System)
                    app.Dispatcher.BeginInvoke(() =>
                    {
                        if (WindowsUsesDarkTheme() != IsDark) Apply(ThemeChoice.System);
                    });
            };
        }

        Changed?.Invoke();
    }

    public static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);

    private static Color ParseColor(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    private static SolidColorBrush Frozen(string hex)
    {
        var brush = new SolidColorBrush(ParseColor(hex));
        brush.Freeze();
        return brush;
    }

    private static bool WindowsUsesDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch
        {
            return false;
        }
    }
}
