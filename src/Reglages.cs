using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace CurseurClaude
{
    /// Réglages enregistrés dans %LOCALAPPDATA%\CurseurClaude\reglages.txt (modifiables à la main).
    sealed class Reglages
    {
        public bool Actif = true;
        public bool Captures = false;
        public bool JournalActif = false;
        public int Couleur = 0xD97757;  // orange de Claude
        public int Contour = 0x3B1A0E;  // brun très foncé
        public string Etiquette = "Claude";
        public int DelaiRepos = 20;     // secondes pendant lesquelles le curseur de Claude reste affiché après son dernier geste
        public long[] Marques = { 100 }; // signature des événements de l'outil de contrôle de Claude
        public bool Existait;

        public static string Dossier
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CurseurClaude"); }
        }

        static string Fichier { get { return Path.Combine(Dossier, "reglages.txt"); } }

        public static Reglages Charger()
        {
            Reglages r = new Reglages();
            try
            {
                if (!File.Exists(Fichier)) return r;
                r.Existait = true;
                foreach (string ligne in File.ReadAllLines(Fichier, Encoding.UTF8))
                {
                    string l = ligne.Trim();
                    if (l.Length == 0 || l.StartsWith("#")) continue;
                    int eg = l.IndexOf('=');
                    if (eg <= 0) continue;
                    string k = l.Substring(0, eg).Trim().ToLowerInvariant();
                    string v = l.Substring(eg + 1).Trim();
                    switch (k)
                    {
                        case "actif": r.Actif = v != "0"; break;
                        case "captures": r.Captures = v == "1"; break;
                        case "journal": r.JournalActif = v == "1"; break;
                        case "couleur": r.Couleur = Hexa(v, r.Couleur); break;
                        case "contour": r.Contour = Hexa(v, r.Contour); break;
                        case "etiquette": if (v.Length > 0) r.Etiquette = v; break;
                        case "delai":
                            int d;
                            if (int.TryParse(v, out d)) r.DelaiRepos = Math.Max(2, Math.Min(3600, d));
                            break;
                        case "marques":
                            List<long> m = new List<long>();
                            foreach (string bout in v.Split(new char[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                            {
                                long n;
                                string b = bout.Trim();
                                if (b.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                                {
                                    if (long.TryParse(b.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out n)) m.Add(n);
                                }
                                else if (long.TryParse(b, out n)) m.Add(n);
                            }
                            if (m.Count > 0) r.Marques = m.ToArray();
                            break;
                    }
                }
            }
            catch (Exception) { }
            return r;
        }

        static int Hexa(string v, int defaut)
        {
            v = v.TrimStart('#');
            int n;
            if (v.Length == 6 && int.TryParse(v, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out n)) return n;
            return defaut;
        }

        public void CopierDe(Reglages o)
        {
            Actif = o.Actif; Captures = o.Captures; JournalActif = o.JournalActif;
            Couleur = o.Couleur; Contour = o.Contour; Etiquette = o.Etiquette;
            DelaiRepos = o.DelaiRepos; Marques = o.Marques;
        }

        public void Enregistrer()
        {
            try
            {
                Directory.CreateDirectory(Dossier);
                StringBuilder s = new StringBuilder();
                s.AppendLine("# Réglages du curseur orange de Claude.");
                s.AppendLine("# Après une modification à la main : menu de l'icône > Quitter, puis relancer l'appli.");
                s.AppendLine("actif=" + (Actif ? 1 : 0));
                s.AppendLine("# 1 = le curseur orange apparaît aussi sur les captures et les vidéos d'écran");
                s.AppendLine("captures=" + (Captures ? 1 : 0));
                s.AppendLine("# couleurs en hexadécimal RRVVBB");
                s.AppendLine("couleur=" + Couleur.ToString("X6"));
                s.AppendLine("contour=" + Contour.ToString("X6"));
                s.AppendLine("etiquette=" + Etiquette);
                s.AppendLine("# secondes d'affichage du curseur de Claude après son dernier geste");
                s.AppendLine("delai=" + DelaiRepos);
                s.AppendLine("# signature des gestes de Claude (100 pour l'appli Claude de novembre 2026)");
                List<string> m = new List<string>();
                foreach (long x in Marques) m.Add(x.ToString(CultureInfo.InvariantCulture));
                s.AppendLine("marques=" + string.Join(",", m.ToArray()));
                s.AppendLine("# 1 = journal détaillé dans journal.txt");
                s.AppendLine("journal=" + (JournalActif ? 1 : 0));
                File.WriteAllText(Fichier, s.ToString(), new UTF8Encoding(false));
                Existait = true;
            }
            catch (Exception) { }
        }
    }

    /// Journal facultatif (journal.txt) et erreurs (toujours notées dans erreurs.txt).
    static class Journal
    {
        static readonly object verrou = new object();
        static readonly StringBuilder tampon = new StringBuilder();
        static System.Threading.Timer vidage;
        public static bool Actif { get; private set; }

        public static void Activer(bool oui)
        {
            Actif = oui;
            if (oui && vidage == null)
            {
                try
                {
                    Directory.CreateDirectory(Reglages.Dossier);
                    string f = Path.Combine(Reglages.Dossier, "journal.txt");
                    if (File.Exists(f) && new FileInfo(f).Length > 2000000) File.Delete(f);
                }
                catch (Exception) { }
                vidage = new System.Threading.Timer(delegate { Vider(); }, null, 500, 500);
            }
        }

        public static void Ecrire(string s)
        {
            if (!Actif) return;
            lock (verrou) tampon.Append(DateTime.Now.ToString("HH:mm:ss.fff")).Append("  ").Append(s).Append("\r\n");
        }

        public static void Erreur(string s)
        {
            Ecrire("ERREUR " + s);
            try
            {
                Directory.CreateDirectory(Reglages.Dossier);
                File.AppendAllText(Path.Combine(Reglages.Dossier, "erreurs.txt"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + s + "\r\n", Encoding.UTF8);
            }
            catch (Exception) { }
        }

        public static void Vider()
        {
            string s;
            lock (verrou)
            {
                if (tampon.Length == 0) return;
                s = tampon.ToString();
                tampon.Clear();
            }
            try { File.AppendAllText(Path.Combine(Reglages.Dossier, "journal.txt"), s, Encoding.UTF8); }
            catch (Exception) { }
        }
    }
}
