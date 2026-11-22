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
            int c = 56, n = Curseurs.Ids.Length, L = 6; // L lignes de curseurs
            int largeur = Math.Max(n * c, 960);
            int hauteur = L * c + 360;
            using (Bitmap b = new Bitmap(largeur, hauteur, PixelFormat.Format32bppArgb))
            using (Graphics g = Graphics.FromImage(b))
            using (SolidBrush sombre = new SolidBrush(Color.FromArgb(32, 32, 32)))
            using (Font f = new Font("Segoe UI", 10f, GraphicsUnit.Pixel))
            {
                g.Clear(Color.FromArgb(243, 243, 243));
                for (int l = 1; l < L; l += 2) g.FillRectangle(sombre, 0, l * c, largeur, c);
                g.FillRectangle(sombre, largeur / 2, L * c, largeur / 2, 130);
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;

                // Lignes 1-2 : style orange. Lignes 3-4 : style lueur. Lignes 5-6 : copies fidèles (fantômes).
                for (int i = 0; i < n; i++)
                {
                    ImageCurseur img = Rendu.Decoder(Natif.LoadCursor(IntPtr.Zero, new IntPtr(Curseurs.Ids[i])));
                    if (img == null) continue;
                    ImageCurseur[] versions = {
                        Styles.Appliquer(img, Styles.Orange, r.Couleur, r.Contour),
                        Styles.Appliquer(img, Styles.Lueur, r.Couleur, r.Contour) };
                    for (int v = 0; v < versions.Length; v++)
                    {
                        ImageCurseur ver = versions[v];
                        using (Bitmap o = Rendu.VersBitmap(ver.Px, ver.L, ver.H))
                            for (int l = 0; l < 2; l++)
                                g.DrawImage(o, new Rectangle(i * c + 12 + img.Hx - ver.Hx, (2 * v + l) * c + 6 + img.Hy - ver.Hy, ver.L, ver.H));
                    }
                    using (Bitmap fa = Rendu.VersBitmap(Rendu.Fantome(img), img.L, img.H))
                        for (int l = 0; l < 2; l++)
                            g.DrawImage(fa, new Rectangle(i * c + 12, (4 + l) * c + 6, img.L, img.H));
                }
                g.PixelOffsetMode = PixelOffsetMode.Default;

                // Curseur de Claude au repos dans le style choisi (flèche + étiquette), sur fond clair puis sombre.
                ImageCurseur fl = Rendu.Decoder(Natif.LoadCursor(IntPtr.Zero, new IntPtr(32512)));
                ImageCurseur flc = null;
                if (Styles.Normaliser(r.Style) == Styles.Perso)
                {
                    string err;
                    flc = Styles.ChargerPerso(r.ImagePerso, r.TaillePerso, r.PointePerso, out err);
                }
                if (flc == null && fl != null) flc = Styles.Appliquer(fl, Styles.Normaliser(r.Style) == Styles.Orange ? Styles.Orange : Styles.Lueur, r.Couleur, r.Contour);
                int y0 = L * c + 20;
                for (int k = 0; k < 2; k++)
                {
                    int x0 = k == 0 ? 40 : largeur / 2 + 40;
                    if (flc != null)
                        using (Bitmap o = Rendu.VersBitmap(flc.Px, flc.L, flc.H))
                            g.DrawImage(o, new Rectangle(x0 - flc.Hx, y0 - flc.Hy, flc.L, flc.H));
                    Rendu.DessinerEtiquette(g, x0 + 13, y0 + 20, r.Etiquette, 1f, orange);
                    Rendu.DessinerEtiquette(g, x0 + 160, y0 + 20, r.Etiquette + " écrit…", 1f, orange);
                    Rendu.DessinerEtiquette(g, x0 + 160, y0 + 60, r.Etiquette, 1.5f, orange);
                }

                // Ondes : clic gauche, clic droit, molette haut, molette bas, à plusieurs moments.
                int y1 = L * c + 150;
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
                int y2 = L * c + 260;
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
