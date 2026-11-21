using Avalonia.Media;

namespace SimpleAnalyzer.Controls;

/// <summary>The plot colours: SimpleHPSDR's default spectrum look (black background, blue line).</summary>
public sealed class SpectrumLook
{
    public static SpectrumLook Current { get; } = new();

    public IPen LinePen { get; } = new Pen(new SolidColorBrush(Color.Parse("#7FB2FF")), 1.2);
    public IBrush Background { get; } = new SolidColorBrush(Color.Parse("#0B0D11"));
}
