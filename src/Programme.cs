using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

// Curseur orange de Claude : quand Claude prend le contrôle de l'ordinateur, il a son propre
// curseur orange et le tien reste là où tu l'as laissé.
namespace CurseurClaude
{
    static class Programme
    {
        public const string Version = "1.0";

        [STAThread]
        static int Main(string[] args)
        {
            try { Natif.SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch (Exception) { }
            string a0 = args.Length > 0 ? args[0].ToLowerInvariant() : "";

            if (a0 == "--gardien" && args.Length > 1) return Gardien(args[1]);
            if (a0 == "--ecrire-icone" && args.Length > 1)
            {
                Reglages r = Reglages.Charger();
                Rendu.EcrireIco(args[1], r.Couleur, r.Contour);
                return 0;
            }
            if (a0 == "--planche" && args.Length > 1) { Planche.Ecrire(args[1]); return 0; }

            bool premier;
            using (Mutex m = new Mutex(true, @"Local\CurseurClaude.Instance", out premier))
            {
                if (!premier)
                {
                    // L'appli tourne déjà : on lui transmet la demande.
                    string signal = "Bonjour";
                    if (a0 == "--demo") signal = "Demo";
                    else if (a0 == "--quitter") signal = "Quitter";
                    else if (a0 == "--etat") signal = "Etat";
                    else if (a0 == "--recharger") signal = "Recharger";
                    Signaux.Envoyer(signal);
                    return 0;
                }
                if (a0 == "--quitter" || a0 == "--etat" || a0 == "--recharger") return 0;

                Curseurs.Recharger(); // efface un éventuel reste d'orange laissé par un plantage
                LancerGardien();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += delegate (object s, ThreadExceptionEventArgs e) { Journal.Erreur("exception : " + e.Exception); };
                AppDomain.CurrentDomain.UnhandledException += delegate (object s, UnhandledExceptionEventArgs e)
                {
                    Journal.Erreur("plantage : " + e.ExceptionObject);
                    try { Curseurs.Recharger(); } catch (Exception) { }
                };
                Application.Run(new Appli(a0 == "--demo"));
            }
            return 0;
        }

        /// Petit processus gardien : attend la fin de l'appli (même un plantage ou un « fin de tâche »)
        /// et remet alors les curseurs normaux de Windows.
        static int Gardien(string texte)
        {
            uint pid;
            if (!uint.TryParse(texte, out pid)) return 1;
            IntPtr h = Natif.OpenProcess(0x00100000, false, pid); // SYNCHRONIZE
            if (h == IntPtr.Zero) return 1;
            Natif.WaitForSingleObject(h, 0xFFFFFFFF);
            Natif.CloseHandle(h);
            Curseurs.Recharger();
            return 0;
        }

        static void LancerGardien()
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(Application.ExecutablePath, "--gardien " + Process.GetCurrentProcess().Id);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                Process.Start(psi);
            }
            catch (Exception ex) { Journal.Erreur("gardien : " + ex.Message); }
        }
    }

    /// Petits signaux entre une deuxième copie lancée et l'appli qui tourne déjà.
    static class Signaux
    {
        static readonly string[] Noms = { "Demo", "Quitter", "Etat", "Recharger", "Bonjour" };

        public static void Envoyer(string nom)
        {
            try { using (EventWaitHandle ev = EventWaitHandle.OpenExisting(@"Local\CurseurClaude." + nom)) ev.Set(); }
            catch (Exception) { }
        }

        public static List<EventWaitHandle> Ecouter(Control pompe, Action<string> action)
        {
            List<EventWaitHandle> liste = new List<EventWaitHandle>();
            foreach (string n in Noms)
            {
                string nom = n;
                EventWaitHandle ev = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\CurseurClaude." + nom);
                ThreadPool.RegisterWaitForSingleObject(ev, delegate
                {
                    try { pompe.BeginInvoke(new Action(delegate { action(nom); })); } catch (Exception) { }
                }, null, -1, false);
                liste.Add(ev);
            }
            return liste;
        }
    }

    /// Lancement automatique à l'ouverture de la session Windows.
    static class Demarrage
    {
        const string Cle = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string Nom = "CurseurClaude";

        public static bool EstActif()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(Cle))
                {
                    string v = k == null ? null : k.GetValue(Nom) as string;
                    return v != null && v.IndexOf(Application.ExecutablePath, StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch (Exception) { return false; }
        }

        public static void Activer(bool oui)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(Cle))
                {
                    if (oui) k.SetValue(Nom, "\"" + Application.ExecutablePath + "\"");
                    else k.DeleteValue(Nom, false);
                }
            }
            catch (Exception ex) { Journal.Erreur("démarrage automatique : " + ex.Message); }
        }
    }

    /// L'appli : l'icône près de l'horloge, son menu, et la surveillance de l'outil de contrôle de Claude.
    sealed class Appli : ApplicationContext
    {
        readonly Reglages reglages;
        readonly Moteur moteur;
        readonly NotifyIcon icone;
        readonly Control pompe;
        readonly System.Windows.Forms.Timer veille;
        readonly List<EventWaitHandle> signaux;
        readonly Dictionary<uint, string> noms = new Dictionary<uint, string>();
        ToolStripMenuItem miActif, miCaptures, miDemarrage;
        IntPtr hIcone = IntPtr.Zero;
        bool sessionVue, fini;

        public Appli(bool demo)
        {
            reglages = Reglages.Charger();
            Journal.Activer(reglages.JournalActif || File.Exists(Path.Combine(Reglages.Dossier, "journal.on")));
            Journal.Ecrire("--- Curseur Claude " + Programme.Version + " démarre ---");
            pompe = new Control();
            IntPtr h = pompe.Handle;

            moteur = new Moteur(reglages);
            moteur.Demarrer();

            icone = new NotifyIcon();
            icone.Text = "Curseur orange de Claude";
            icone.ContextMenuStrip = CreerMenu();
            MettreIcone();
            icone.MouseDoubleClick += delegate (object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) moteur.Demo(); };
            icone.Visible = true;

            if (!reglages.Existait)
            {
                // Premier lancement : démarrage avec Windows et petite explication.
                Demarrage.Activer(true);
                miDemarrage.Checked = Demarrage.EstActif();
                reglages.Enregistrer();
                icone.ShowBalloonTip(10000, "Le curseur orange de Claude est prêt",
                    "Quand Claude prendra le contrôle de l'ordinateur, sa souris sera orange et la tienne restera là où tu l'as laissée. Double-clique sur la flèche orange près de l'horloge pour voir une démo.",
                    ToolTipIcon.Info);
            }

            signaux = Signaux.Ecouter(pompe, Signal);
            SystemEvents.SessionEnding += delegate { moteur.Arreter(); Curseurs.Recharger(); };
            veille = new System.Windows.Forms.Timer();
            veille.Interval = 1000;
            veille.Tick += VeillerSession;
            veille.Start();
            if (demo) moteur.Demo();
        }

        ContextMenuStrip CreerMenu()
        {
            ContextMenuStrip m = new ContextMenuStrip();
            ToolStripMenuItem titre = new ToolStripMenuItem("Curseur orange de Claude");
            titre.Enabled = false;
            titre.Font = new Font(titre.Font, FontStyle.Bold);
            m.Items.Add(titre);
            m.Items.Add(new ToolStripSeparator());

            miActif = new ToolStripMenuItem("Activé");
            miActif.Checked = reglages.Actif;
            miActif.Click += delegate
            {
                reglages.Actif = !reglages.Actif;
                miActif.Checked = reglages.Actif;
                reglages.Enregistrer();
                moteur.Activer(reglages.Actif);
                MettreIcone();
            };
            m.Items.Add(miActif);

            ToolStripMenuItem demo = new ToolStripMenuItem("Faire une démonstration");
            demo.Click += delegate { moteur.Demo(); };
            m.Items.Add(demo);

            ToolStripMenuItem aide = new ToolStripMenuItem("Comment ça marche ?");
            aide.Click += delegate { Aide(); };
            m.Items.Add(aide);
            m.Items.Add(new ToolStripSeparator());

            miCaptures = new ToolStripMenuItem("Visible sur les captures d'écran (pour filmer)");
            miCaptures.Checked = reglages.Captures;
            miCaptures.Click += delegate
            {
                reglages.Captures = !reglages.Captures;
                miCaptures.Checked = reglages.Captures;
                reglages.Enregistrer();
                moteur.Captures(reglages.Captures);
            };
            m.Items.Add(miCaptures);

            miDemarrage = new ToolStripMenuItem("Lancer au démarrage de Windows");
            miDemarrage.Checked = Demarrage.EstActif();
            miDemarrage.Click += delegate
            {
                Demarrage.Activer(!Demarrage.EstActif());
                miDemarrage.Checked = Demarrage.EstActif();
            };
            m.Items.Add(miDemarrage);
            m.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem quitter = new ToolStripMenuItem("Quitter");
            quitter.Click += delegate { Quitter(); };
            m.Items.Add(quitter);
            return m;
        }

        void Aide()
        {
            MessageBox.Show(
                "Quand Claude prend le contrôle de ton ordinateur :\n\n" +
                "•  sa souris devient ORANGE, avec l'étiquette « Claude » ;\n" +
                "•  ta souris à toi reste dessinée là où tu l'as laissée ;\n" +
                "•  dès que tu bouges ta souris, tu la récupères à cet endroit, et le curseur orange reste là où Claude travaillait ;\n" +
                "•  un cercle orange montre chaque clic de Claude, et « Claude écrit… » apparaît quand il tape.\n\n" +
                "Pendant un clic ou un glisser-déposer de Claude, ta souris attend moins d'une seconde avant de revenir : comme ça, son clic ne part pas au mauvais endroit.\n\n" +
                "Le curseur orange n'apparaît pas sur les captures d'écran (Claude ne le voit pas). Pour le filmer, coche « Visible sur les captures d'écran ».",
                "Curseur orange de Claude", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        void MettreIcone()
        {
            int t = Math.Max(16, SystemInformation.SmallIconSize.Width);
            using (Bitmap b = Rendu.Icone(t, reglages.Couleur, reglages.Contour, !reglages.Actif))
            {
                IntPtr h = b.GetHicon();
                icone.Icon = Icon.FromHandle(h);
                if (hIcone != IntPtr.Zero) Natif.DestroyIcon(hIcone);
                hIcone = h;
            }
            icone.Text = reglages.Actif ? "Curseur orange de Claude" : "Curseur orange de Claude (en pause)";
        }

        void Signal(string nom)
        {
            switch (nom)
            {
                case "Demo": moteur.Demo(); break;
                case "Quitter": Quitter(); break;
                case "Etat": moteur.EcrireEtat(); break;
                case "Recharger":
                    reglages.CopierDe(Reglages.Charger());
                    Journal.Activer(reglages.JournalActif || File.Exists(Path.Combine(Reglages.Dossier, "journal.on")));
                    miActif.Checked = reglages.Actif;
                    miCaptures.Checked = reglages.Captures;
                    moteur.Activer(reglages.Actif);
                    moteur.Captures(reglages.Captures);
                    moteur.Couleurs();
                    MettreIcone();
                    Journal.Ecrire("réglages relus");
                    break;
                case "Bonjour":
                    icone.ShowBalloonTip(5000, "Déjà lancé", "Le curseur orange de Claude tourne déjà : c'est la flèche orange près de l'horloge.", ToolTipIcon.Info);
                    break;
            }
        }

        /// Cherche la grande vitre transparente que l'appli Claude pose sur l'écran pendant qu'elle contrôle l'ordinateur.
        void VeillerSession(object s, EventArgs e)
        {
            bool trouve = false;
            try
            {
                StringBuilder classe = new StringBuilder(64);
                Natif.EnumWindows(delegate (IntPtr h, IntPtr l)
                {
                    if (!Natif.IsWindowVisible(h)) return true;
                    int ex = Natif.GetWindowLong(h, -20);
                    if ((ex & 0x08080028) != 0x08080028) return true; // NOACTIVATE | LAYERED | TRANSPARENT | TOPMOST
                    classe.Length = 0;
                    Natif.GetClassName(h, classe, 64);
                    if (classe.ToString() != "Chrome_WidgetWin_1") return true;
                    uint pid;
                    Natif.GetWindowThreadProcessId(h, out pid);
                    string nom;
                    if (!noms.TryGetValue(pid, out nom)) { nom = Natif.NomProcessus(pid); noms[pid] = nom; }
                    if (!string.Equals(nom, "claude", StringComparison.OrdinalIgnoreCase)) return true;
                    RECT r;
                    Natif.GetWindowRect(h, out r);
                    RECT ecran = Natif.Ecran(new POINT((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2));
                    if (r.Left <= ecran.Left && r.Top <= ecran.Top && r.Right >= ecran.Right && r.Bottom >= ecran.Bottom)
                    {
                        trouve = true;
                        return false;
                    }
                    return true;
                }, IntPtr.Zero);
                if (noms.Count > 500) noms.Clear();
            }
            catch (Exception ex) { Journal.Erreur("veille : " + ex.Message); }
            if (trouve != sessionVue)
            {
                sessionVue = trouve;
                moteur.Session(trouve);
            }
        }

        void Quitter()
        {
            if (fini) return;
            fini = true;
            veille.Stop();
            icone.Visible = false;
            moteur.Arreter();
            Curseurs.Recharger();
            Journal.Ecrire("--- arrêt ---");
            Journal.Vider();
            ExitThread();
        }
    }
}
