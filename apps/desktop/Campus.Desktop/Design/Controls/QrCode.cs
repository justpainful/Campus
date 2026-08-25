using Campus.Desktop.Design;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using QRCoder;

namespace Campus.Desktop.Design.Controls;

/// <summary>
/// A QR code, drawn as shapes rather than rasterised, so it stays crisp at any size.
///
/// Deliberately black on white in both themes. This is the one control in Campus that is not for
/// a person to look at — a camera reads it, and a camera wants dark modules on a light field.
/// Following the theme here is not restraint, it is a bug: the modules were drawn in
/// Label.Primary on Label.OnAccent, and in dark mode both of those are white.
/// </summary>
public sealed class QrCode : Grid
{
    public QrCode()
    {
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
    }

    public static readonly DependencyProperty PayloadProperty = DependencyProperty.Register(
        nameof(Payload), typeof(string), typeof(QrCode),
        new PropertyMetadata(string.Empty, (d, _) => ((QrCode)d).Rebuild()));

    public string Payload
    {
        get => (string)GetValue(PayloadProperty);
        set => SetValue(PayloadProperty, value);
    }

    public static readonly DependencyProperty ModuleSizeProperty = DependencyProperty.Register(
        nameof(ModuleSize), typeof(double), typeof(QrCode),
        new PropertyMetadata(6d, (d, _) => ((QrCode)d).Rebuild()));

    /// <summary>How many pixels one square of the code takes.</summary>
    public double ModuleSize
    {
        get => (double)GetValue(ModuleSizeProperty);
        set => SetValue(ModuleSizeProperty, value);
    }

    private void Rebuild()
    {
        Children.Clear();

        if (string.IsNullOrWhiteSpace(Payload)) return;

        // Error correction Q rather than L: this is read off a screen at an angle, often with a
        // finger or a reflection over part of it.
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(Payload, QRCodeGenerator.ECCLevel.Q);

        var matrix = data.ModuleMatrix;
        var size = matrix.Count;

        Width = size * ModuleSize;
        Height = size * ModuleSize;
        CornerRadius = new CornerRadius(4);

        // The light field the code sits on, which doubles as the quiet zone around it — part of
        // the specification, and a code without one is a code some scanners will not see.
        Background = (Brush)Application.Current.Resources[ThemeTokens.Machine.Paper];

        var dark = (Brush)Application.Current.Resources[ThemeTokens.Machine.Ink];
        var canvas = new Canvas { Width = Width, Height = Height };

        for (var y = 0; y < size; y++)
        {
            // One rectangle per run of dark modules rather than per module: a code of this size
            // is thousands of squares, and a thousand shapes is a slow page.
            var x = 0;
            while (x < size)
            {
                if (!matrix[y][x]) { x++; continue; }

                var start = x;
                while (x < size && matrix[y][x]) x++;

                var run = new Rectangle
                {
                    Width = (x - start) * ModuleSize,
                    Height = ModuleSize,
                    Fill = dark,
                };

                Canvas.SetLeft(run, start * ModuleSize);
                Canvas.SetTop(run, y * ModuleSize);
                canvas.Children.Add(run);
            }
        }

        Children.Add(canvas);
        AutomationProperties.SetName(this, L.T("pairing.code"));
    }
}
