using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// Une « vitre » posée sur l'écran : on la traverse au clic, elle reste devant tout,
// elle ne prend jamais le clavier et (par défaut) elle n'apparaît pas sur les captures d'écran.
namespace CurseurClaude
{
    sealed class Calque : NativeWindow
    {
        IntPtr dc = IntPtr.Zero, dib = IntPtr.Zero, ancien = IntPtr.Zero, bits = IntPtr.Zero;
        int dibL, dibH;
        byte opacite = 255;
        readonly Action surCache;

        public int X { get; private set; }
        public int Y { get; private set; }
        public int L { get; private set; }
        public int H { get; private set; }
        public bool Affiche { get; private set; }
        public string Nom { get; private set; }
        public byte Opacite { get { return opacite; } }

        /// surCache : appelé quand un autre programme réduit ou cache la vitre (l'outil de Claude
        /// réduit les fenêtres des autres programmes avant ses captures).
        public Calque(string nom, bool visibleSurCaptures, Action surCache)
        {
            Nom = nom;
            this.surCache = surCache;
            CreateParams cp = new CreateParams();
            cp.Caption = "Curseur Claude - " + nom;
            cp.Style = unchecked((int)0x80000000); // WS_POPUP
            // LAYERED | TRANSPARENT | TOPMOST | TOOLWINDOW | NOACTIVATE
            cp.ExStyle = 0x00080000 | 0x00000020 | 0x00000008 | 0x00000080 | 0x08000000;
            cp.X = -32000; cp.Y = -32000; cp.Width = 1; cp.Height = 1;
            CreateHandle(cp);
            Captures(visibleSurCaptures);
        }

        public void Captures(bool visibles)
        {
            Natif.SetWindowDisplayAffinity(Handle, visibles ? 0u : 0x11u); // WDA_NONE / WDA_EXCLUDEFROMCAPTURE
        }

        /// Affiche une image (n'importe quel format, convertie en alpha prémultiplié) en (x, y).
        public void Montrer(Bitmap img, int x, int y, byte alpha)
        {
            int w = img.Width, h = img.Height;
            PreparerDib(w, h);
            BitmapData d = img.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                int[] ligne = new int[w];
                for (int yy = 0; yy < h; yy++)
                {
                    Marshal.Copy(new IntPtr(d.Scan0.ToInt64() + (long)yy * d.Stride), ligne, 0, w);
                    Marshal.Copy(ligne, 0, new IntPtr(bits.ToInt64() + (long)yy * w * 4), w);
                }
            }
            finally { img.UnlockBits(d); }
            X = x; Y = y; L = w; H = h; opacite = alpha;
            Appliquer();
        }

        public void Deplacer(int x, int y)
        {
            X = x; Y = y;
            if (!Affiche) return;
            Natif.SetWindowPos(Handle, Natif.HWND_TOPMOST, x, y, 0, 0, 0x0001 | 0x0010); // NOSIZE | NOACTIVATE
        }

        public void ChangerOpacite(byte a)
        {
            if (a == opacite) return;
            opacite = a;
            if (Affiche) Appliquer();
        }

        public void Cacher()
        {
            if (!Affiche) return;
            Affiche = false;
            Natif.ShowWindow(Handle, 0); // SW_HIDE
        }

        /// Remet la vitre à sa place si quelqu'un l'a réduite ou cachée.
        public bool Verifier()
        {
            if (!Affiche || dib == IntPtr.Zero) return false;
            if (Natif.IsIconic(Handle) || !Natif.IsWindowVisible(Handle))
            {
                Appliquer();
                return true;
            }
            Natif.SetWindowPos(Handle, Natif.HWND_TOPMOST, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
            return false;
        }

        void Appliquer()
        {
            if (Natif.IsIconic(Handle)) Natif.ShowWindow(Handle, 4); // SW_SHOWNOACTIVATE : sort de la réduction
            POINT pos = new POINT(X, Y);
            SIZE taille = new SIZE(L, H);
            POINT zero = new POINT(0, 0);
            BLENDFUNCTION bf = new BLENDFUNCTION();
            bf.BlendOp = 0; bf.BlendFlags = 0; bf.SourceConstantAlpha = opacite; bf.AlphaFormat = 1; // AC_SRC_ALPHA
            IntPtr ecran = Natif.GetDC(IntPtr.Zero);
            Natif.UpdateLayeredWindow(Handle, ecran, ref pos, ref taille, dc, ref zero, 0, ref bf, 2); // ULW_ALPHA
            Natif.ReleaseDC(IntPtr.Zero, ecran);
            if (!Affiche || !Natif.IsWindowVisible(Handle))
            {
                Affiche = true;
                Natif.ShowWindow(Handle, 4); // SW_SHOWNOACTIVATE
            }
            Natif.SetWindowPos(Handle, Natif.HWND_TOPMOST, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
        }

        void PreparerDib(int w, int h)
        {
            if (dib != IntPtr.Zero && w == dibL && h == dibH) return;
            LibererDib();
            BITMAPINFOHEADER bi = Rendu.EnTete(w, h);
            IntPtr ecran = Natif.GetDC(IntPtr.Zero);
            dc = Natif.CreateCompatibleDC(ecran);
            Natif.ReleaseDC(IntPtr.Zero, ecran);
            dib = Natif.CreateDIBSection(dc, ref bi, 0, out bits, IntPtr.Zero, 0);
            ancien = Natif.SelectObject(dc, dib);
            dibL = w; dibH = h;
        }

        void LibererDib()
        {
            if (dc != IntPtr.Zero)
            {
                Natif.SelectObject(dc, ancien);
                Natif.DeleteDC(dc);
                dc = IntPtr.Zero;
            }
            if (dib != IntPtr.Zero) { Natif.DeleteObject(dib); dib = IntPtr.Zero; }
        }

        public void Detruire()
        {
            Cacher();
            LibererDib();
            if (Handle != IntPtr.Zero) DestroyHandle();
        }

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case 0x0084: m.Result = new IntPtr(-1); return; // WM_NCHITTEST -> HTTRANSPARENT
                case 0x0021: m.Result = new IntPtr(3); return;  // WM_MOUSEACTIVATE -> MA_NOACTIVATE
                case 0x0005: // WM_SIZE
                    if (m.WParam.ToInt64() == 1 && Affiche && surCache != null) surCache(); // SIZE_MINIMIZED
                    break;
                case 0x0018: // WM_SHOWWINDOW
                    if (m.WParam == IntPtr.Zero && Affiche && surCache != null) surCache();
                    break;
            }
            base.WndProc(ref m);
        }
    }
}
