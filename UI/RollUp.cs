using System.Windows;
using System.Windows.Interop;

namespace MySmdb;

/// <summary>
/// Minimizar "dentro de la aplicación" para las ventanas de herramientas (historial, búsqueda, monitor): en vez
/// de irse a la barra de tareas o quedar como el rectángulo diminuto que deja Windows, la ventana se pliega a su
/// barra de título, con el título legible, en la esquina inferior izquierda de la ventana principal. El mismo
/// botón de minimizar la vuelve a desplegar donde estaba.
/// </summary>
public static class RollUp
{
    private const int WmSysCommand = 0x0112, ScMinimize = 0xF020;
    private const double RolledWidth = 340, Gap = 6;

    private sealed record Saved(double Left, double Top, double Width, double Height, double MinWidth, double MinHeight,
        ResizeMode ResizeMode, WindowState State);

    private static readonly Dictionary<Window, Saved> Rolled = new();

    public static void Attach(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            if (PresentationSource.FromVisual(window) is HwndSource source)
                source.AddHook((IntPtr _, int message, IntPtr wParam, IntPtr _, ref bool handled) =>
                {
                    // El botón de minimizar (o Win+Abajo): se pliega o se despliega, sin llegar a minimizarse.
                    if (message == WmSysCommand && ((int)wParam.ToInt64() & 0xFFF0) == ScMinimize)
                    {
                        handled = true;
                        Toggle(window);
                    }
                    return IntPtr.Zero;
                });
        };
        window.Closed += (_, _) => Rolled.Remove(window);
    }

    public static bool IsRolled(Window window) => Rolled.ContainsKey(window);

    public static void Toggle(Window window)
    {
        if (Rolled.Remove(window, out var saved)) Expand(window, saved);
        else Collapse(window);
    }

    private static void Collapse(Window window)
    {
        var state = window.WindowState;
        window.WindowState = WindowState.Normal;
        var saved = new Saved(window.Left, window.Top, window.Width, window.Height, window.MinWidth, window.MinHeight, window.ResizeMode, state);

        if (window.Content is UIElement content) content.Visibility = Visibility.Collapsed;
        window.MinWidth = window.MinHeight = 0;
        window.ResizeMode = ResizeMode.CanMinimize;   // sin redimensionar ni maximizar mientras está plegada
        window.Width = RolledWidth;
        window.SizeToContent = SizeToContent.Height;   // sin contenido: solo la barra de título
        window.UpdateLayout();

        // Esquina inferior izquierda de la ventana principal; las plegadas se apilan hacia arriba.
        int index = Rolled.Keys.Count(w => w.Owner == window.Owner);
        Rolled[window] = saved;
        if (window.Owner is { IsLoaded: true } owner && PresentationSource.FromVisual(owner) is { CompositionTarget: { } target })
        {
            var corner = target.TransformFromDevice.Transform(owner.PointToScreen(new Point(0, owner.ActualHeight)));
            double height = Math.Max(window.ActualHeight, 30);
            window.Left = corner.X + Gap;
            window.Top = corner.Y - Gap - (index + 1) * (height + Gap) - 28;   // por encima de la barra de estado
        }
    }

    private static void Expand(Window window, Saved saved)
    {
        window.SizeToContent = SizeToContent.Manual;
        window.ResizeMode = saved.ResizeMode;
        window.MinWidth = saved.MinWidth;
        window.MinHeight = saved.MinHeight;
        window.Left = saved.Left;
        window.Top = saved.Top;
        window.Width = saved.Width;
        window.Height = saved.Height;
        if (window.Content is UIElement content) content.Visibility = Visibility.Visible;
        window.WindowState = saved.State;
        window.Activate();
    }
}
