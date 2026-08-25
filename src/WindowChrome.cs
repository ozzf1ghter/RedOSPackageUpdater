using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RedOSPackageUpdater
{
    internal sealed class ModernTitleBar : Control
    {
        private const int ButtonWidth = 46;
        private int _hot = -1, _pressed = -1;
        private bool _active = true;
        public Form OwnerForm { get; set; }

        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern IntPtr GetSystemMenu(IntPtr hWnd, bool revert);
        [DllImport("user32.dll")] private static extern int TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr window, IntPtr rect);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        public ModernTitleBar()
        {
            Height = 38; Cursor = Cursors.Default;
            SetStyle(ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.UserPaint|ControlStyles.ResizeRedraw,true);
        }

        public void SetWindowActive(bool active) { _active = active; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); AppIcons.Prepare(e.Graphics);
            Color bg = Theme.IsDark ? Theme.NavigationBg : Theme.Surface;
            Color fg = _active ? Theme.Text : Theme.Muted;
            e.Graphics.Clear(bg);
            using (var line = new Pen(Theme.Border)) e.Graphics.DrawLine(line,0,Height-1,Width,Height-1);
            // Рисуем готовый оптический кадр 20x20 пиксель-в-пиксель: масштабирование 24->22
            // размывало тонкие белые элементы знака на реальном DPI.
            AppIcons.DrawMark(e.Graphics,new Rectangle(13,9,20,20),Theme.Accent,Color.White);
            TextRenderer.DrawText(e.Graphics,"RED OS Package Updater",Theme.UiFontBold,new Rectangle(43,2,Math.Max(0,Width-190),Height-3),fg,
                TextFormatFlags.Left|TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis|TextFormatFlags.NoPrefix|TextFormatFlags.SingleLine);
            for (int i=0;i<3;i++) DrawWindowButton(e.Graphics,i,ButtonRect(i),fg,bg);
        }

        private Rectangle ButtonRect(int index) { return new Rectangle(Width-(3-index)*ButtonWidth,2,ButtonWidth,Height-3); }
        private int ButtonAt(Point p) { for(int i=0;i<3;i++) if(ButtonRect(i).Contains(p)) return i; return -1; }

        private void DrawWindowButton(Graphics g, int index, Rectangle r, Color fg, Color bg)
        {
            if (_hot == index)
            {
                Color hover = index == 2 ? (Theme.IsDark ? Color.FromArgb(65,43,50) : Theme.DangerTint) : Theme.HeaderBg;
                if (_pressed == index) hover = index == 2 ? Color.FromArgb(70,Theme.Danger) : Theme.Sel;
                using(var b=new SolidBrush(hover)) g.FillRectangle(b,r);
                if(index==2) fg=Theme.Danger;
            }
            float cx=r.Left+r.Width/2F, cy=r.Top+r.Height/2F;
            using(var p=new Pen(fg,1.35F)){p.StartCap=LineCap.Round;p.EndCap=LineCap.Round;
                if(index==0) g.DrawLine(p,cx-6,cy+3,cx+6,cy+3);
                else if(index==1)
                {
                    if(OwnerForm!=null && OwnerForm.WindowState==FormWindowState.Maximized){g.DrawRectangle(p,cx-4,cy-5,9,9);g.DrawRectangle(p,cx-6,cy-3,9,9);}
                    else g.DrawRectangle(p,cx-5,cy-5,10,10);
                }
                else {g.DrawLine(p,cx-5,cy-5,cx+5,cy+5);g.DrawLine(p,cx+5,cy-5,cx-5,cy+5);}
            }
        }

        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); int next=ButtonAt(e.Location); if(next!=_hot){_hot=next;Invalidate();} }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hot=-1;_pressed=-1;Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e); if(e.Button==MouseButtons.Right && ButtonAt(e.Location)<0){ShowSystemMenu(e.Location);return;}
            _pressed=ButtonAt(e.Location); Invalidate();
            if(e.Button==MouseButtons.Left && _pressed<0){ReleaseCapture();SendMessage(OwnerForm.Handle,0xA1,new IntPtr(2),IntPtr.Zero);}
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            int hit=ButtonAt(e.Location), action=_pressed; _pressed=-1;Invalidate(); base.OnMouseUp(e);
            if(e.Button!=MouseButtons.Left || hit!=action || OwnerForm==null) return;
            if(action==0) OwnerForm.WindowState=FormWindowState.Minimized;
            else if(action==1) ToggleMaximize();
            else if(action==2) OwnerForm.Close();
        }
        protected override void OnMouseDoubleClick(MouseEventArgs e) { if(e.Button==MouseButtons.Left && ButtonAt(e.Location)<0) ToggleMaximize(); else base.OnMouseDoubleClick(e); }
        private void ToggleMaximize(){if(OwnerForm==null)return;OwnerForm.WindowState=OwnerForm.WindowState==FormWindowState.Maximized?FormWindowState.Normal:FormWindowState.Maximized;Invalidate();}
        private void ShowSystemMenu(Point local)
        {
            if(OwnerForm==null)return; Point screen=PointToScreen(local); IntPtr menu=GetSystemMenu(OwnerForm.Handle,false);
            int command=TrackPopupMenu(menu,0x100|0x002,screen.X,screen.Y,0,OwnerForm.Handle,IntPtr.Zero);
            if(command!=0) PostMessage(OwnerForm.Handle,0x112,(IntPtr)command,IntPtr.Zero);
        }
    }

    public partial class MainForm
    {
        private ModernTitleBar _titleBar;
        private const int WmNcHitTest=0x84, WmActivate=0x0006;
        private const int HtLeft=10,HtRight=11,HtTop=12,HtTopLeft=13,HtTopRight=14,HtBottom=15,HtBottomLeft=16,HtBottomRight=17;

        private void EnableModernWindowChrome()
        {
            FormBorderStyle=FormBorderStyle.None; Padding=new Padding(1,39,1,1);
            _titleBar=new ModernTitleBar{OwnerForm=this,Left=1,Top=1,Width=Math.Max(1,ClientSize.Width-2),Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right};
            Controls.Add(_titleBar); _titleBar.BringToFront();
            Action updateBounds=delegate { if(WindowState==FormWindowState.Normal) MaximizedBounds=Screen.FromControl(this).WorkingArea; };
            LocationChanged+=delegate { updateBounds(); }; Shown+=delegate { updateBounds(); };
        }

        protected override void WndProc(ref Message m)
        {
            if(m.Msg==WmNcHitTest && WindowState==FormWindowState.Normal)
            {
                Point p=PointToClient(new Point((short)((long)m.LParam&0xffff),(short)(((long)m.LParam>>16)&0xffff)));
                int grip=7; bool left=p.X<grip,right=p.X>=ClientSize.Width-grip,top=p.Y<grip,bottom=p.Y>=ClientSize.Height-grip;
                if(left&&top){m.Result=(IntPtr)HtTopLeft;return;} if(right&&top){m.Result=(IntPtr)HtTopRight;return;} if(left&&bottom){m.Result=(IntPtr)HtBottomLeft;return;} if(right&&bottom){m.Result=(IntPtr)HtBottomRight;return;}
                if(left){m.Result=(IntPtr)HtLeft;return;} if(right){m.Result=(IntPtr)HtRight;return;} if(top){m.Result=(IntPtr)HtTop;return;} if(bottom){m.Result=(IntPtr)HtBottom;return;}
            }
            base.WndProc(ref m);
            if(m.Msg==WmActivate && _titleBar!=null) _titleBar.SetWindowActive(((int)m.WParam&0xffff)!=0);
        }
    }
}
