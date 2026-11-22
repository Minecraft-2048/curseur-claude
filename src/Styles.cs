using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

// Les apparences du curseur de Claude : orange plein (la première), lueur orange (comme un néon),
// ou une image choisie par l'utilisateur.
namespace CurseurClaude
{
    static class Styles
    {
        public const string Orange = "orange";
        public const string Lueur = "lueur";
        public const string Perso = "perso";

        public static readonly string[] Tous = { Orange, Lueur, Perso };

        public static string Nom(string style)
        {
            switch (style)
            {
                case Orange: return "Orange (l'ancienne)";
                case Perso: return "Mon image";
                default: return "Lueur orange (la nouvelle)";
            }
        }

        public static string Normaliser(string s)
        {
            s = (s ?? "").Trim().ToLowerInvariant();
            if (s == Orange || s == Perso) return s;
            return Lueur;
        }

        /// Version « Claude » d'un curseur de Windows, en gardant sa forme (flèche, main, barre de texte…).
        public static ImageCurseur Appliquer(ImageCurseur src, string style, int couleur, int contour)
        {
            if (style == Orange)
            {
                ImageCurseur r = new ImageCurseur();
                r.L = src.L; r.H = src.H; r.Hx = src.Hx; r.Hy = src.Hy;
                r.Px = Rendu.Orange(src, couleur, contour);
                r.Inv = new bool[r.Px.Length];
                return r;
            }
            return Neon(src, couleur);
        }

        const int MARGE = 8; // place autour du curseur pour le halo

        /// Blanc, bordé d'orange, dans un halo orange (comme un néon).
        static ImageCurseur Neon(ImageCurseur src, int couleur)
        {
            int w = src.L, h = src.H, P = MARGE, W = w + 2 * P, H = h + 2 * P;
            float orR = (couleur >> 16) & 255, orG = (couleur >> 8) & 255, orB = couleur & 255;
            const float BR = 255f, BV = 251f, BB = 247f; // blanc très légèrement chaud
            float[] a = new float[W * H], r = new float[W * H], g = new float[W * H], b = new float[W * H];
            bool[] inv = new bool[W * H];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x, j = (y + P) * W + (x + P);
                    if (src.Inv[i]) { a[j] = 1; r[j] = BR; g[j] = BV; b[j] = BB; inv[j] = true; continue; }
                    uint p = src.Px[i];
                    int al = (int)(p >> 24);
                    if (al == 0) continue;
                    int pr = (int)(p >> 16) & 255, pg = (int)(p >> 8) & 255, pb = (int)p & 255;
                    double lum = (0.299 * pr + 0.587 * pg + 0.114 * pb) / 255.0;
                    float t = (float)Math.Max(0.0, Math.Min(1.0, (lum - 0.18) / 0.64));
                    int sat = Math.Max(pr, Math.Max(pg, pb)) - Math.Min(pr, Math.Min(pg, pb));
                    if (sat > 70) t = Math.Min(t, 0.25f); // parties colorées (cercle « occupé ») : orange
                    a[j] = al / 255f;
                    r[j] = orR + (BR - orR) * t; g[j] = orG + (BV - orG) * t; b[j] = orB + (BB - orB) * t;
                }

            // Liseré orange autour des pixels inversants (barre de texte, croix), qui deviennent blancs.
            float[] a2 = (float[])a.Clone();
            for (int y = 1; y < H - 1; y++)
                for (int x = 1; x < W - 1; x++)
                {
                    if (!inv[y * W + x]) continue;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int k = (y + dy) * W + (x + dx);
                            if (a[k] > 0 || inv[k]) continue;
                            a2[k] = 1; r[k] = orR; g[k] = orG; b[k] = orB;
                        }
                }
            a = a2;

            // Bord orange un peu plus épais : un pixel de plus tout autour de la silhouette.
            float[] a3 = (float[])a.Clone();
            for (int y = 1; y < H - 1; y++)
                for (int x = 1; x < W - 1; x++)
                {
                    int j = y * W + x;
                    if (a[j] > 0.05f) continue;
                    float voisin = 0;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                            voisin = Math.Max(voisin, a[(y + dy) * W + (x + dx)] * (dx != 0 && dy != 0 ? 0.6f : 1f));
                    if (voisin <= 0.3f) continue;
                    a3[j] = Math.Min(1f, voisin) * 0.9f; r[j] = orR; g[j] = orG; b[j] = orB;
                }
            a = a3;

            // Halo : la silhouette floutée, en orange, posée sous le curseur.
            float[] flou = Flou(a, W, H, 2.5f);
            uint[] px = new uint[W * H];
            for (int j = 0; j < px.Length; j++)
            {
                float ga = Math.Min(1f, flou[j] * 1.5f) * 0.8f;
                float ca = a[j];
                float oa = ca + ga * (1 - ca);
                if (oa < 0.004f) continue;
                float rr = (r[j] * ca + orR * ga * (1 - ca)) / oa;
                float gg = (g[j] * ca + orG * ga * (1 - ca)) / oa;
                float bb = (b[j] * ca + orB * ga * (1 - ca)) / oa;
                px[j] = ((uint)Octet(oa * 255) << 24) | ((uint)Octet(rr) << 16) | ((uint)Octet(gg) << 8) | (uint)Octet(bb);
            }
            ImageCurseur res = new ImageCurseur();
            res.L = W; res.H = H; res.Hx = src.Hx + P; res.Hy = src.Hy + P;
            res.Px = px; res.Inv = new bool[W * H];
            return res;
        }

        static int Octet(float v) { return Math.Max(0, Math.Min(255, (int)Math.Round(v))); }

        static float[] Flou(float[] src, int w, int h, float sigma)
        {
            int rayon = (int)Math.Ceiling(sigma * 3);
            float[] noyau = new float[2 * rayon + 1];
            float somme = 0;
            for (int i = -rayon; i <= rayon; i++)
            {
                noyau[i + rayon] = (float)Math.Exp(-(i * i) / (2 * sigma * sigma));
                somme += noyau[i + rayon];
            }
            for (int i = 0; i < noyau.Length; i++) noyau[i] /= somme;
            float[] tmp = new float[w * h], res = new float[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float s = 0;
                    for (int k = -rayon; k <= rayon; k++)
                    {
                        int xx = x + k;
                        if (xx >= 0 && xx < w) s += src[y * w + xx] * noyau[k + rayon];
                    }
                    tmp[y * w + x] = s;
                }
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float s = 0;
                    for (int k = -rayon; k <= rayon; k++)
                    {
                        int yy = y + k;
                        if (yy >= 0 && yy < h) s += tmp[yy * w + x] * noyau[k + rayon];
                    }
                    res[y * w + x] = s;
                }
            return res;
        }

        // ---------- « Mon image » ----------

        /// Charge l'image choisie : .png, .gif, .bmp, .jpg, .ico (redimensionnées à « taille » au plus)
        /// ou un vrai curseur .cur / .ani (gardé tel quel, avec sa pointe).
        /// pointe : « x,y » pour forcer la pointe ; vide = le point le plus en haut à gauche.
        public static ImageCurseur ChargerPerso(string chemin, int taille, string pointe, out string erreur)
        {
            erreur = null;
            try
            {
                if (string.IsNullOrEmpty(chemin) || !File.Exists(chemin)) { erreur = "image introuvable"; return null; }
                string ext = Path.GetExtension(chemin).ToLowerInvariant();
                ImageCurseur img;
                bool pointeDuFichier = false;
                if (ext == ".cur" || ext == ".ani")
                {
                    IntPtr h = Natif.LoadImage(IntPtr.Zero, chemin, 2, 0, 0, 0x10); // IMAGE_CURSOR, LR_LOADFROMFILE
                    if (h == IntPtr.Zero) { erreur = "ce fichier de curseur est illisible"; return null; }
                    img = Rendu.Decoder(h);
                    Natif.DestroyCursor(h);
                    if (img == null) { erreur = "ce fichier de curseur est illisible"; return null; }
                    for (int i = 0; i < img.Px.Length; i++)
                        if (img.Inv[i]) { img.Px[i] = 0xFFFFFFFFu; img.Inv[i] = false; }
                    pointeDuFichier = true;
                }
                else
                {
                    byte[] data = File.ReadAllBytes(chemin);
                    using (MemoryStream ms = new MemoryStream(data))
                    using (Image im = Image.FromStream(ms))
                        img = Reduire(im, Math.Max(8, Math.Min(256, taille)));
                }

                int px, py;
                if (LirePointe(pointe, out px, out py))
                {
                    img.Hx = Math.Max(0, Math.Min(img.L - 1, px));
                    img.Hy = Math.Max(0, Math.Min(img.H - 1, py));
                }
                else if (!pointeDuFichier)
                {
                    // La pointe = le point visible le plus en haut à gauche (le bout d'une flèche).
                    int meilleur = int.MaxValue;
                    img.Hx = 0; img.Hy = 0;
                    for (int y = 0; y < img.H; y++)
                        for (int x = 0; x < img.L; x++)
                            if ((img.Px[y * img.L + x] >> 24) > 100 && x + y < meilleur)
                            {
                                meilleur = x + y; img.Hx = x; img.Hy = y;
                            }
                }
                bool visible = false;
                foreach (uint p in img.Px) if ((p >> 24) > 0) { visible = true; break; }
                if (!visible) { erreur = "l'image est entièrement transparente"; return null; }
                return img;
            }
            catch (Exception ex)
            {
                erreur = "image illisible (" + ex.Message + ")";
                return null;
            }
        }

        static ImageCurseur Reduire(Image im, int taille)
        {
            float s = Math.Min(1f, taille / (float)Math.Max(im.Width, im.Height));
            int w = Math.Max(1, (int)Math.Round(im.Width * s)), h = Math.Max(1, (int)Math.Round(im.Height * s));
            using (Bitmap b = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(b))
                {
                    g.Clear(Color.Transparent);
                    g.InterpolationMode = s < 1f ? InterpolationMode.HighQualityBicubic : InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.CompositingMode = CompositingMode.SourceCopy;
                    g.DrawImage(im, new Rectangle(0, 0, w, h));
                }
                ImageCurseur img = new ImageCurseur();
                img.L = w; img.H = h;
                img.Px = new uint[w * h];
                img.Inv = new bool[w * h];
                BitmapData d = b.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    int[] ligne = new int[w];
                    for (int y = 0; y < h; y++)
                    {
                        Marshal.Copy(new IntPtr(d.Scan0.ToInt64() + (long)y * d.Stride), ligne, 0, w);
                        for (int x = 0; x < w; x++) img.Px[y * w + x] = unchecked((uint)ligne[x]);
                    }
                }
                finally { b.UnlockBits(d); }
                return img;
            }
        }

        static bool LirePointe(string s, out int x, out int y)
        {
            x = 0; y = 0;
            if (string.IsNullOrEmpty(s)) return false;
            string[] p = s.Split(',', ';', ' ');
            int n = 0;
            int[] v = new int[2];
            foreach (string q in p)
            {
                int k;
                if (q.Trim().Length > 0 && int.TryParse(q.Trim(), out k) && n < 2) v[n++] = k;
            }
            if (n < 2) return false;
            x = v[0]; y = v[1];
            return true;
        }

        // ---------- Aperçu (fenêtre Paramètres) ----------

        /// Petit aperçu : le curseur de Claude et son étiquette, sur fond sombre en haut et clair en bas.
        public static Bitmap Apercu(ImageCurseur curseur, int largeur, int hauteur, string etiquette, int couleur, string message)
        {
            Bitmap b = new Bitmap(largeur, hauteur, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(b))
            using (SolidBrush sombre = new SolidBrush(Color.FromArgb(30, 30, 30)))
            using (SolidBrush clair = new SolidBrush(Color.FromArgb(243, 243, 243)))
            {
                int moitie = hauteur / 2;
                g.FillRectangle(sombre, 0, 0, largeur, moitie);
                g.FillRectangle(clair, 0, moitie, largeur, hauteur - moitie);
                for (int k = 0; k < 2; k++)
                {
                    int haut = k == 0 ? 0 : moitie;
                    if (curseur == null)
                    {
                        using (Font f = new Font("Segoe UI", 9f, GraphicsUnit.Point))
                        using (SolidBrush gris = new SolidBrush(Color.FromArgb(150, 150, 150)))
                        {
                            StringFormat sf = new StringFormat();
                            sf.Alignment = StringAlignment.Center;
                            sf.LineAlignment = StringAlignment.Center;
                            g.DrawString(message ?? "", f, gris, new RectangleF(0, haut, largeur, moitie), sf);
                        }
                        continue;
                    }
                    int hx = 22, hy = haut + 7;
                    g.InterpolationMode = InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    using (Bitmap c = Rendu.VersBitmap(curseur.Px, curseur.L, curseur.H))
                    {
                        // Les grandes images perso sont réduites pour tenir dans l'aperçu.
                        float s = Math.Min(1f, (moitie - 9) / (float)Math.Max(1, curseur.H));
                        g.DrawImage(c, new RectangleF(hx - curseur.Hx * s, hy - curseur.Hy * s, curseur.L * s, curseur.H * s));
                    }
                    g.PixelOffsetMode = PixelOffsetMode.Default;
                    Rendu.DessinerEtiquette(g, hx + 13, hy + 21, etiquette, 0.9f, Rendu.Couleur(couleur));
                }
            }
            return b;
        }
    }
}
