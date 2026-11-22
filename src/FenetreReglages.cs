using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

// Fenêtre « Paramètres » : apparence du curseur de Claude (orange, lueur orange ou image perso),
// couleur, texte de l'étiquette et options. Chaque changement s'applique tout de suite.
namespace CurseurClaude
{
    sealed class FenetreReglages : Form
    {
        static readonly string[] Codes = { Styles.Orange, Styles.Lueur, Styles.Perso };
        static readonly string[] Titres = { "Orange (l'ancienne)", "Lueur orange (la nouvelle)", "Mon image" };
        static readonly string[] Descriptions = { "Orange plein, contour foncé.", "Blanc, bord et halo orange, comme un néon." };

        readonly Reglages reglages;
        readonly Moteur moteur;
        readonly Action surChangement;
        readonly float e;
        readonly RadioButton[] choix = new RadioButton[3];
        readonly PictureBox[] apercus = new PictureBox[3];
        readonly Panel[] cartes = new Panel[3];
        readonly Label infoPerso;
        readonly TextBox texte;
        readonly Panel pastille;
        readonly CheckBox cbActif, cbCaptures, cbDemarrage;
        bool chargement = true;

        int S(float v) { return (int)Math.Round(v * e); }

        public FenetreReglages(Reglages r, Moteur m, Action surChangement)
        {
            reglages = r;
            moteur = m;
            this.surChangement = surChangement;
            POINT c; Natif.GetCursorPos(out c);
            e = Natif.Echelle(c);

            Text = "Paramètres — Curseur orange de Claude";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.None;
            Font = new Font("Segoe UI", 9f * e, GraphicsUnit.Point);
            BackColor = Color.FromArgb(248, 248, 248);
            ClientSize = new Size(S(686), S(480));
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }

            Controls.Add(Titre("Apparence du curseur de Claude", 16, 12));

            for (int i = 0; i < 3; i++)
            {
                int k = i;
                Panel carte = new Panel();
                carte.Bounds = new Rectangle(S(16 + i * 222), S(42), S(210), S(190));
                carte.BackColor = Color.White;
                carte.Cursor = Cursors.Hand;
                carte.Paint += delegate (object s, PaintEventArgs pe) { PeindreCarte(pe.Graphics, k); };
                carte.Click += delegate { Choisir(k); };
                cartes[i] = carte;

                PictureBox ap = new PictureBox();
                ap.Bounds = new Rectangle(S(8), S(8), S(194), S(104));
                ap.SizeMode = PictureBoxSizeMode.Normal;
                ap.Click += delegate { Choisir(k); };
                apercus[i] = ap;
                carte.Controls.Add(ap);

                RadioButton rb = new RadioButton();
                rb.Text = Titres[i];
                rb.AutoCheck = false;
                rb.Font = new Font(Font, FontStyle.Bold);
                rb.Bounds = new Rectangle(S(8), S(118), S(196), S(24));
                rb.Click += delegate { Choisir(k); };
                choix[i] = rb;
                carte.Controls.Add(rb);

                if (i < 2)
                {
                    Label d = new Label();
                    d.Text = Descriptions[i];
                    d.ForeColor = Color.FromArgb(90, 90, 90);
                    d.Bounds = new Rectangle(S(8), S(146), S(196), S(36));
                    d.Click += delegate { Choisir(k); };
                    carte.Controls.Add(d);
                }
                else
                {
                    Button b = new Button();
                    b.Text = "Choisir une image…";
                    b.Bounds = new Rectangle(S(8), S(148), S(194), S(30));
                    b.Click += delegate { ChoisirImage(); };
                    carte.Controls.Add(b);
                }
                Controls.Add(carte);
            }

            infoPerso = new Label();
            infoPerso.Bounds = new Rectangle(S(16), S(238), S(656), S(36));
            infoPerso.ForeColor = Color.FromArgb(100, 100, 100);
            Controls.Add(infoPerso);

            Label lc = new Label();
            lc.Text = "Couleur :";
            lc.Bounds = new Rectangle(S(16), S(285), S(64), S(22));
            Controls.Add(lc);
            pastille = new Panel();
            pastille.Bounds = new Rectangle(S(82), S(282), S(30), S(24));
            pastille.BorderStyle = BorderStyle.FixedSingle;
            pastille.Cursor = Cursors.Hand;
            pastille.Click += delegate { ChoisirCouleur(); };
            Controls.Add(pastille);
            Button bc = new Button();
            bc.Text = "Changer…";
            bc.Bounds = new Rectangle(S(120), S(280), S(90), S(28));
            bc.Click += delegate { ChoisirCouleur(); };
            Controls.Add(bc);
            Button bd = new Button();
            bd.Text = "Orange de Claude";
            bd.Bounds = new Rectangle(S(216), S(280), S(130), S(28));
            bd.Click += delegate { reglages.Couleur = 0xD97757; Appliquer(); };
            Controls.Add(bd);

            Label lt = new Label();
            lt.Text = "Texte de l'étiquette :";
            lt.Bounds = new Rectangle(S(370), S(285), S(130), S(22));
            Controls.Add(lt);
            texte = new TextBox();
            texte.Bounds = new Rectangle(S(502), S(282), S(170), S(24));
            texte.MaxLength = 30;
            texte.TextChanged += delegate
            {
                if (chargement) return;
                string t = texte.Text.Trim();
                reglages.Etiquette = t.Length > 0 ? t : "Claude";
                Appliquer();
            };
            Controls.Add(texte);

            Controls.Add(Titre("Options", 16, 326));
            cbActif = Case("Activé : le curseur de Claude apparaît quand il prend le contrôle", 354);
            cbActif.CheckedChanged += delegate
            {
                if (chargement) return;
                reglages.Actif = cbActif.Checked;
                reglages.Enregistrer();
                moteur.Activer(reglages.Actif);
                Prevenir();
            };
            cbCaptures = Case("Visible sur les captures d'écran (pour filmer ton écran ; Claude, lui, le verra aussi)", 380);
            cbCaptures.CheckedChanged += delegate
            {
                if (chargement) return;
                reglages.Captures = cbCaptures.Checked;
                reglages.Enregistrer();
                moteur.Captures(reglages.Captures);
                Prevenir();
            };
            cbDemarrage = Case("Lancer au démarrage de Windows", 406);
            cbDemarrage.CheckedChanged += delegate
            {
                if (chargement) return;
                Demarrage.Activer(cbDemarrage.Checked);
                Prevenir();
            };

            Button demo = new Button();
            demo.Text = "Démonstration";
            demo.Bounds = new Rectangle(S(686 - 16 - 110 - 10 - 130), S(438), S(130), S(30));
            demo.Click += delegate { moteur.Demo(); };
            Controls.Add(demo);
            Button fermer = new Button();
            fermer.Text = "Fermer";
            fermer.Bounds = new Rectangle(S(686 - 16 - 110), S(438), S(110), S(30));
            fermer.Click += delegate { Close(); };
            Controls.Add(fermer);
            CancelButton = fermer;

            MettreAJour();
            chargement = false;
        }

        Label Titre(string t, int x, int y)
        {
            Label l = new Label();
            l.Text = t;
            l.Font = new Font("Segoe UI Semibold", 11f * e, GraphicsUnit.Point);
            l.Bounds = new Rectangle(S(x), S(y), S(500), S(26));
            return l;
        }

        CheckBox Case(string t, int y)
        {
            CheckBox cb = new CheckBox();
            cb.Text = t;
            cb.Bounds = new Rectangle(S(16), S(y), S(656), S(24));
            Controls.Add(cb);
            return cb;
        }

        void PeindreCarte(Graphics g, int i)
        {
            bool actif = Styles.Normaliser(reglages.Style) == Codes[i];
            Rectangle r = new Rectangle(0, 0, cartes[i].Width - 1, cartes[i].Height - 1);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (Pen p = new Pen(actif ? Rendu.Couleur(reglages.Couleur) : Color.FromArgb(215, 215, 215), actif ? S(3) : 1))
            {
                if (actif) r.Inflate(-S(1), -S(1));
                g.DrawRectangle(p, r);
            }
        }

        /// Recalcule les aperçus et remet les cases dans l'état des réglages.
        void MettreAJour()
        {
            bool avant = chargement;
            chargement = true;
            string style = Styles.Normaliser(reglages.Style);
            ImageCurseur src = moteur.FlecheWindows ?? Rendu.Decoder(Natif.LoadCursor(IntPtr.Zero, new IntPtr(32512)));
            string erreur = null;
            ImageCurseur perso = null;
            if (!string.IsNullOrEmpty(reglages.ImagePerso))
                perso = Styles.ChargerPerso(reglages.ImagePerso, reglages.TaillePerso, reglages.PointePerso, out erreur);
            ImageCurseur[] images = {
                src == null ? null : Styles.Appliquer(src, Styles.Orange, reglages.Couleur, reglages.Contour),
                src == null ? null : Styles.Appliquer(src, Styles.Lueur, reglages.Couleur, reglages.Contour),
                perso };
            for (int i = 0; i < 3; i++)
            {
                Image vieille = apercus[i].Image;
                apercus[i].Image = Styles.Apercu(images[i], apercus[i].Width, apercus[i].Height, reglages.Etiquette, reglages.Couleur,
                    i == 2 ? (string.IsNullOrEmpty(reglages.ImagePerso) ? "Aucune image choisie" : "Image illisible") : "");
                if (vieille != null) vieille.Dispose();
                choix[i].Checked = style == Codes[i];
                cartes[i].Invalidate();
            }
            if (style == Styles.Perso && perso == null)
            {
                infoPerso.ForeColor = Color.FromArgb(180, 60, 30);
                infoPerso.Text = "Ton image n'a pas pu être utilisée (" + (erreur ?? "aucune image") + ") : en attendant, Claude a la lueur orange.";
            }
            else
            {
                infoPerso.ForeColor = Color.FromArgb(100, 100, 100);
                infoPerso.Text = "« Mon image » : un PNG transparent (la pointe = son point le plus en haut à gauche) ou un vrai curseur .cur ou .ani. "
                    + "Elle remplace toutes les formes du curseur de Claude, et la couleur choisie sert à l'étiquette et aux cercles des clics.";
            }
            pastille.BackColor = Rendu.Couleur(reglages.Couleur);
            if (texte.Text != reglages.Etiquette) texte.Text = reglages.Etiquette;
            cbActif.Checked = reglages.Actif;
            cbCaptures.Checked = reglages.Captures;
            cbDemarrage.Checked = Demarrage.EstActif();
            chargement = avant;
        }

        void Choisir(int i)
        {
            if (chargement) return;
            if (Codes[i] == Styles.Perso && (string.IsNullOrEmpty(reglages.ImagePerso) || !File.Exists(reglages.ImagePerso)))
            {
                ChoisirImage();
                return;
            }
            reglages.Style = Codes[i];
            Appliquer();
        }

        void ChoisirImage()
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Title = "Choisis l'image du curseur de Claude";
                d.Filter = "Images et curseurs (*.png;*.gif;*.bmp;*.jpg;*.jpeg;*.ico;*.cur;*.ani)|*.png;*.gif;*.bmp;*.jpg;*.jpeg;*.ico;*.cur;*.ani|Tous les fichiers (*.*)|*.*";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                string erreur;
                if (Styles.ChargerPerso(d.FileName, reglages.TaillePerso, "", out erreur) == null)
                {
                    MessageBox.Show(this, "Je n'arrive pas à utiliser cette image : " + erreur + ".", "Curseur orange de Claude", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                try
                {
                    // Copie dans le dossier de l'appli : l'image reste même si l'original est déplacé.
                    Directory.CreateDirectory(Reglages.Dossier);
                    string dest = Path.Combine(Reglages.Dossier, "image-perso" + Path.GetExtension(d.FileName).ToLowerInvariant());
                    if (!string.Equals(Path.GetFullPath(d.FileName), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (string vieux in Directory.GetFiles(Reglages.Dossier, "image-perso.*"))
                            try { File.Delete(vieux); } catch (Exception) { }
                        File.Copy(d.FileName, dest, true);
                    }
                    reglages.ImagePerso = dest;
                }
                catch (Exception ex)
                {
                    Journal.Erreur("copie de l'image perso : " + ex.Message);
                    reglages.ImagePerso = d.FileName;
                }
                reglages.PointePerso = "";
                reglages.Style = Styles.Perso;
                Appliquer();
            }
        }

        void ChoisirCouleur()
        {
            using (ColorDialog d = new ColorDialog())
            {
                d.FullOpen = true;
                d.Color = Rendu.Couleur(reglages.Couleur);
                if (d.ShowDialog(this) != DialogResult.OK) return;
                reglages.Couleur = (d.Color.R << 16) | (d.Color.G << 8) | d.Color.B;
                Appliquer();
            }
        }

        /// Enregistre, refait les curseurs de Claude et les aperçus.
        void Appliquer()
        {
            reglages.Enregistrer();
            moteur.Apparence();
            MettreAJour();
            Prevenir();
        }

        void Prevenir()
        {
            if (surChangement != null) surChangement();
        }

        /// Appelé quand les réglages changent ailleurs (menu de l'icône, fichier relu).
        public void Actualiser()
        {
            if (!IsDisposed) MettreAJour();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                foreach (PictureBox p in apercus)
                    if (p != null && p.Image != null) { p.Image.Dispose(); p.Image = null; }
            base.Dispose(disposing);
        }
    }
}
