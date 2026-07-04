// Copyright (c) 2026 LanDen Labs - Dennis Lang

using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Faster
{
    /// <summary>
    /// Loads the app's icon/logo (<c>icon.ico</c> / <c>icon.png</c>) for use as every window's
    /// title-bar/taskbar icon and as the image shown in the "About" tab. Reads from the embedded
    /// resource baked into the exe first (works inside a self-contained single-file publish),
    /// falling back to a loose file next to the exe for a debug run from the build folder.
    /// Deliberately smaller than cs-b4browse's <c>EmbeddedAssets.cs</c>: Faster only ever needs
    /// this one icon, so there's no need for that file's more general lookup-by-name API.
    /// </summary>
    internal static class AppIcon
    {
        /// <summary>The window/taskbar icon (icon.ico) - null if it couldn't be loaded from
        /// either the embedded resource or a loose file, in which case callers should just leave
        /// Form.Icon at its default rather than throw.</summary>
        public static Icon? LoadIcon()
        {
            try
            {
                using var stream = OpenResourceOrFile("icon.ico");
                return stream != null ? new Icon(stream) : null;
            }
            catch { return null; }
        }

        /// <summary>The About tab's logo image (icon.png) - null if it couldn't be loaded.
        /// Returns a fresh Image each call (the caller owns disposal); AboutPanel is created once
        /// per MainForm so this only runs once in practice.</summary>
        public static Image? LoadImage()
        {
            try
            {
                using var source = OpenResourceOrFile("icon.png");
                if (source == null) return null;

                // GDI+ can keep referencing the backing stream after Image.FromStream returns
                // (lazy decode), so copy into a MemoryStream and deliberately leave IT open for
                // the Image's lifetime - only the original resource/file stream (already fully
                // copied from) is safe to dispose here.
                var ms = new MemoryStream();
                source.CopyTo(ms);
                ms.Position = 0;
                return Image.FromStream(ms);
            }
            catch { return null; }
        }

        private static Stream? OpenResourceOrFile(string fileName)
        {
            var asm = Assembly.GetExecutingAssembly();
            string? resName = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("." + fileName, StringComparison.OrdinalIgnoreCase));
            if (resName != null)
            {
                var resStream = asm.GetManifestResourceStream(resName);
                if (resStream != null) return resStream;
            }

            string path = Path.Combine(AppContext.BaseDirectory, fileName);
            return File.Exists(path) ? File.OpenRead(path) : null;
        }
    }
}
