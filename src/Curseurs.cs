using System;
using System.Diagnostics;

// Les curseurs de Windows (flèche, main, barre de texte…) et leurs copies orange.
namespace CurseurClaude
{
    sealed class Curseurs
    {
        /// OCR_NORMAL, IBEAM, WAIT, CROSS, UP, SIZENWSE, SIZENESW, SIZEWE, SIZENS, SIZEALL, NO, HAND, APPSTARTING, HELP, PIN, PERSON
        public static readonly uint[] Ids = { 32512, 32513, 32514, 32515, 32516, 32642, 32643, 32644, 32645, 32646, 32648, 32649, 32650, 32651, 32671, 32672 };

        readonly IntPtr[] orange = new IntPtr[Ids.Length];
        readonly IntPtr[] originaux = new IntPtr[Ids.Length];
        readonly ImageCurseur[] sources = new ImageCurseur[Ids.Length];
        int couleur, contour;

        /// Flèche orange (pour dessiner le curseur de Claude au repos).
        public uint[] Fleche;
        public int FlecheL, FlecheH, FlecheHx, FlecheHy;

        public bool Orange { get; private set; }
        public bool RechargementPrevu { get; private set; }
        int rechargerA;

        public Curseurs(int couleur, int contour)
        {
            this.couleur = couleur;
            this.contour = contour;
            Rafraichir(true);
        }

        public void ChangerCouleurs(int c, int k)
        {
            couleur = c; contour = k;
            Rafraichir(true);
        }

        /// Refait les copies orange si les curseurs de Windows ont changé (jamais pendant qu'ils sont orange).
        public int Rafraichir(bool forcer)
        {
            if (Orange) return 0;
            int refaits = 0;
            for (int i = 0; i < Ids.Length; i++)
            {
                ImageCurseur img = Rendu.Decoder(Natif.LoadCursor(IntPtr.Zero, new IntPtr(Ids[i])));
                if (img == null) continue;
                if (!forcer && orange[i] != IntPtr.Zero && img.MemesPixels(sources[i])) continue;
                uint[] px = Rendu.Orange(img, couleur, contour);
                IntPtr c = Rendu.CreerCurseur(px, img.L, img.H, img.Hx, img.Hy);
                if (c == IntPtr.Zero) continue;
                if (orange[i] != IntPtr.Zero) Natif.DestroyCursor(orange[i]);
                orange[i] = c;
                sources[i] = img;
                refaits++;
                if (Ids[i] == 32512)
                {
                    Fleche = px; FlecheL = img.L; FlecheH = img.H; FlecheHx = img.Hx; FlecheHy = img.Hy;
                }
            }
            return refaits;
        }

        /// Tous les curseurs de Windows passent en orange (on garde une copie des vrais).
        public int MettreOrange()
        {
            if (Orange) return 0;
            int poses = 0;
            for (int i = 0; i < Ids.Length; i++)
            {
                if (orange[i] == IntPtr.Zero) continue;
                if (originaux[i] != IntPtr.Zero) Natif.DestroyCursor(originaux[i]);
                originaux[i] = Natif.CopyIcon(Natif.LoadCursor(IntPtr.Zero, new IntPtr(Ids[i])));
                if (Natif.SetSystemCursor(Natif.CopyIcon(orange[i]), Ids[i])) poses++;
            }
            Orange = true;
            RechargementPrevu = false;
            return poses;
        }

        /// Remet tout de suite les copies des vrais curseurs, puis (un peu plus tard) les recharge
        /// depuis Windows pour retrouver aussi les curseurs animés.
        public void RemettreNormaux()
        {
            if (!Orange) return;
            for (int i = 0; i < Ids.Length; i++)
                if (originaux[i] != IntPtr.Zero) Natif.SetSystemCursor(Natif.CopyIcon(originaux[i]), Ids[i]);
            Orange = false;
            RechargementPrevu = true;
            rechargerA = Environment.TickCount + 1500;
        }

        /// À appeler régulièrement : fait le rechargement prévu quand c'est l'heure. Renvoie la durée en ms (ou -1).
        public long RechargerSiPrevu()
        {
            if (!RechargementPrevu || Orange || Environment.TickCount - rechargerA < 0) return -1;
            RechargementPrevu = false;
            Stopwatch sw = Stopwatch.StartNew();
            Recharger();
            return sw.ElapsedMilliseconds;
        }

        /// Recharge les curseurs choisis dans Windows (efface toute trace d'orange).
        public static void Recharger()
        {
            Natif.SystemParametersInfo(Natif.SPI_SETCURSORS, 0, IntPtr.Zero, 0);
        }
    }
}
