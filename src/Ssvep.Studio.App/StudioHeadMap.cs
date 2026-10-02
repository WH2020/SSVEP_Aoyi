using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Ssvep.Speller.Core.Devices;
using Ssvep.Speller.Core.Studio;

namespace Ssvep.Studio.App;

public sealed class StudioHeadMap : UserControl
{
    private readonly Dictionary<string, (ToggleButton Button, TextBlock Channel, Ellipse Dot)> nodes = new();
    public event Action<string>? ElectrodeInvoked;
    public ToggleButton ElectrodeButton(string name) => nodes[StudioElectrodes.Normalize(name)].Button;
    private static Brush Color(string value) => (Brush)new BrushConverter().ConvertFromString(value)!;

    public StudioHeadMap()
    {
        var canvas = new Canvas { Width = 520, Height = 526 };
        Content = new Viewbox { Child = canvas, Stretch = Stretch.Uniform };
        var outline = Color("#B5C7CF");
        void Add(FrameworkElement item, double x, double y) { Canvas.SetLeft(item,x); Canvas.SetTop(item,y); canvas.Children.Add(item); }
        Add(new Ellipse { Width=414,Height=414,Stroke=outline,StrokeThickness=2,Fill=Color("#F7FAFB") },53,53);
        Add(new Ellipse { Width=23,Height=70,Stroke=outline,StrokeThickness=2 },29,225);
        Add(new Ellipse { Width=23,Height=70,Stroke=outline,StrokeThickness=2 },468,225);
        Add(new Polyline { Points=new PointCollection {new(242,55),new(260,28),new(278,55)},Stroke=outline,StrokeThickness=2 },0,0);
        Add(new Line { X1=260,Y1=80,X2=260,Y2=445,Stroke=Color("#E1E9ED"),StrokeDashArray=[3,5] },0,0);
        Add(new Line { X1=78,Y1=260,X2=442,Y2=260,Stroke=Color("#E1E9ED"),StrokeDashArray=[3,5] },0,0);
        void Label(string text,double x,double y) => Add(new TextBlock {Text=text,FontSize=13,Foreground=Color("#66788B")},x,y);
        Label("鼻尖 / 前",232,4); Label("左",3,250); Label("右",501,250); Label("枕部 / 后",232,500);
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty,new CornerRadius(23));
        border.SetValue(Border.BackgroundProperty,new TemplateBindingExtension(BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty,new TemplateBindingExtension(BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty,new TemplateBindingExtension(BorderThicknessProperty));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty,HorizontalAlignment.Center);
        presenter.SetValue(VerticalAlignmentProperty,VerticalAlignment.Center);
        border.AppendChild(presenter);
        var template = new ControlTemplate(typeof(ToggleButton)) {VisualTree=border};
        foreach (var site in StudioElectrodes.Positions)
        {
            var label = new TextBlock {Text=site.Name,FontSize=14,FontWeight=FontWeights.SemiBold,HorizontalAlignment=HorizontalAlignment.Center};
            var channel = new TextBlock {Text="—",FontSize=10,HorizontalAlignment=HorizontalAlignment.Center};
            var stack = new StackPanel(); stack.Children.Add(label); stack.Children.Add(channel);
            var button = new ToggleButton { Width=40,Height=40,Content=stack,Template=template,Cursor=Cursors.Hand,
                Tag=site.Name,Margin=new Thickness(0),BorderThickness=new Thickness(1),FocusVisualStyle=null };
            // WPF button semantics retain keyboard focus/Space activation for every available electrode.
            button.IsKeyboardFocusedChanged += (_, _) => button.BorderThickness = new Thickness(button.IsKeyboardFocused ? 3 : button.IsChecked == true ? 2 : 1);
            button.Click += (_, _) => ElectrodeInvoked?.Invoke(site.Name);
            ToolTipService.SetShowOnDisabled(button,true);
            Add(button,site.X-20,site.Y-20);
            var dot = new Ellipse {Width=8,Height=8,Stroke=Brushes.White,StrokeThickness=1,IsHitTestVisible=false};
            Add(dot,site.X+11,site.Y+9);
            nodes.Add(site.Name,(button,channel,dot));
        }
    }

    public void Refresh(StudioChannelSetup setup, IReadOnlyList<ChannelImpedance> impedance, bool simulation,
        bool fresh, bool connected, bool locked, bool editing, bool showExtended)
    {
        foreach (var site in StudioElectrodes.Positions)
        {
            var node = nodes[site.Name];
            var binding = setup.Bindings.FirstOrDefault(b => b.Electrode == site.Name && b.ChannelIndex < setup.ChannelCount);
            bool selected = binding is not null && setup.SelectedChannels.Contains(binding.ChannelIndex);
            // A mapped intermediate site stays visible even with the optional unbound sites hidden.
            bool visible = !site.Extended || showExtended || binding is not null;
            node.Button.Visibility = node.Dot.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            node.Button.IsEnabled = !locked && (editing || binding is not null);
            node.Button.IsChecked = selected;
            node.Button.Background = Color(selected ? "#D9F0EB" : binding is not null ? "#FFFFFF" : "#F1F4F6");
            node.Button.BorderBrush = Color(selected ? "#146C63" : site.Extended ? "#B7C7D9" : "#9BAFB8");
            node.Button.BorderThickness = new Thickness(node.Button.IsKeyboardFocused ? 3 : selected ? 2 : 1);
            node.Button.Foreground = Color(binding is not null ? "#183E43" : "#8A98A8");
            node.Channel.Text = binding is null ? "未指定" : $"CH{binding.ChannelIndex+1}";
            node.Channel.Visibility = binding is null ? Visibility.Collapsed : Visibility.Visible;
            var value = binding is null ? null : impedance.FirstOrDefault(v => v.ChannelIndex == binding.ChannelIndex);
            bool measured = !simulation && connected && fresh && value is not null
                && value.Quality != ImpedanceQuality.Unknown && double.IsFinite(value.KiloOhms) && value.KiloOhms > 0;
            node.Dot.Fill = Color(!measured ? "#A6B3C0" : value!.KiloOhms > StudioRuntimeProfile.MaximumImpedanceKiloOhms ? "#D98A25" : "#249C86");
            node.Dot.Visibility = visible && binding is not null ? Visibility.Visible : Visibility.Collapsed;
            string status = simulation ? "模拟 · 未测量" : measured ? $"{value!.KiloOhms:0.#} kΩ" : "未知 / 待更新";
            string description = binding is null ? $"{site.Name} · 尚未指定通道" : $"{site.Name} · CH{binding.ChannelIndex+1} · {(selected ? "已选择" : "未选择")} · {status}";
            if (site.Extended) description += " · 10–10 扩展位置";
            node.Button.ToolTip = description;
            AutomationProperties.SetName(node.Button,description);
        }
    }
}
