using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace CurseurClaude
{
    /// Planche de contrôle (CurseurClaude.exe --planche fichier.png) : dessine tout dans une image
    /// pour vérifier le rendu sans toucher à l'écran.
    static class Planche
    {
        public static void Ecrire(string chemin)
        {
            Reglages r = Reglages.Charger();
            Color orange = Rendu.Couleur(r.Couleur);
            int c = 56, n = Curseurs.Ids.Length;
            int largeur = Math.Max(n * c, 960);
            int hauteur = 4 * c + 360;
            using (Bitmap b = new Bitmap(largeur, hauteur, PixelFormat.Format32bppArgb))
            using (Graphics g = Graphics.FromImage(b))
            using (SolidBrush sombre = new SolidBrush(Color.FromArgb(32, 32, 32)))
            using (Font f = new Font("Segoe UI", 10f, GraphicsUnit.Pixel))
            {
                g.Clear(Color.FromArgb(243, 243, 243));
                g.FillRectangle(sombre, 0, c, largeur, c);
                g.FillRectangle(sombre, 0, 3 * c, largeur, c);
                g.FillRectangle(sombre, largeur / 2, 4 * c, largeur / 2, 130);
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;

                // Lignes 1-2 : curseurs orange. Lignes 3-4 : copies fidèles (fantômes).
                for (int i = 0; i < n; i++)
                {
                    ImageCurseur img = Rendu.Decoder(Natif.LoadCursor(IntPtr.Zero, new IntPtr(Curseurs.Ids[i])));
                    if (img == null) continue;
                    using (Bitmap o = Rendu.VersBitmap(Rendu.Orange(img, r.Couleur, r.Contour), img.L, img.H))
                    {
                        g.DrawImage(o, new Rectangle(i * c + 12, 12, img.L, img.H));
                        g.DrawImage(o, new Rectangle(i * c + 12, c + 12, img.L, img.H));
                    }
                    using (Bitmap fa = Rendu.VersBitmap(Rendu.Fantome(img), img.L, img.H))
                    {
                        g.DrawImage(fa, new Rectangle(i * c + 12, 2 * c + 12, img.L, img.H));
                        g.DrawImage(fa, new Rectangle(i * c + 12, 3 * c + 12, img.L, img.H));
                    }
                }
                g.PixelOffsetMode = PixelOffsetMode.Default;

                // Curseur de Claude au repos (flèche + étiquette), sur fond clair puis sur fond sombre.
                ImageCurseur fl = Rendu.Decoder(Natif.LoadCursor(IntPtr.Zero, new IntPtr(32512)));
                int y0 = 4 * c + 20;
                for (int k = 0; k < 2; k++)
                {
                    int x0 = k == 0 ? 40 : largeur / 2 + 40;
                    if (fl != null)
                        using (Bitmap o = Rendu.VersBitmap(Rendu.Orange(fl, r.Couleur, r.Contour), fl.L, fl.H))
                            g.DrawImage(o, new Rectangle(x0 - fl.Hx, y0 - fl.Hy, fl.L, fl.H));
                    Rendu.DessinerEtiquette(g, x0 + 13, y0 + 20, r.Etiquette, 1f, orange);
                    Rendu.DessinerEtiquette(g, x0 + 160, y0 + 20, r.Etiquette + " écrit…", 1f, orange);
                    Rendu.DessinerEtiquette(g, x0 + 160, y0 + 60, r.Etiquette, 1.5f, orange);
                }

                // Ondes : clic gauche, clic droit, molette haut, molette bas, à plusieurs moments.
                int y1 = 4 * c + 150;
                int[] genres = { 0, 1, 3, 4 };
                float[] moments = { 0.05f, 0.25f, 0.5f, 0.8f };
                int x = 10;
                foreach (int ge in genres)
                {
                    foreach (float t in moments)
                    {
                        using (Bitmap o = Rendu.Onde(t, 1f, orange, ge)) g.DrawImage(o, x, y1);
                        x += Rendu.TailleOnde(1f) - 4;
                    }
                    x += 12;
                }
                g.DrawString("ondes : clic gauche, clic droit, molette haut, molette bas", f, Brushes.Black, 10, y1 + 70);

                // Icônes à plusieurs tailles, allumée puis en pause.
                int y2 = 4 * c + 260;
                x = 10;
                foreach (int t in new int[] { 16, 20, 24, 32, 48, 64 })
                {
                    using (Bitmap ic = Rendu.Icone(t, r.Couleur, r.Contour, false)) g.DrawImage(ic, x, y2);
                    using (Bitmap ic = Rendu.Icone(t, r.Couleur, r.Contour, true)) g.DrawImage(ic, x, y2 + 66);
                    x += t + 16;
                }
                b.Save(chemin, ImageFormat.Png);
            }
        }
    }
}
