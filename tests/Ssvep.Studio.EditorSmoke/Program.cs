using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Ssvep.Speller.Core.Studio;
using Ssvep.Studio.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        string output = Path.GetFullPath(args.FirstOrDefault() ?? "outputs/studio-development/editor-checks");
        Directory.CreateDirectory(output);
        var app = new Application();
        MainWindow? window = null;
        try
        {
            window = new MainWindow { Width = 1280, Height = 840 };
            void Check(bool value, string message) { if (!value) throw new Exception(message); }
            var image = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null,
                new byte[] { 20,180,30,255, 20,180,30,255, 20,180,30,255, 20,180,30,255 }, 8);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using var bytes = new MemoryStream(); encoder.Save(bytes);
            var content = StudioContentService.CreateDefault();
            var targets = content.Targets.ToArray();
            targets[0] = targets[0] with { Kind = StudioContentKind.Image, ImageBase64 = Convert.ToBase64String(bytes.ToArray()) };
            string imported = Path.Combine(output, "image.ssvep.json");
            StudioContentService.Save(imported, content with { Targets = targets });
            window.LoadContent(imported);
            Check(((ComboBox)window.FindName("KindCombo")).SelectedIndex == 2, "Image type not loaded in editor");
            ((ComboBox)window.FindName("KindCombo")).SelectedIndex = 0;
            ((TextBox)window.FindName("TargetText")).Text = "新的文字";
            string saved = Path.Combine(output, "edited.ssvep.json");
            window.SaveContent(saved);
            var restored = StudioContentService.Load(saved);
            Check(restored.Targets[0].Text == "新的文字" && restored.Targets[0].ImageBase64 == null,
                "Changing image to text must clear incompatible image payload");
            ((ComboBox)window.FindName("KindCombo")).SelectedIndex = 1;
            ((ComboBox)window.FindName("ShapeCombo")).SelectedIndex = 2;
            window.SaveContent(saved);
            restored = StudioContentService.Load(saved);
            Check(restored.Targets[0].Kind == StudioContentKind.Shape && restored.Targets[0].Shape == StudioShape.Diamond,
                "Shape editor did not persist diamond");

            targets[0] = targets[0] with { ImageBase64 = Convert.ToBase64String(new byte[] {137,80,78,71,13,10,26,10}) };
            string corrupt = Path.Combine(output, "corrupt.ssvep.json");
            StudioContentService.Save(corrupt, content with { Targets = targets });
            bool rejected = false;
            try { window.LoadContent(corrupt); } catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException or FormatException) { rejected = true; }
            Check(rejected, "Image decoder must reject signature-only corrupt image");
            Check(((ComboBox)window.FindName("KindCombo")).SelectedIndex == 1, "Failed image import replaced current content");

            foreach (var size in new[] { new Size(1280,840), new Size(1000,680) })
            {
                var root = (FrameworkElement)window.Content;
                root.Measure(size); root.Arrange(new Rect(size)); root.UpdateLayout();
                var snapshot = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96,96,PixelFormats.Pbgra32);
                snapshot.Render(root);
                var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(snapshot));
                using var file = File.Create(Path.Combine(output, $"editor-{size.Width}.png")); png.Save(file);
            }
            File.WriteAllText(Path.Combine(output,"result.txt"), "PASS: actual editor image decode, image-to-text cleanup, shape persistence, corrupt image rejection without replacing current scheme, desktop/minimum-size rendering.");
            return 0;
        }
        catch(Exception ex) { File.WriteAllText(Path.Combine(output,"result.txt"),ex.ToString()); Console.Error.WriteLine(ex); return 1; }
        finally { if (window is not null) window.Runtime.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }
}
