using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;
using Newtonsoft.Json.Linq;

namespace WinFormsMcp.Instrumented;

internal static class ScreenCapture
{
    internal static JObject Capture(Control target, JObject? region, bool includeFrame,
        int maxPixels, int maxBytes, out int imageBytes)
    {
        imageBytes = 0;
        Form? form = target.FindForm();
        if (target.IsDisposed || !target.IsHandleCreated || !target.Visible ||
            form == null || form.WindowState == FormWindowState.Minimized)
            throw new BridgeFailure("capture_unavailable", "Visible-screen capture requires a visible, non-minimized window and a created control handle.");
        Rectangle requested = includeFrame && target is Form window
            ? window.Bounds
            : target.RectangleToScreen(target.ClientRectangle);
        if (region != null)
        {
            int x = UiRuntime.ReadInt(region, "x", 0, 0, requested.Width);
            int y = UiRuntime.ReadInt(region, "y", 0, 0, requested.Height);
            int width = UiRuntime.ReadInt(region, "width", null, 1, requested.Width - x);
            int height = UiRuntime.ReadInt(region, "height", null, 1, requested.Height - y);
            requested = new Rectangle(requested.X + x, requested.Y + y, width, height);
        }
        Rectangle actual = requested;
        if (!(target is Form))
        {
            // A child can extend beyond a scrolled viewport. Copying its full screen bounds
            // would otherwise include pixels outside its visible ancestor chain.
            for (Control? ancestor = target.Parent; ancestor != null; ancestor = ancestor.Parent)
            {
                if (ancestor.IsDisposed || !ancestor.IsHandleCreated || !ancestor.Visible)
                    throw new BridgeFailure("capture_unavailable", "A containing control is not available for visible-screen capture.");
                actual = Rectangle.Intersect(actual, ancestor.RectangleToScreen(ancestor.ClientRectangle));
                if (ancestor is Form) break;
            }
        }
        actual = Rectangle.Intersect(actual, SystemInformation.VirtualScreen);
        if (actual.Width <= 0 || actual.Height <= 0)
            throw new BridgeFailure("capture_unavailable", "The requested region is outside its visible container or desktop.");
        if ((long)actual.Width * actual.Height > maxPixels)
            throw new BridgeFailure("capture_limit", "The requested image exceeds the configured pixel limit.");
        if (maxBytes <= 0) throw new BridgeFailure("capture_limit", "The combined PNG size exceeds the configured limit.");
        byte[] bytes;
        using (var bitmap = new Bitmap(actual.Width, actual.Height, PixelFormat.Format32bppArgb))
        {
            using (Graphics graphics = Graphics.FromImage(bitmap))
                graphics.CopyFromScreen(actual.Location, Point.Empty, actual.Size, CopyPixelOperation.SourceCopy);
            using var buffer = new MemoryStream();
            bitmap.Save(buffer, ImageFormat.Png);
            if (buffer.Length > maxBytes)
                throw new BridgeFailure("capture_limit", "The combined PNG size exceeds the configured limit.");
            bytes = buffer.ToArray();
        }
        imageBytes = bytes.Length;
        return new JObject
        {
            ["mimeType"] = "image/png",
            ["data"] = Convert.ToBase64String(bytes),
            ["width"] = actual.Width,
            ["height"] = actual.Height,
            ["bounds"] = UiRuntime.Bounds(actual),
            ["requestedBounds"] = UiRuntime.Bounds(requested),
            ["method"] = "screen_copy",
            ["occlusionPossible"] = true,
            ["includesFrame"] = includeFrame && target is Form,
            ["clipped"] = actual != requested,
            ["timeUtc"] = DateTimeOffset.UtcNow.ToString("O")
        };
    }
}
