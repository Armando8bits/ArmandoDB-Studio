using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using ICSharpCode.AvalonEdit;

namespace MySmdb;

/// <summary>
/// Buscar y reemplazar (Ctrl+Mayús+H) sobre el editor de la pestaña activa. Ventana no modal: se puede seguir
/// editando, y si cambias de pestaña actúa sobre la nueva.
/// </summary>
public class ReplaceDialog : Window
{
    private readonly Func<TextEditor?> _editor;
    private readonly TextBox _find = new() { Margin = new Thickness(0, 4, 0, 4) };
    private readonly TextBox _replace = new() { Margin = new Thickness(0, 4, 0, 4) };
    private readonly CheckBox _matchCase = new() { Content = "Coincidir mayúsculas y minúsculas", Margin = new Thickness(0, 2, 0, 2) };
    private readonly CheckBox _wholeWord = new() { Content = "Palabra completa", Margin = new Thickness(0, 2, 0, 2) };
    private readonly CheckBox _regex = new() { Content = "Expresión regular", Margin = new Thickness(0, 2, 0, 2) };
    private readonly TextBlock _status = new() { Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap, MinHeight = 20 };

    public ReplaceDialog(Window owner, Func<TextEditor?> editor)
    {
        _editor = editor;
        Owner = owner;
        Title = "Buscar y reemplazar";
        Width = 470;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var grid = new Grid { Margin = new Thickness(14) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < 4; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        void Place(UIElement element, int row, int column, int span = 1)
        {
            Grid.SetRow(element, row);
            Grid.SetColumn(element, column);
            Grid.SetColumnSpan(element, span);
            grid.Children.Add(element);
        }

        Place(new Label { Content = "Buscar:", VerticalAlignment = VerticalAlignment.Center }, 0, 0);
        Place(_find, 0, 1);
        Place(new Label { Content = "Reemplazar por:", VerticalAlignment = VerticalAlignment.Center }, 1, 0);
        Place(_replace, 1, 1);

        var options = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        options.Children.Add(_matchCase);
        options.Children.Add(_wholeWord);
        options.Children.Add(_regex);
        Place(options, 2, 1);

        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        Button AddButton(string text, Action action, bool isDefault = false)
        {
            var button = new Button { Content = text, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(10, 4, 10, 4), IsDefault = isDefault };
            button.Click += (_, _) => action();
            buttons.Children.Add(button);
            return button;
        }
        AddButton("Buscar siguiente", () => FindNext(), isDefault: true);
        AddButton("Reemplazar", ReplaceCurrent);
        AddButton("Reemplazar todo", ReplaceAll);
        AddButton("Cerrar", Close).IsCancel = true;
        Place(buttons, 3, 0, 2);

        var root = new StackPanel();
        root.Children.Add(grid);
        _status.Margin = new Thickness(14, 0, 14, 12);
        root.Children.Add(_status);
        Content = root;

        Activated += (_, _) => { _find.Focus(); _find.SelectAll(); };
    }

    /// <summary>Precarga el texto seleccionado en el editor (si es de una sola línea).</summary>
    public void Prefill(string? selection)
    {
        if (!string.IsNullOrEmpty(selection) && !selection.Contains('\n'))
            _find.Text = selection;
    }

    private Regex? BuildRegex()
    {
        if (_find.Text.Length == 0)
        {
            SetStatus("Escribe el texto a buscar.", error: true);
            return null;
        }
        string pattern = _regex.IsChecked == true ? _find.Text : Regex.Escape(_find.Text);
        if (_wholeWord.IsChecked == true) pattern = $@"\b(?:{pattern})\b";
        var options = RegexOptions.Multiline | (_matchCase.IsChecked == true ? RegexOptions.None : RegexOptions.IgnoreCase);
        try
        {
            return new Regex(pattern, options);
        }
        catch (ArgumentException ex)
        {
            SetStatus("Expresión regular no válida: " + ex.Message, error: true);
            return null;
        }
    }

    /// <summary>Texto de reemplazo de una coincidencia: con $1, $2... si es expresión regular; literal si no.</summary>
    private string Replacement(Match match) => _regex.IsChecked == true ? match.Result(_replace.Text) : _replace.Text;

    private bool FindNext(bool quiet = false)
    {
        var editor = _editor();
        if (editor == null || BuildRegex() is not { } regex) return false;

        int start = editor.SelectionStart + editor.SelectionLength;
        var match = regex.Match(editor.Text, Math.Min(start, editor.Text.Length));
        bool wrapped = false;
        if (!match.Success)
        {
            match = regex.Match(editor.Text, 0);
            wrapped = true;
        }
        if (!match.Success)
        {
            SetStatus("No se encontraron coincidencias.", error: true);
            return false;
        }

        editor.Select(match.Index, match.Length);
        var location = editor.Document.GetLocation(match.Index);
        editor.ScrollTo(location.Line, location.Column);
        if (!quiet) SetStatus(wrapped ? "Se llegó al final; se continuó desde el principio." : "");
        return true;
    }

    private void ReplaceCurrent()
    {
        var editor = _editor();
        if (editor == null || BuildRegex() is not { } regex) return;

        // Solo se reemplaza si lo seleccionado es exactamente una coincidencia; si no, primero se busca.
        var match = regex.Match(editor.SelectedText);
        if (editor.SelectionLength > 0 && match.Success && match.Index == 0 && match.Length == editor.SelectionLength)
        {
            int start = editor.SelectionStart;
            string replacement = Replacement(match);
            editor.Document.Replace(start, editor.SelectionLength, replacement);
            editor.Select(start + replacement.Length, 0);
            SetStatus("Reemplazado.");
        }
        FindNext(quiet: true);
    }

    private void ReplaceAll()
    {
        var editor = _editor();
        if (editor == null || BuildRegex() is not { } regex) return;

        int count = regex.Matches(editor.Text).Count;
        if (count == 0)
        {
            SetStatus("No se encontraron coincidencias.", error: true);
            return;
        }
        // Un único cambio en el documento: se deshace de una vez con Ctrl+Z.
        string result = regex.Replace(editor.Text, Replacement);
        editor.Document.Replace(0, editor.Document.TextLength, result);
        SetStatus($"{count} reemplazos.");
    }

    private void SetStatus(string text, bool error = false)
    {
        _status.Text = text;
        _status.SetResourceReference(TextBlock.ForegroundProperty, error ? "Brush.ErrorText" : "Brush.SecondaryText");
    }
}
