using System.Collections.Generic;
using System.IO;
using Avalonia.Media.Imaging;
using PlanViewer.Core.Services;

namespace PlanViewer.App.Helpers;

public static class IconHelper
{
    private static readonly Dictionary<string, Bitmap?> Cache = new();

    public static Bitmap? LoadIcon(string iconName)
    {
        if (Cache.TryGetValue(iconName, out var cached))
            return cached;

        var asm = typeof(PlanIconMapper).Assembly;
        var stream = asm.GetManifestResourceStream(
            $"PlanViewer.Core.Resources.PlanIcons.{iconName}.png");

        Bitmap? bitmap = null;
        if (stream != null)
        {
            bitmap = new Bitmap(stream);
        }
        else
        {
            // Try fallback
            var fallback = asm.GetManifestResourceStream(
                "PlanViewer.Core.Resources.PlanIcons.iterator_catch_all.png");
            if (fallback != null)
                bitmap = new Bitmap(fallback);
        }

        Cache[iconName] = bitmap;
        return bitmap;
    }
}
