using System;
using System.Diagnostics;
using System.IO;

// Les curseurs de Windows (flèche, main, barre de texte…) et leurs copies « Claude »
// dans le style choisi (orange, lueur orange ou image perso).
namespace CurseurClaude
{
    sealed class Curseurs
    {
        /// OCR_NORMAL, IBEAM, WAIT, CROSS, UP, SIZENWSE, SIZENESW, SIZEWE, SIZENS, SIZEALL, NO, HAND, APPSTARTING, HELP, PIN, PERSON
        public static readonly uint[] Ids = { 32512, 32513, 32514, 32515, 32516, 32642, 32643, 32644, 32645, 32646, 32648, 32649, 32650, 32651, 32671, 32672 };

        readonly IntPtr[] claude = new IntPtr[Ids.Length];
        readonly IntPtr[] originaux = new IntPtr[Ids.Length];
        readonly ImageCurseur[] sources = new ImageCurseur[Ids.Length];
        int couleur, contour, taillePerso;
        string style, imagePerso, pointePerso;
        ImageCurseur perso;
        string clePerso;

        /// La flèche de Claude dans le style actuel (pour la dessiner au repos).
        public ImageCurseur Fleche { get; private set; }
        /// Le style vraiment utilisé (« perso » retombe sur « lueur » si l'image est illisible).
        public string StyleActif { get; private set; }
        public string ErreurPerso { get; private set; }

        public bool Orange { get; private set; }
        public bool RechargementPrevu { get; private set; }
        int rechargerA;

        public Curseurs(Reglages r)
        {
            Configurer(r);
            Rafraichir(true);
        }

        public void Configurer(Reglages r)
        {
            couleur = r.Couleur;
            contour = r.Contour;
            style = Styles.Normaliser(r.Style);
            imagePerso = r.ImagePerso;
            taillePerso = r.TaillePerso;
            pointePerso = r.PointePerso;
        }

        /// Le curseur de Windows d'origine (avant toute transformation).
        public ImageCurseur Source(uint id)
        {
            int i = Array.IndexOf(Ids, id);
            return i >= 0 ? sources[i] : null;
        }

        /// Relit les curseurs de Windows (jamais pendant qu'ils sont remplacés) et refait les copies
        /// s'ils ont changé. Renvoie le nombre de curseurs de Windows qui avaient changé.
        public int Rafraichir(bool forcer)
        {
            if (Orange) return 0;
            int changes = 0;
            for (int i = 0; i < Ids.Length; i++)
            {
                ImageCurseur img = Rendu.Decoder(Natif.LoadCursor(IntPtr.Zero, new IntPtr(Ids[i])));
                if (img == null || img.MemesPixels(sources[i])) continue;
                sources[i] = img;
                changes++;
            }
            if (changes > 0 || forcer) Regenerer();
            return changes;
        }

        /// Refait toutes les copies dans le style actuel. Si le vrai curseur est déjà à Claude,
        /// le changement se voit tout de suite.
        public void Regenerer()
        {
            StyleActif = style;
            ErreurPerso = null;
            ImageCurseur image = null;
            if (style == Styles.Perso)
            {
                string cle = imagePerso + "|" + taillePerso + "|" + pointePerso + "|"
                    + (File.Exists(imagePerso) ? File.GetLastWriteTimeUtc(imagePerso).Ticks.ToString() : "absent");
                if (cle != clePerso)
                {
                    string erreur;
                    perso = Styles.ChargerPerso(imagePerso, taillePerso, pointePerso, out erreur);
                    ErreurPerso = erreur;
                    clePerso = cle;
                }
                image = perso;
                if (image == null)
                {
                    StyleActif = Styles.Lueur;
                    if (ErreurPerso == null) ErreurPerso = "image illisible";
                }
            }
            for (int i = 0; i < Ids.Length; i++)
            {
                if (sources[i] == null) continue;
                ImageCurseur res = image ?? Styles.Appliquer(sources[i], StyleActif, couleur, contour);
                IntPtr c = Rendu.CreerCurseur(res.Px, res.L, res.H, res.Hx, res.Hy);
                if (c == IntPtr.Zero) continue;
                if (claude[i] != IntPtr.Zero) Natif.DestroyCursor(claude[i]);
                claude[i] = c;
                if (Ids[i] == 32512) Fleche = res;
                if (Orange) Natif.SetSystemCursor(Natif.CopyIcon(c), Ids[i]);
            }
        }

        /// Tous les curseurs de Windows deviennent ceux de Claude (on garde une copie des vrais).
        public int MettreOrange()
        {
            if (Orange) return 0;
            int poses = 0;
            for (int i = 0; i < Ids.Length; i++)
            {
                if (claude[i] == IntPtr.Zero) continue;
                if (originaux[i] != IntPtr.Zero) Natif.DestroyCursor(originaux[i]);
                originaux[i] = Natif.CopyIcon(Natif.LoadCursor(IntPtr.Zero, new IntPtr(Ids[i])));
                if (Natif.SetSystemCursor(Natif.CopyIcon(claude[i]), Ids[i])) poses++;
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
