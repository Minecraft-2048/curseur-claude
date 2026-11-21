using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;

// Tout le dessin : lecture des curseurs Windows, versions orange, fantôme, étiquette, ondes, icône.
namespace CurseurClaude
{
    /// Un curseur lu pixel par pixel : couleurs ARGB (alpha non prémultiplié), point chaud,
    /// et pixels « inversants » (la barre de texte et la croix de Windows inversent le fond).
    sealed class ImageCurseur
    {
        public int L, H, Hx, Hy;
        public uint[] Px;
        public bool[] Inv;

        public bool MemesPixels(ImageCurseur o)
        {
            if (o == null || o.L != L || o.H != H || o.Hx != Hx || o.Hy != Hy) return false;
            for (int i = 0; i < Px.Length; i++)
                if (Px[i] != o.Px[i] || Inv[i] != o.Inv[i]) return false;
            return true;
        }
    }

    static class Rendu
    {
        // ---------- Lecture d'un curseur ----------

        /// Lit un curseur en le dessinant sur fond noir puis sur fond blanc : la différence donne
        /// la transparence, et un pixel qui « s'inverse » (blanc sur noir, noir sur blanc) est repéré.
        public static ImageCurseur Decoder(IntPtr hCurseur)
        {
            if (hCurseur == IntPtr.Zero) return null;
            ICONINFO ii;
            if (!Natif.GetIconInfo(hCurseur, out ii)) return null;
            int w = 0, h = 0;
            try
            {
                BITMAP bm;
                if (ii.hbmColor != IntPtr.Zero && Natif.GetObject(ii.hbmColor, Marshal.SizeOf(typeof(BITMAP)), out bm) != 0)
                { w = bm.bmWidth; h = bm.bmHeight; }
                else if (ii.hbmMask != IntPtr.Zero && Natif.GetObject(ii.hbmMask, Marshal.SizeOf(typeof(BITMAP)), out bm) != 0)
                { w = bm.bmWidth; h = bm.bmHeight / 2; }
            }
            finally
            {
                if (ii.hbmColor != IntPtr.Zero) Natif.DeleteObject(ii.hbmColor);
                if (ii.hbmMask != IntPtr.Zero) Natif.DeleteObject(ii.hbmMask);
            }
            if (w <= 0 || h <= 0 || w > 512 || h > 512) return null;
            int[] noir = Peindre(hCurseur, w, h, unchecked((int)0xFF000000));
            int[] blanc = Peindre(hCurseur, w, h, -1);
            if (noir == null || blanc == null) return null;

            ImageCurseur img = new ImageCurseur();
            img.L = w; img.H = h; img.Hx = ii.xHotspot; img.Hy = ii.yHotspot;
            img.Px = new uint[w * h];
            img.Inv = new bool[w * h];
            for (int i = 0; i < w * h; i++)
            {
                int n = noir[i], b = blanc[i];
                int rn = (n >> 16) & 255, gn = (n >> 8) & 255, bn = n & 255;
                int rb = (b >> 16) & 255, gb = (b >> 8) & 255, bb = b & 255;
                int d = ((rb - rn) + (gb - gn) + (bb - bn)) / 3;
                if (d < -96) { img.Inv[i] = true; img.Px[i] = 0xFFFFFFFF; continue; }
                int a = 255 - Math.Max(0, Math.Min(255, d));
                if (a < 4) continue;
                int r = Math.Min(255, rn * 255 / a), g = Math.Min(255, gn * 255 / a), bl = Math.Min(255, bn * 255 / a);
                img.Px[i] = ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | (uint)bl;
            }
            return img;
        }

        static int[] Peindre(IntPtr hCurseur, int w, int h, int fond)
        {
            BITMAPINFOHEADER bi = EnTete(w, h);
            IntPtr bits;
            IntPtr dc = Natif.CreateCompatibleDC(IntPtr.Zero);
            IntPtr bmp = Natif.CreateDIBSection(dc, ref bi, 0, out bits, IntPtr.Zero, 0);
            if (bmp == IntPtr.Zero) { Natif.DeleteDC(dc); return null; }
            IntPtr ancien = Natif.SelectObject(dc, bmp);
            int[] px = new int[w * h];
            for (int i = 0; i < px.Length; i++) px[i] = fond;
            Marshal.Copy(px, 0, bits, px.Length);
            Natif.DrawIconEx(dc, 0, 0, hCurseur, w, h, 0, IntPtr.Zero, 3); // DI_NORMAL
            Natif.GdiFlush();
            Marshal.Copy(bits, px, 0, px.Length);
            Natif.SelectObject(dc, ancien);
            Natif.DeleteObject(bmp);
            Natif.DeleteDC(dc);
            return px;
        }

        public static BITMAPINFOHEADER EnTete(int w, int h)
        {
            BITMAPINFOHEADER bi = new BITMAPINFOHEADER();
            bi.biSize = Marshal.SizeOf(typeof(BITMAPINFOHEADER));
            bi.biWidth = w;
            bi.biHeight = -h; // de haut en bas
            bi.biPlanes = 1;
            bi.biBitCount = 32;
            return bi;
        }

        // ---------- Recoloriage ----------

        /// Version orange d'un curseur : le blanc devient orange, le noir devient le contour foncé,
        /// les pixels inversants deviennent orange avec un liseré foncé pour rester visibles partout.
        public static uint[] Orange(ImageCurseur src, int couleur, int contour)
        {
            int w = src.L, h = src.H;
            uint[] px = new uint[w * h];
            int cr = (couleur >> 16) & 255, cg = (couleur >> 8) & 255, cb = couleur & 255;
            int kr = (contour >> 16) & 255, kg = (contour >> 8) & 255, kb = contour & 255;
            for (int i = 0; i < px.Length; i++)
            {
                if (src.Inv[i]) { px[i] = 0xFF000000u | (uint)(couleur & 0xFFFFFF); continue; }
                uint p = src.Px[i];
                int a = (int)(p >> 24);
                if (a == 0) continue;
                int r = (int)(p >> 16) & 255, g = (int)(p >> 8) & 255, b = (int)p & 255;
                double lum = (0.299 * r + 0.587 * g + 0.114 * b) / 255.0;
                double t = Math.Max(0.0, Math.Min(1.0, (lum - 0.18) / 0.64));
                int sat = Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b));
                if (sat > 70) t = Math.Max(t, 0.62); // parties colorées (cercle bleu « occupé », panneau rouge) : orange un peu plus sombre
                int nr = (int)(kr + (cr - kr) * t), ng = (int)(kg + (cg - kg) * t), nb = (int)(kb + (cb - kb) * t);
                px[i] = ((uint)a << 24) | ((uint)nr << 16) | ((uint)ng << 8) | (uint)nb;
            }
            return Lisere(px, src, 0xFF000000u | (uint)(contour & 0xFFFFFF));
        }

        /// Copie fidèle du curseur de l'utilisateur (les pixels inversants deviennent noirs bordés de blanc).
        public static uint[] Fantome(ImageCurseur src)
        {
            uint[] px = (uint[])src.Px.Clone();
            for (int i = 0; i < px.Length; i++) if (src.Inv[i]) px[i] = 0xFF000000u;
            return Lisere(px, src, 0xFFFFFFFFu);
        }

        static uint[] Lisere(uint[] px, ImageCurseur src, uint couleurLisere)
        {
            int w = src.L, h = src.H;
            uint[] res = (uint[])px.Clone();
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    if (!src.Inv[y * w + x]) continue;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int xx = x + dx, yy = y + dy;
                            if (xx < 0 || yy < 0 || xx >= w || yy >= h) continue;
                            int j = yy * w + xx;
                            if (src.Inv[j] || (px[j] >> 24) != 0) continue;
                            res[j] = couleurLisere;
                        }
                }
            return res;
        }

        /// Fabrique un vrai curseur Windows à partir de pixels ARGB.
        public static IntPtr CreerCurseur(uint[] px, int w, int h, int hx, int hy)
        {
            BITMAPINFOHEADER bi = EnTete(w, h);
            IntPtr bits;
            IntPtr ecran = Natif.GetDC(IntPtr.Zero);
            IntPtr couleur = Natif.CreateDIBSection(ecran, ref bi, 0, out bits, IntPtr.Zero, 0);
            Natif.ReleaseDC(IntPtr.Zero, ecran);
            if (couleur == IntPtr.Zero) return IntPtr.Zero;
            int[] tmp = new int[px.Length];
            for (int i = 0; i < px.Length; i++) tmp[i] = (px[i] >> 24) == 0 ? 0 : unchecked((int)px[i]);
            Marshal.Copy(tmp, 0, bits, tmp.Length);

            int pas = ((w + 15) / 16) * 2; // lignes du masque alignées sur 16 bits
            byte[] masque = new byte[pas * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if ((px[y * w + x] >> 24) == 0) masque[y * pas + x / 8] |= (byte)(0x80 >> (x % 8));
            IntPtr hMasque = Natif.CreateBitmap(w, h, 1, 1, masque);

            ICONINFO ii = new ICONINFO();
            ii.fIcon = false; ii.xHotspot = hx; ii.yHotspot = hy; ii.hbmMask = hMasque; ii.hbmColor = couleur;
            IntPtr cur = Natif.CreateIconIndirect(ref ii);
            Natif.DeleteObject(couleur);
            Natif.DeleteObject(hMasque);
            return cur;
        }

        public static Bitmap VersBitmap(uint[] px, int w, int h)
        {
            Bitmap b = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            BitmapData d = b.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                int[] ligne = new int[w];
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++) ligne[x] = unchecked((int)px[y * w + x]);
                    Marshal.Copy(ligne, 0, new IntPtr(d.Scan0.ToInt64() + (long)y * d.Stride), w);
                }
            }
            finally { b.UnlockBits(d); }
            return b;
        }

        public static Color Couleur(int rgb) { return Color.FromArgb(255, (rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255); }

        // ---------- Étiquette « Claude » ----------

        static Font Police(float e) { return new Font("Segoe UI Semibold", 12.5f * e, FontStyle.Regular, GraphicsUnit.Pixel); }

        public static Size TailleEtiquette(string texte, float e)
        {
            using (Bitmap b = new Bitmap(1, 1))
            using (Graphics g = Graphics.FromImage(b))
            using (Font f = Police(e))
            {
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                SizeF t = g.MeasureString(texte, f, PointF.Empty, StringFormat.GenericTypographic);
                float largeur = 7f * e + 11f * e + 5f * e + t.Width + 9f * e;
                return new Size((int)Math.Ceiling(largeur), (int)Math.Ceiling(21f * e));
            }
        }

        /// Pilule orange avec une petite étincelle blanche et le nom, comme les curseurs des
        /// collègues dans un document partagé. (x, y) = coin haut-gauche.
        public static void DessinerEtiquette(Graphics g, float x, float y, string texte, float e, Color fond)
        {
            Size s = TailleEtiquette(texte, e);
            float r = s.Height / 2f;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using (GraphicsPath ombre = Pilule(x + 0.5f * e, y + 1.5f * e, s.Width, s.Height, r))
            using (SolidBrush b = new SolidBrush(Color.FromArgb(60, 0, 0, 0)))
                g.FillPath(b, ombre);
            using (GraphicsPath p = Pilule(x, y, s.Width, s.Height, r))
            using (SolidBrush b = new SolidBrush(fond))
                g.FillPath(b, p);
            Etincelle(g, x + 7f * e + 5.5f * e, y + s.Height / 2f, 5.4f * e, Color.White, e);
            using (Font f = Police(e))
            using (SolidBrush blanc = new SolidBrush(Color.White))
            {
                SizeF t = g.MeasureString(texte, f, PointF.Empty, StringFormat.GenericTypographic);
                g.DrawString(texte, f, blanc, x + 7f * e + 11f * e + 5f * e, y + (s.Height - t.Height) / 2f - 0.5f * e, StringFormat.GenericTypographic);
            }
        }

        static GraphicsPath Pilule(float x, float y, float w, float h, float r)
        {
            GraphicsPath p = new GraphicsPath();
            float d = r * 2;
            p.AddArc(x, y, d, d, 90, 180);
            p.AddArc(x + w - d, y, d, d, 270, 180);
            p.CloseFigure();
            return p;
        }

        /// Petite étincelle à huit branches (une longue, une courte).
        public static void Etincelle(Graphics g, float cx, float cy, float rayon, Color c, float e)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (Pen p = new Pen(c, Math.Max(1f, 1.7f * e)))
            {
                p.StartCap = LineCap.Round;
                p.EndCap = LineCap.Round;
                for (int k = 0; k < 8; k++)
                {
                    double ang = Math.PI * 2 * k / 8 + 0.12;
                    float lon = (k % 2 == 0) ? rayon : rayon * 0.68f;
                    g.DrawLine(p, cx + (float)Math.Cos(ang) * 1.1f * e, cy + (float)Math.Sin(ang) * 1.1f * e,
                                  cx + (float)Math.Cos(ang) * lon, cy + (float)Math.Sin(ang) * lon);
                }
            }
        }

        // ---------- Ondes de clic et de molette ----------

        public static int TailleOnde(float e) { return 2 * (int)Math.Ceiling(30 * e) + 8; }

        /// genre : 0 clic gauche, 1 clic droit, 2 clic du milieu, 3 molette vers le haut, 4 vers le bas.
        /// t va de 0 (début) à 1 (fin de l'animation).
        public static Bitmap Onde(float t, float e, Color c, int genre)
        {
            int taille = TailleOnde(e);
            float R = (float)Math.Ceiling(30 * e);
            Bitmap b = new Bitmap(taille, taille, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                float cx = taille / 2f, cy = taille / 2f;
                float ease = 1f - (float)Math.Pow(1 - t, 3);
                int alpha = Math.Max(0, Math.Min(255, (int)(255 * Math.Pow(1 - t, 1.4))));
                if (genre <= 2)
                {
                    float r = 5 * e + (R - 5 * e) * ease;
                    using (Pen p = new Pen(Color.FromArgb(alpha, c), 3f * e)) g.DrawEllipse(p, cx - r, cy - r, 2 * r, 2 * r);
                    if (genre == 1)
                    {
                        float r2 = r * 0.6f;
                        using (Pen p = new Pen(Color.FromArgb(alpha, c), 2f * e)) g.DrawEllipse(p, cx - r2, cy - r2, 2 * r2, 2 * r2);
                    }
                    if (t < 0.5f)
                    {
                        int a2 = (int)(230 * (1 - t * 2));
                        float rp = (genre == 2 ? 3f : 4.5f) * e;
                        using (SolidBrush br = new SolidBrush(Color.FromArgb(a2, c))) g.FillEllipse(br, cx - rp, cy - rp, 2 * rp, 2 * rp);
                    }
                }
                else
                {
                    float sens = genre == 3 ? -1f : 1f;
                    float dec = sens * 10f * e * ease;
                    using (Pen p = new Pen(Color.FromArgb(alpha, c), 3f * e))
                    {
                        p.StartCap = LineCap.Round;
                        p.EndCap = LineCap.Round;
                        p.LineJoin = LineJoin.Round;
                        for (int k = 0; k < 2; k++)
                        {
                            float yy = cy + dec + sens * (k * 7f - 4f) * e;
                            g.DrawLines(p, new PointF[] {
                                new PointF(cx - 8 * e, yy - sens * 5 * e),
                                new PointF(cx, yy),
                                new PointF(cx + 8 * e, yy - sens * 5 * e) });
                        }
                    }
                }
            }
            return b;
        }

        // ---------- Icône ----------

        /// Flèche orange dessinée en vectoriel (icône de la zone de notification et du programme).
        public static Bitmap Icone(int taille, int couleur, int contour, bool eteinte)
        {
            Bitmap b = new Bitmap(taille, taille, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                float u = taille / 17f;
                PointF[] pts = {
                    new PointF(2.6f, 0.9f), new PointF(2.6f, 14.6f), new PointF(5.9f, 11.5f),
                    new PointF(8.3f, 16.2f), new PointF(10.7f, 15.1f), new PointF(8.4f, 10.5f),
                    new PointF(13.1f, 10.5f) };
                for (int i = 0; i < pts.Length; i++) pts[i] = new PointF(pts[i].X * u, pts[i].Y * u);
                Color fond = eteinte ? Color.FromArgb(255, 150, 150, 150) : Couleur(couleur);
                using (GraphicsPath p = new GraphicsPath())
                {
                    p.AddPolygon(pts);
                    using (SolidBrush br = new SolidBrush(fond)) g.FillPath(br, p);
                    using (Pen pen = new Pen(Couleur(contour), Math.Max(1f, 1.1f * u)))
                    {
                        pen.LineJoin = LineJoin.Round;
                        g.DrawPath(pen, p);
                    }
                }
                if (taille >= 32) Etincelle(g, 13.2f * u, 3.9f * u, 3.3f * u, fond, u * 0.6f);
            }
            return b;
        }

        /// Écrit un fichier .ico (petites tailles en BMP, 256 en PNG).
        public static void EcrireIco(string chemin, int couleur, int contour)
        {
            int[] tailles = { 16, 20, 24, 32, 40, 48, 64, 256 };
            List<byte[]> images = new List<byte[]>();
            foreach (int t in tailles)
                using (Bitmap b = Icone(t, couleur, contour, false))
                    images.Add(t >= 256 ? Png(b) : DibIco(b));
            using (FileStream fs = File.Create(chemin))
            using (BinaryWriter w = new BinaryWriter(fs))
            {
                w.Write((short)0); w.Write((short)1); w.Write((short)tailles.Length);
                int pos = 6 + 16 * tailles.Length;
                for (int i = 0; i < tailles.Length; i++)
                {
                    int t = tailles[i];
                    w.Write((byte)(t >= 256 ? 0 : t)); w.Write((byte)(t >= 256 ? 0 : t));
                    w.Write((byte)0); w.Write((byte)0);
                    w.Write((short)1); w.Write((short)32);
                    w.Write(images[i].Length); w.Write(pos);
                    pos += images[i].Length;
                }
                foreach (byte[] im in images) w.Write(im);
            }
        }

        static byte[] Png(Bitmap b)
        {
            using (MemoryStream ms = new MemoryStream()) { b.Save(ms, ImageFormat.Png); return ms.ToArray(); }
        }

        static byte[] DibIco(Bitmap b)
        {
            int w = b.Width, h = b.Height;
            int pasMasque = ((w + 31) / 32) * 4;
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter wr = new BinaryWriter(ms))
            {
                wr.Write(40); wr.Write(w); wr.Write(h * 2); wr.Write((short)1); wr.Write((short)32);
                wr.Write(0); wr.Write(w * h * 4); wr.Write(0); wr.Write(0); wr.Write(0); wr.Write(0);
                for (int y = h - 1; y >= 0; y--)
                    for (int x = 0; x < w; x++)
                    {
                        Color c = b.GetPixel(x, y);
                        wr.Write(c.B); wr.Write(c.G); wr.Write(c.R); wr.Write(c.A);
                    }
                for (int y = h - 1; y >= 0; y--)
                {
                    byte[] ligne = new byte[pasMasque];
                    for (int x = 0; x < w; x++) if (b.GetPixel(x, y).A == 0) ligne[x / 8] |= (byte)(0x80 >> (x % 8));
                    wr.Write(ligne);
                }
                return ms.ToArray();
            }
        }
    }
}
