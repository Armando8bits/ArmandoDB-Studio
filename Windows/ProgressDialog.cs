using System.Windows;
using System.Windows.Controls;

namespace MySmdb;

/// <summary>Ventana de avance para operaciones largas; cerrarla o pulsar Cancelar cancela la operación.</summary>
public class ProgressDialog : Window
{
    private readonly ProgressBar _bar = new() { Height = 18, Minimum = 0, Maximum = 100 };
    private readonly TextBlock _text = new() { Margin = new Thickness(0, 0, 0, 8) };
    private bool _finished;
    private bool _closed;

    public ProgressDialog(Window owner, string title, CancellationTokenSource cancellation)
    {
        Owner = owner;
        Title = title;
        Width = 400;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var cancel = new Button
        {
            Content = "Cancelar",
            Width = 90,
            Padding = new Thickness(0, 4, 0, 4),
            Margin = new Thickness(0, 12, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        cancel.Click += (_, _) => Close();

        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(_text);
        panel.Children.Add(_bar);
        panel.Children.Add(cancel);
        Content = panel;

        Closing += (_, _) =>
        {
            if (!_finished) cancellation.Cancel();
        };
        Closed += (_, _) => _closed = true;
    }

    public void Report(int done, int total)
    {
        _bar.Value = total == 0 ? 100 : done * 100.0 / total;
        _text.Text = $"{done:N0} de {total:N0} filas";
    }

    public void Finish()
    {
        _finished = true;
        if (!_closed) Close();
    }
}
