using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace RedOSPackageUpdater
{
    internal static class AppIcons
    {
        public static void Prepare(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        }

        public static void Draw(Graphics g, string name, Color color, Rectangle bounds)
        {
            Prepare(g);
            float scale = Math.Min(bounds.Width, bounds.Height) / 20F;
            float x = bounds.X + (bounds.Width - 20F * scale) / 2F;
            float y = bounds.Y + (bounds.Height - 20F * scale) / 2F;
            Func<float, float> X = v => x + v * scale;
            Func<float, float> Y = v => y + v * scale;
            using (var pen = new Pen(color, Math.Max(1.25F, 1.65F * scale)))
            {
                pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round; pen.LineJoin = LineJoin.Round;
                if (name == "servers")
                {
                    g.DrawRoundedRectangle(pen, RectangleF.FromLTRB(X(2), Y(2), X(18), Y(8)), 1.6F * scale);
                    g.DrawRoundedRectangle(pen, RectangleF.FromLTRB(X(2), Y(12), X(18), Y(18)), 1.6F * scale);
                    Dot(g, color, X(5), Y(5), scale); Dot(g, color, X(5), Y(15), scale);
                }
                else if (name == "operations" || name == "settings") DrawGear(g, pen, X, Y);
                else if (name == "fstec")
                {
                    PointF[] p = { new PointF(X(10),Y(1.5F)),new PointF(X(17),Y(4.5F)),new PointF(X(16),Y(13)),new PointF(X(10),Y(18.5F)),new PointF(X(4),Y(13)),new PointF(X(3),Y(4.5F)) };
                    g.DrawPolygon(pen, p); g.DrawLines(pen, new[] { new PointF(X(6.5F),Y(10)), new PointF(X(9),Y(12.5F)), new PointF(X(14),Y(7.5F)) });
                }
                else if (name == "reports")
                {
                    g.DrawRoundedRectangle(pen, RectangleF.FromLTRB(X(4),Y(2),X(16),Y(18)), 1.5F*scale);
                    g.DrawLine(pen,X(7),Y(7),X(13),Y(7)); g.DrawLine(pen,X(7),Y(11),X(13),Y(11)); g.DrawLine(pen,X(7),Y(15),X(11),Y(15));
                }
                else if (name == "access" || name == "key")
                {
                    g.DrawEllipse(pen, X(2),Y(3),8*scale,8*scale); g.DrawLine(pen,X(9),Y(10),X(17.5F),Y(18.5F)); g.DrawLine(pen,X(14),Y(15),X(16),Y(13));
                }
                else if (name == "search") { g.DrawEllipse(pen,X(2),Y(2),12*scale,12*scale); g.DrawLine(pen,X(13),Y(13),X(18),Y(18)); }
                else if (name == "add") { g.DrawLine(pen,X(10),Y(3),X(10),Y(17)); g.DrawLine(pen,X(3),Y(10),X(17),Y(10)); }
                else if (name == "update")
                {
                    g.DrawArc(pen,X(2),Y(3),15*scale,14*scale,210,235); g.DrawLines(pen,new[]{new PointF(X(15),Y(2.5F)),new PointF(X(18),Y(5)),new PointF(X(14.5F),Y(6))});
                }
                else if (name == "play") { g.DrawEllipse(pen,X(2),Y(2),16*scale,16*scale); g.DrawPolygon(pen,new[]{new PointF(X(8),Y(6)),new PointF(X(14),Y(10)),new PointF(X(8),Y(14))}); }
                else if (name == "stop") { g.DrawEllipse(pen,X(2),Y(2),16*scale,16*scale); g.DrawRectangle(pen,X(7),Y(7),6*scale,6*scale); }
                else if (name == "warning") { g.DrawPolygon(pen,new[]{new PointF(X(10),Y(2)),new PointF(X(18),Y(17)),new PointF(X(2),Y(17))}); g.DrawLine(pen,X(10),Y(7),X(10),Y(12)); Dot(g,color,X(10),Y(15),scale); }
                else if (name == "error") { g.DrawEllipse(pen,X(2),Y(2),16*scale,16*scale); g.DrawLine(pen,X(7),Y(7),X(13),Y(13)); g.DrawLine(pen,X(13),Y(7),X(7),Y(13)); }
                else if (name == "success") { g.DrawEllipse(pen,X(2),Y(2),16*scale,16*scale); g.DrawLines(pen,new[]{new PointF(X(6),Y(10)),new PointF(X(9),Y(13)),new PointF(X(14.5F),Y(7))}); }
                else DrawMore(g, color, X, Y, scale);
            }
        }

        public static void DrawMark(Graphics g, Rectangle bounds, Color background, Color foreground)
        {
            Image image = AppIconAsset.ForSize(Math.Max(bounds.Width, bounds.Height));
            if (image != null)
            {
                g.InterpolationMode = bounds.Width <= 24 ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
                g.DrawImage(image, bounds); return;
            }
            using (var path = ModernButton.Rounded(bounds, Math.Max(3, bounds.Width / 5)))
            using (var brush = new SolidBrush(background)) g.FillPath(brush, path);
        }

        private static void DrawMore(Graphics g, Color c, Func<float,float>X, Func<float,float>Y, float s) { Dot(g,c,X(10),Y(4),s); Dot(g,c,X(10),Y(10),s); Dot(g,c,X(10),Y(16),s); }
        private static void Dot(Graphics g, Color c, float x, float y, float s) { using(var b=new SolidBrush(c)) g.FillEllipse(b,x-s,y-s,2*s,2*s); }
        private static void DrawGear(Graphics g, Pen pen, Func<float,float>X, Func<float,float>Y)
        {
            float s = X(1)-X(0);
            g.DrawEllipse(pen,X(3),Y(3),14*s,14*s); g.DrawEllipse(pen,X(7),Y(7),6*s,6*s);
            for(int i=0;i<8;i++){ double a=i*Math.PI/4; float x1=X(10)+(float)Math.Cos(a)*7*s; float y1=Y(10)+(float)Math.Sin(a)*7*s; float x2=X(10)+(float)Math.Cos(a)*9*s; float y2=Y(10)+(float)Math.Sin(a)*9*s; g.DrawLine(pen,x1,y1,x2,y2); }
        }
    }

    internal static class AppIconAsset
    {
        private static readonly object Sync = new object();
        private static readonly System.Collections.Generic.Dictionary<int, Bitmap> Cache = new System.Collections.Generic.Dictionary<int, Bitmap>();
        public static Image ForSize(int requested)
        {
            int size = requested <= 16 ? 16 : requested <= 20 ? 20 : requested <= 24 ? 24 : requested <= 32 ? 32 : requested <= 48 ? 48 : requested <= 64 ? 64 : 256;
            lock (Sync)
            {
                Bitmap bitmap;
                if (Cache.TryGetValue(size, out bitmap)) return bitmap;
                string resource = "app-icon-" + size + ".png";
                using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource))
                {
                    if (stream == null) return null;
                    using (var loaded = new Bitmap(stream)) bitmap = new Bitmap(loaded);
                }
                Cache[size] = bitmap; return bitmap;
            }
        }
    }

    internal static class GraphicsExtensions
    {
        public static void DrawRoundedRectangle(this Graphics graphics, Pen pen, RectangleF bounds, float radius)
        {
            float d = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
            using (var path = new GraphicsPath())
            { path.AddArc(bounds.Left,bounds.Top,d,d,180,90); path.AddArc(bounds.Right-d,bounds.Top,d,d,270,90); path.AddArc(bounds.Right-d,bounds.Bottom-d,d,d,0,90); path.AddArc(bounds.Left,bounds.Bottom-d,d,d,90,90); path.CloseFigure(); graphics.DrawPath(pen,path); }
        }
    }

    internal sealed class AppIconView : Control
    {
        public AppIconView() { SetStyle(ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.UserPaint|ControlStyles.ResizeRedraw,true); }
        protected override void OnPaint(PaintEventArgs e) { base.OnPaint(e); AppIcons.DrawMark(e.Graphics,new Rectangle(0,0,Math.Max(1,Width-1),Math.Max(1,Height-1)),Theme.Accent,Color.White); }
    }
}
