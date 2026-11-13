using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DynamicIsland.UI;

/// <summary>Маленькое окно ввода города, чтобы не тащить полноценные настройки.</summary>
public sealed class CityDialog : Window
{
    private readonly TextBox _city;

    public string City => _city.Text.Trim();

    public CityDialog(string current)
    {
        Title = "Город для погоды";
        Width = 340;
        Height = 168;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        ShowInTaskbar = false;

        var border = new Border
        {
            CornerRadius = new CornerRadius(20),
            Background = new SolidColorBrush(Color.FromRgb(16, 17, 20)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(20, 16, 20, 16),
        };
        border.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        };

        var panel = new StackPanel();

        panel.Children.Add(new TextBlock
        {
            Text = "Город для погоды",
            Foreground = Brushes.White,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4),
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Оставьте пустым — определим по IP.",
            Foreground = new SolidColorBrush(Color.FromArgb(140, 255, 255, 255)),
            FontSize = 11,
            Margin = new Thickness(0, 0, 0, 12),
        });

        _city = new TextBox
        {
            Text = current,
            FontSize = 13,
            Padding = new Thickness(10, 7, 10, 7),
            Background = new SolidColorBrush(Color.FromArgb(18, 255, 255, 255)),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CaretBrush = Brushes.White,
        };
        _city.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { DialogResult = true; }
            else if (e.Key == Key.Escape) { DialogResult = false; }
        };
        panel.Children.Add(_city);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
        };

        buttons.Children.Add(MakeButton("Отмена", false, () => DialogResult = false));
        buttons.Children.Add(MakeButton("Сохранить", true, () => DialogResult = true));
        panel.Children.Add(buttons);

        border.Child = panel;
        Content = border;

        Loaded += (_, _) =>
        {
            _city.Focus();
            _city.SelectAll();
        };
    }

    private static Button MakeButton(string text, bool accent, Action click)
    {
        var button = new Button
        {
            Content = text,
            FontSize = 12,
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(8, 0, 0, 0),
            Foreground = accent ? new SolidColorBrush(Color.FromRgb(8, 10, 12)) : Brushes.White,
            Background = accent
                ? new SolidColorBrush(Color.FromRgb(127, 224, 255))
                : new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)),
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
        };
        button.Click += (_, _) => click();

        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetBinding(Border.BackgroundProperty,
            new System.Windows.Data.Binding(nameof(Background)) { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        border.SetBinding(Border.PaddingProperty,
            new System.Windows.Data.Binding(nameof(Padding)) { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        template.VisualTree = border;
        button.Template = template;
        return button;
    }
}
