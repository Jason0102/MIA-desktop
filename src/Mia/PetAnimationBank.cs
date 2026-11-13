using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Mia;

internal sealed class PetAnimationBank
{
    internal sealed record Frame(BitmapSource Image,byte[] Alpha,int Width,int Height);
    internal sealed record Clip(string Name,Frame[] Frames,double Fps,double AnchorY,double GroundAnchorY);
    private readonly Dictionary<string,Clip> clips=new(StringComparer.Ordinal);
    internal string[] Names=>clips.Keys.ToArray();
    internal Clip? Get(string name)=>clips.GetValueOrDefault(name);
    internal PetAnimationBank(string directory)
    {
        string manifest=Path.Combine(directory,"manifest.json");
        if(!File.Exists(manifest))return;
        try
        {
            using var json=JsonDocument.Parse(File.ReadAllText(manifest));
            foreach(var entry in json.RootElement.GetProperty("clips").EnumerateArray())
            {
                try
                {
                    string name=entry.GetProperty("name").GetString()??"";
                    if(name is not ("perched" or "carried" or "falling" or "landing" or "petting" or "listening" or "speaking" or "stretching" or "kicking" or "pawing" or "pounce" or "ball-carry" or "ball-toss" or "ball-rest" or "headball" or "walking" or "ear-groom" or "collar-tidy" or "hand-warm" or "yawn" or "proud-nod" or "jump-up") && !TimedCompanion.IsActivity(name))continue;
                    string file=entry.GetProperty("file").GetString()??"";
                    if(Path.GetFileName(file)!=file || !file.EndsWith(".png",StringComparison.OrdinalIgnoreCase))continue;
                    int width=entry.GetProperty("frameWidth").GetInt32(),height=entry.GetProperty("frameHeight").GetInt32();
                    int count=entry.GetProperty("count").GetInt32();
                    double fps=entry.GetProperty("fps").GetDouble(),anchor=entry.GetProperty("anchorY").GetDouble();
                    double ground=entry.TryGetProperty("groundAnchorY",out var g)?g.GetDouble():203;
                    if(width<192 || width>1536 || height<208 || height>1664 || Math.Abs((double)width/height-192.0/208)>.02 || count<1 || count>32 || !double.IsFinite(fps) || fps<=0 || fps>60 || anchor<1 || anchor>208 || ground<1 || ground>208)continue;
                    var image=new BitmapImage();image.BeginInit();image.CacheOption=BitmapCacheOption.OnLoad;
                    image.UriSource=new Uri(Path.GetFullPath(Path.Combine(directory,file)));image.EndInit();image.Freeze();
                    if(image.PixelWidth!=width*count || image.PixelHeight!=height)continue;
                    var frames=new Frame[count];
                    for(int i=0;i<count;i++)
                    {
                        var crop=new CroppedBitmap(image,new Int32Rect(i*width,0,width,height));crop.Freeze();
                        frames[i]=CreateFrame(crop);
                    }
                    clips[name]=new Clip(name,frames,fps,anchor,ground);
                }
                catch(Exception ex) when(ex is IOException or ArgumentException or InvalidOperationException or KeyNotFoundException or FormatException or NotSupportedException) { }
            }
        }
        catch(Exception ex) when(ex is IOException or JsonException or InvalidOperationException or KeyNotFoundException) { }
        try { LoadSpeechAtlases(directory); }
        catch(Exception ex) when(ex is IOException or JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException or NotSupportedException) { }
    }

    private void LoadSpeechAtlases(string directory)
    {
        string manifest = Path.Combine(directory, "speech-atlases.json");
        if (!File.Exists(manifest)) return;
        using var document = JsonDocument.Parse(File.ReadAllText(manifest));
        foreach (var atlas in document.RootElement.EnumerateArray())
        {
            string file = atlas.GetProperty("file").GetString()!;
            if (Path.GetFileName(file) != file) continue;
            int columns = atlas.GetProperty("columns").GetInt32();
            int rows = atlas.GetProperty("rows").GetInt32();
            var xBounds = atlas.GetProperty("xBounds").EnumerateArray().Select(x => x.GetInt32()).ToArray();
            var yBounds = atlas.GetProperty("yBounds").EnumerateArray().Select(y => y.GetInt32()).ToArray();
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(Path.GetFullPath(Path.Combine(directory, file)));
            image.EndInit();
            image.Freeze();
            var tags = atlas.GetProperty("tags").EnumerateArray().Select(t => t.GetString()!).ToArray();
            for (int tag = 0; tag < tags.Length; tag++)
            {
                var frames = new Frame[2];
                for (int key = 0; key < 2; key++)
                {
                    int cell = tag * 2 + key, column = cell % columns, row = cell / columns;
                    int left = xBounds[column], top = yBounds[row];
                    var crop = new CroppedBitmap(image, new Int32Rect(left, top,
                        xBounds[column + 1] - left, yBounds[row + 1] - top));
                    frames[key] = NormalizeSpeechFrame(crop);
                }
                string name = "speech-" + tags[tag];
                clips[name] = new Clip(name, frames, 2, 203, 203);
            }
        }
    }

    private static Frame NormalizeSpeechFrame(BitmapSource image)
    {
        var frame = CreateFrame(image);
        int left = frame.Width, top = frame.Height, right = 0, bottom = 0;
        for (int y = 0; y < frame.Height; y++)
            for (int x = 0; x < frame.Width; x++)
                if (frame.Alpha[y * frame.Width + x] > 16)
                {
                    left = Math.Min(left, x); top = Math.Min(top, y);
                    right = Math.Max(right, x); bottom = Math.Max(bottom, y);
                }
        if (right <= left || bottom <= top) return frame;
        var crop = new CroppedBitmap(image, new Int32Rect(left, top, right - left + 1, bottom - top + 1));
        double scale = Math.Min(680.0 / crop.PixelWidth, 780.0 / crop.PixelHeight);
        double width = crop.PixelWidth * scale, height = crop.PixelHeight * scale;
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
            drawing.DrawImage(crop, new Rect((768 - width) / 2, 812 - height, width, height));
        var normalized = new RenderTargetBitmap(768, 832, 96, 96, PixelFormats.Pbgra32);
        normalized.Render(visual);
        normalized.Freeze();
        return CreateFrame(normalized);
    }
    internal static Frame CreateFrame(BitmapSource source)
    {
        var bgra=new FormatConvertedBitmap(source,PixelFormats.Bgra32,null,0);
        // Hit testing needs only alpha. Avoid retaining a second full-color copy of every HD frame.
        var alpha=new byte[source.PixelWidth*source.PixelHeight];
        var row=new byte[source.PixelWidth*4];
        for(int y=0;y<source.PixelHeight;y++)
        {
            bgra.CopyPixels(new Int32Rect(0,y,source.PixelWidth,1),row,row.Length,0);
            for(int x=0;x<source.PixelWidth;x++)alpha[y*source.PixelWidth+x]=row[x*4+3];
        }
        return new Frame(source,alpha,source.PixelWidth,source.PixelHeight);
    }
}
