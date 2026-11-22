using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

// Le moteur tourne dans son propre fil. Il voit passer chaque événement souris et clavier
// (crochets « bas niveau »), reconnaît ceux de Claude (signature 100 posée par son outil de
// contrôle) et gère les deux curseurs :
//  - quand Claude bouge la souris, le vrai curseur devient orange (c'est le sien) et ton curseur
//    reste dessiné là où tu l'avais laissé ;
//  - quand tu bouges ta souris, tu la récupères à cet endroit, et le curseur orange de Claude
//    reste dessiné là où il travaillait.
namespace CurseurClaude
{
    sealed class Moteur
    {
        public const long MARQUE_NOUS = 0x43555253;  // nos propres clics rejoués (« CURS »)
        public const long MARQUE_DEMO = 0x44454D4F;  // mouvements de la démonstration (« DEMO »)
        const int GRACE_MS = 900;      // après un geste de Claude, ta souris attend un peu avant de revenir
        const int GESTE_MS = 250;      // en plein geste de Claude (aller, appuyer, relâcher) : tes clics attendent
        const int TENUE_MAX_MS = 10000; // Claude tient un bouton (glisser-déposer) : au plus 10 s
        const int DUREE_ONDE = 450;

        readonly Reglages reglages;
        Thread fil;
        Control pompe;
        readonly ManualResetEvent pret = new ManualResetEvent(false);
        HookProc procSouris, procClavier;
        IntPtr crochetSouris, crochetClavier;
        uint dernierAppel;
        int derniereReinstallation = Environment.TickCount - 60000;

        Curseurs curseurs;
        Calque fantome, claude;
        Calque[] ondes;
        int[] ondeDebut, ondeGenre;
        POINT[] ondeCentre;
        float[] ondeEchelle;
        bool[] ondeActive;
        System.Windows.Forms.Timer anim, entretien;
        int tickEntretien;

        // État : lu et écrit seulement dans le fil du moteur.
        bool actif;
        bool modeClaude;              // le vrai curseur appartient à Claude (il est orange)
        POINT posUser, posClaude;
        bool posClaudeConnue;
        int boutonsClaude;
        int dernierClaude = Environment.TickCount - 100000;     // dernier geste de souris de Claude
        int derniereActivite = Environment.TickCount - 100000;  // souris ou clavier
        int frappeJusqua = Environment.TickCount - 100000;
        bool frappeAffichee;
        bool session, sessionPendantCeMode;
        int fantomeHx, fantomeHy;
        int mouvementsGardes;

        // Curseur de Claude : flèche + étiquette (au repos) ou étiquette seule (quand le vrai curseur est à lui).
        bool claudeRepos;
        string cleClaude;
        int origineX, origineY;
        byte alphaClaude, alphaVoulu;

        // Démonstration
        PointF[] demoPoints;
        int demoDebut, demoDuree, demoClicIdx;
        float[] demoClics;
        bool demoFrappe;

        public Moteur(Reglages r)
        {
            reglages = r;
            actif = r.Actif;
        }

        // ================= Démarrage et arrêt =================

        public void Demarrer()
        {
            fil = new Thread(Boucle);
            fil.Name = "Moteur du curseur de Claude";
            fil.IsBackground = true;
            fil.Priority = ThreadPriority.Highest;
            fil.SetApartmentState(ApartmentState.STA);
            fil.Start();
            pret.WaitOne(10000);
        }

        void Boucle()
        {
            try
            {
                pompe = new Control();
                IntPtr h = pompe.Handle; // la fenêtre de messages du moteur naît dans ce fil
                curseurs = new Curseurs(reglages);
                if (curseurs.ErreurPerso != null) Journal.Erreur("image perso : " + curseurs.ErreurPerso);
                bool cap = reglages.Captures;
                Action verifier = () => Programmer(VerifierCalques);
                fantome = new Calque("ton curseur", cap, verifier);
                claude = new Calque("curseur de Claude", cap, verifier);
                ondes = new Calque[4];
                ondeDebut = new int[4]; ondeGenre = new int[4]; ondeCentre = new POINT[4]; ondeEchelle = new float[4]; ondeActive = new bool[4];
                for (int i = 0; i < ondes.Length; i++) ondes[i] = new Calque("onde " + (i + 1), cap, null);
                anim = new System.Windows.Forms.Timer();
                anim.Interval = 15;
                anim.Tick += Animer;
                entretien = new System.Windows.Forms.Timer();
                entretien.Interval = 500;
                entretien.Tick += Entretenir;
                entretien.Start();
                procSouris = Souris;
                procClavier = Clavier;
                Accrocher();
            }
            catch (Exception ex) { Journal.Erreur("démarrage du moteur : " + ex); }
            finally { pret.Set(); }
            Application.Run();
        }

        void Accrocher()
        {
            Decrocher();
            IntPtr mod = Natif.GetModuleHandle(null);
            crochetSouris = Natif.SetWindowsHookEx(14, procSouris, mod, 0);   // WH_MOUSE_LL
            crochetClavier = Natif.SetWindowsHookEx(13, procClavier, mod, 0); // WH_KEYBOARD_LL
            dernierAppel = (uint)Environment.TickCount;
            Journal.Ecrire("crochets posés : souris " + (crochetSouris != IntPtr.Zero) + ", clavier " + (crochetClavier != IntPtr.Zero));
        }

        void Decrocher()
        {
            if (crochetSouris != IntPtr.Zero) { Natif.UnhookWindowsHookEx(crochetSouris); crochetSouris = IntPtr.Zero; }
            if (crochetClavier != IntPtr.Zero) { Natif.UnhookWindowsHookEx(crochetClavier); crochetClavier = IntPtr.Zero; }
        }

        void Programmer(Action a)
        {
            try { if (pompe != null && pompe.IsHandleCreated) pompe.BeginInvoke(a); }
            catch (Exception) { }
        }

        /// Rend la souris, remet les vrais curseurs, retire les crochets et ferme le fil du moteur.
        public void Arreter()
        {
            if (pompe == null || !pompe.IsHandleCreated) return;
            try
            {
                IAsyncResult r = pompe.BeginInvoke(new Action(delegate
                {
                    try
                    {
                        if (modeClaude) RendreSouris(posUser, true);
                        curseurs.RemettreNormaux();
                        Curseurs.Recharger();
                        Decrocher();
                        anim.Stop();
                        entretien.Stop();
                        fantome.Detruire();
                        claude.Detruire();
                        foreach (Calque o in ondes) o.Detruire();
                    }
                    catch (Exception ex) { Journal.Erreur("arrêt du moteur : " + ex); }
                    Application.ExitThread();
                }));
                r.AsyncWaitHandle.WaitOne(3000);
            }
            catch (Exception) { }
        }

        // ================= Commandes venues du menu =================

        public void Activer(bool oui)
        {
            Programmer(delegate
            {
                if (!oui && modeClaude) RendreSouris(posUser, true);
                actif = oui;
                if (!oui)
                {
                    claude.Cacher(); alphaClaude = 0; alphaVoulu = 0;
                    fantome.Cacher();
                    for (int i = 0; i < ondes.Length; i++) { ondeActive[i] = false; ondes[i].Cacher(); }
                    demoPoints = null;
                }
                Journal.Ecrire(oui ? "activé" : "désactivé");
            });
        }

        public void Captures(bool visibles)
        {
            Programmer(delegate
            {
                fantome.Captures(visibles);
                claude.Captures(visibles);
                foreach (Calque o in ondes) o.Captures(visibles);
            });
        }

        /// Le style, la couleur ou l'étiquette ont changé : on refait les curseurs de Claude.
        public void Apparence()
        {
            Programmer(delegate
            {
                curseurs.Configurer(reglages);
                curseurs.Regenerer();
                if (curseurs.ErreurPerso != null) Journal.Erreur("image perso : " + curseurs.ErreurPerso);
                Journal.Ecrire("apparence : " + curseurs.StyleActif);
                cleClaude = null;
                DessinerClaude();
            });
        }

        /// La flèche normale de Windows (pour les aperçus de la fenêtre Paramètres).
        public ImageCurseur FlecheWindows
        {
            get { return curseurs == null ? null : curseurs.Source(32512); }
        }

        /// Le style vraiment utilisé et l'éventuelle erreur de l'image perso.
        public string StyleActif { get { return curseurs == null ? reglages.Style : curseurs.StyleActif; } }

        /// L'outil de contrôle de Claude est-il ouvert ? (sa grande vitre transparente est à l'écran)
        public void Session(bool active)
        {
            Programmer(delegate
            {
                if (active == session) return;
                session = active;
                Journal.Ecrire(active ? "l'outil de contrôle de Claude s'ouvre" : "l'outil de contrôle de Claude se ferme");
                if (active)
                {
                    if (modeClaude) sessionPendantCeMode = true;
                    return;
                }
                // Le curseur de Claude s'efface 2,5 s après la fin.
                derniereActivite = Environment.TickCount - Math.Max(0, reglages.DelaiRepos * 1000 - 2500);
                if (modeClaude && sessionPendantCeMode && boutonsClaude == 0 && Environment.TickCount - dernierClaude > GRACE_MS)
                {
                    Journal.Ecrire("Claude a fini : ta souris revient chez toi");
                    RendreSouris(posUser, true);
                }
            });
        }

        public void Demo() { Programmer(DemarrerDemo); }

        public void EcrireEtat()
        {
            Programmer(delegate
            {
                try
                {
                    StringBuilder s = new StringBuilder();
                    POINT c; Natif.GetCursorPos(out c);
                    s.AppendLine("actif=" + actif);
                    s.AppendLine("modeClaude=" + modeClaude);
                    s.AppendLine("curseursOrange=" + curseurs.Orange);
                    s.AppendLine("vraiCurseur=" + c);
                    s.AppendLine("posUser=" + posUser);
                    s.AppendLine("posClaude=" + posClaude + " connue=" + posClaudeConnue);
                    s.AppendLine("boutonsClaude=" + boutonsClaude);
                    s.AppendLine("fantome=" + (fantome.Affiche ? "visible " + fantome.X + "," + fantome.Y + " " + fantome.L + "x" + fantome.H + " reduit=" + Natif.IsIconic(fantome.Handle) + " visibleWin=" + Natif.IsWindowVisible(fantome.Handle) : "cache"));
                    s.AppendLine("claude=" + (claude.Affiche ? "visible " + claude.X + "," + claude.Y + " " + claude.L + "x" + claude.H + " alpha=" + claude.Opacite + " repos=" + claudeRepos + " reduit=" + Natif.IsIconic(claude.Handle) : "cache"));
                    s.AppendLine("session=" + session);
                    s.AppendLine("crochets=" + (crochetSouris != IntPtr.Zero) + "," + (crochetClavier != IntPtr.Zero));
                    Directory.CreateDirectory(Reglages.Dossier);
                    File.WriteAllText(Path.Combine(Reglages.Dossier, "etat.txt"), s.ToString(), new UTF8Encoding(false));
                }
                catch (Exception ex) { Journal.Erreur("état : " + ex.Message); }
            });
        }

        // ================= Crochets =================

        unsafe IntPtr Souris(int code, IntPtr w, IntPtr l)
        {
            if (code >= 0)
            {
                dernierAppel = (uint)Environment.TickCount;
                if (actif)
                {
                    MSLLHOOKSTRUCT* m = (MSLLHOOKSTRUCT*)l;
                    long extra = m->dwExtraInfo.ToInt64();
                    bool injecte = (m->flags & 3) != 0; // LLMHF_INJECTED | LLMHF_LOWER_IL_INJECTED
                    if (injecte && extra == MARQUE_DEMO && demoPoints == null)
                        return new IntPtr(1); // reste d'une démonstration arrêtée
                    if (!(injecte && extra == MARQUE_NOUS))
                    {
                        try
                        {
                            if (injecte && (extra == MARQUE_DEMO || EstClaude(extra)))
                                EvenementClaude((int)w, m->pt, m->mouseData);
                            else if (modeClaude && EvenementUtilisateur((int)w, m->pt, m->mouseData, extra))
                                return new IntPtr(1); // l'événement est retenu
                        }
                        catch (Exception ex) { Journal.Erreur("crochet souris : " + ex); }
                    }
                }
            }
            return Natif.CallNextHookEx(IntPtr.Zero, code, w, l);
        }

        unsafe IntPtr Clavier(int code, IntPtr w, IntPtr l)
        {
            if (code >= 0)
            {
                dernierAppel = (uint)Environment.TickCount;
                if (actif)
                {
                    KBDLLHOOKSTRUCT* k = (KBDLLHOOKSTRUCT*)l;
                    if ((k->flags & 0x10) != 0 && EstClaude(k->dwExtraInfo.ToInt64())) // LLKHF_INJECTED
                    {
                        int t = Environment.TickCount;
                        derniereActivite = t;
                        frappeJusqua = t + 1300;
                        if (!frappeAffichee && posClaudeConnue) Programmer(DessinerClaude);
                    }
                }
            }
            return Natif.CallNextHookEx(IntPtr.Zero, code, w, l);
        }

        bool EstClaude(long extra)
        {
            long[] m = reglages.Marques;
            for (int i = 0; i < m.Length; i++) if (m[i] == extra) return true;
            return false;
        }

        void EvenementClaude(int msg, POINT pt, uint data)
        {
            int t = Environment.TickCount;
            dernierClaude = t;
            derniereActivite = t;
            posClaude = pt;
            posClaudeConnue = true;
            if (!modeClaude) EntrerModeClaude();
            switch (msg)
            {
                case 0x201: boutonsClaude |= 1; LancerOnde(pt, 0); break;
                case 0x202: boutonsClaude &= ~1; break;
                case 0x204: boutonsClaude |= 2; LancerOnde(pt, 1); break;
                case 0x205: boutonsClaude &= ~2; break;
                case 0x207: boutonsClaude |= 4; LancerOnde(pt, 2); break;
                case 0x208: boutonsClaude &= ~4; break;
                case 0x20A: LancerOnde(pt, ((short)(data >> 16)) > 0 ? 3 : 4); break;
            }
            if (msg != 0x200) Journal.Ecrire("Claude : " + NomMsg(msg) + " en " + pt);
            if (claude.Affiche) claude.Deplacer(pt.X - origineX, pt.Y - origineY);
            Programmer(DessinerClaude);
        }

        /// Premier geste de Claude : le vrai curseur passe à Claude.
        void EntrerModeClaude()
        {
            POINT c; Natif.GetCursorPos(out c);
            posUser = c; // l'événement de Claude n'est pas encore appliqué : c'est encore ta position
            modeClaude = true;
            sessionPendantCeMode = session;
            mouvementsGardes = 0;

            // 1. Ton curseur reste dessiné là où tu l'as laissé, avec sa forme du moment.
            CURSORINFO ci = new CURSORINFO();
            ci.cbSize = Marshal.SizeOf(typeof(CURSORINFO));
            ImageCurseur img = null;
            if (Natif.GetCursorInfo(ref ci) && (ci.flags & 1) != 0) img = Rendu.Decoder(ci.hCursor);
            if (img != null)
            {
                fantomeHx = img.Hx; fantomeHy = img.Hy;
                using (Bitmap b = Rendu.VersBitmap(Rendu.Fantome(img), img.L, img.H))
                    fantome.Montrer(b, posUser.X - fantomeHx, posUser.Y - fantomeHy, 255);
            }

            // 2. Le vrai curseur devient celui de Claude : orange.
            int poses = curseurs.MettreOrange();

            // 3. L'étiquette suivra la flèche orange (la flèche, c'est le vrai curseur).
            claudeRepos = false;
            cleClaude = null;
            Journal.Ecrire("Claude prend la souris ; ton curseur reste en " + posUser + (img == null ? " (il était caché)" : "")
                + " ; " + poses + "/" + Curseurs.Ids.Length + " curseurs passés en orange");
        }

        /// Un événement de TA souris pendant que le vrai curseur est à Claude. Renvoie vrai s'il faut le retenir.
        bool EvenementUtilisateur(int msg, POINT pt, uint data, long extra)
        {
            if (demoPoints != null)
            {
                // Tu bouges pendant la démonstration : elle s'arrête et tu reprends ta souris tout de suite.
                demoPoints = null;
                dernierClaude = Environment.TickCount - 100000;
                boutonsClaude = 0;
                Journal.Ecrire("démonstration arrêtée : tu as bougé ta souris");
            }
            int depuis = Environment.TickCount - dernierClaude;
            bool tenue = boutonsClaude != 0 && depuis < TENUE_MAX_MS;
            bool occupe = tenue || depuis < GRACE_MS;

            if ((extra & 0xFFFFFF00) == 0xFF515700) // stylet ou écran tactile : la position est absolue
            {
                if (occupe) return true;
                RendreSouris(pt, false);
                return false;
            }

            if (msg == 0x200) // mouvement
            {
                POINT cur; Natif.GetCursorPos(out cur);
                POINT nouv = Natif.Borner(new POINT(posUser.X + pt.X - cur.X, posUser.Y + pt.Y - cur.Y));
                if (occupe)
                {
                    // Claude est en plein geste : ta souris bouge seulement ton curseur dessiné.
                    posUser = nouv;
                    mouvementsGardes++;
                    if (fantome.Affiche) fantome.Deplacer(posUser.X - fantomeHx, posUser.Y - fantomeHy);
                    return true;
                }
                RendreSouris(nouv, true);
                return true;
            }

            // Clic ou molette
            if (tenue || depuis < GESTE_MS) return true; // on ne casse pas le clic ou le glisser de Claude
            RendreSouris(posUser, true);
            return Rejouer(msg, data);
        }

        /// Tu récupères ta souris en « cible » ; le curseur de Claude reste dessiné là où il était.
        void RendreSouris(POINT cible, bool deplacer)
        {
            if (!modeClaude) return;
            modeClaude = false;
            boutonsClaude = 0;
            claudeRepos = true;
            cleClaude = null;
            DessinerClaude();
            curseurs.RemettreNormaux();
            if (deplacer) Natif.SetCursorPos(cible.X, cible.Y);
            fantome.Cacher();
            posUser = cible;
            Journal.Ecrire("tu récupères ta souris en " + cible + (mouvementsGardes > 0 ? " (" + mouvementsGardes + " mouvements gardés le temps du geste de Claude)" : ""));
        }

        /// Ton clic était parti pendant que le vrai curseur était chez Claude : on le refait chez toi.
        bool Rejouer(int msg, uint data)
        {
            uint flags, md = 0;
            switch (msg)
            {
                case 0x201: flags = 0x0002; break;
                case 0x202: flags = 0x0004; break;
                case 0x204: flags = 0x0008; break;
                case 0x205: flags = 0x0010; break;
                case 0x207: flags = 0x0020; break;
                case 0x208: flags = 0x0040; break;
                case 0x20B: flags = 0x0080; md = data >> 16; break;
                case 0x20C: flags = 0x0100; md = data >> 16; break;
                case 0x20A: flags = 0x0800; md = unchecked((uint)(int)(short)(data >> 16)); break;
                case 0x20E: flags = 0x1000; md = unchecked((uint)(int)(short)(data >> 16)); break;
                default: return false;
            }
            INPUT[] ins = new INPUT[1];
            ins[0].type = 0;
            ins[0].mi.dwFlags = flags;
            ins[0].mi.mouseData = md;
            ins[0].mi.dwExtraInfo = new IntPtr(MARQUE_NOUS);
            Programmer(delegate { Natif.SendInput(1, ins, Marshal.SizeOf(typeof(INPUT))); });
            Journal.Ecrire("ton " + NomMsg(msg) + " est refait chez toi");
            return true;
        }

        static string NomMsg(int m)
        {
            switch (m)
            {
                case 0x200: return "mouvement";
                case 0x201: return "appui gauche";
                case 0x202: return "relâché gauche";
                case 0x204: return "appui droit";
                case 0x205: return "relâché droit";
                case 0x207: return "appui milieu";
                case 0x208: return "relâché milieu";
                case 0x20A: return "molette";
                case 0x20B: return "appui bouton X";
                case 0x20C: return "relâché bouton X";
                case 0x20E: return "molette horizontale";
                default: return "0x" + m.ToString("X");
            }
        }

        // ================= Dessin =================

        bool Voulu()
        {
            if (!posClaudeConnue || !actif) return false;
            if (modeClaude || session) return true;
            return Environment.TickCount - derniereActivite < reglages.DelaiRepos * 1000;
        }

        /// Dessine le curseur de Claude : flèche orange + étiquette au repos, étiquette seule sinon.
        void DessinerClaude()
        {
            if (!posClaudeConnue || !actif)
            {
                claude.Cacher(); alphaClaude = 0; alphaVoulu = 0;
                return;
            }
            bool frappe = Environment.TickCount - frappeJusqua < 0;
            frappeAffichee = frappe;
            alphaVoulu = Voulu() ? (byte)255 : (byte)0;
            if (alphaVoulu != alphaClaude && !anim.Enabled) anim.Start();
            if (alphaVoulu == 0 && alphaClaude == 0) { claude.Cacher(); return; }

            string texte = frappe ? reglages.Etiquette + " écrit…" : reglages.Etiquette;
            float e = Natif.Echelle(posClaude);
            RECT ecran = Natif.Ecran(posClaude);
            Size tp = Rendu.TailleEtiquette(texte, e);
            int pw = tp.Width + (int)Math.Ceiling(2 * e), ph = tp.Height + (int)Math.Ceiling(3 * e);
            bool gauche = posClaude.X + (int)(13 * e) + pw > ecran.Right;
            bool haut = posClaude.Y + (int)(20 * e) + ph > ecran.Bottom;
            ImageCurseur fl = curseurs.Fleche;
            bool fleche = claudeRepos && fl != null;
            string cle = texte + "|" + fleche + "|" + gauche + "|" + haut + "|" + e + "|" + reglages.Couleur + "|" + (fl == null ? 0 : fl.GetHashCode());
            if (cle == cleClaude && claude.Affiche)
            {
                claude.Deplacer(posClaude.X - origineX, posClaude.Y - origineY);
                return;
            }
            cleClaude = cle;

            int ax = fleche ? -fl.Hx : 0, ay = fleche ? -fl.Hy : 0, aw = fleche ? fl.L : 0, ah = fleche ? fl.H : 0;
            int lx = gauche ? -(int)(6 * e) - pw : (int)(13 * e);
            int ly = haut ? -(int)(6 * e) - ph : (int)(20 * e);
            int minX = Math.Min(0, lx), minY = Math.Min(0, ly), maxX = Math.Max(1, lx + pw), maxY = Math.Max(1, ly + ph);
            if (fleche)
            {
                minX = Math.Min(minX, ax); minY = Math.Min(minY, ay);
                maxX = Math.Max(maxX, ax + aw); maxY = Math.Max(maxY, ay + ah);
            }
            int largeur = maxX - minX + 2, hauteur = maxY - minY + 2;
            using (Bitmap b = new Bitmap(largeur, hauteur, PixelFormat.Format32bppPArgb))
            using (Graphics g = Graphics.FromImage(b))
            {
                if (fleche)
                {
                    g.InterpolationMode = InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    using (Bitmap f = Rendu.VersBitmap(fl.Px, aw, ah))
                        g.DrawImage(f, new Rectangle(ax - minX, ay - minY, aw, ah), 0, 0, aw, ah, GraphicsUnit.Pixel);
                    g.PixelOffsetMode = PixelOffsetMode.Default;
                }
                Rendu.DessinerEtiquette(g, lx - minX, ly - minY, texte, e, Rendu.Couleur(reglages.Couleur));
                origineX = -minX;
                origineY = -minY;
                claude.Montrer(b, posClaude.X - origineX, posClaude.Y - origineY, Math.Max((byte)1, alphaClaude));
            }
        }

        void LancerOnde(POINT pt, int genre)
        {
            Programmer(delegate { DemarrerOnde(pt, genre); });
        }

        void DemarrerOnde(POINT pt, int genre)
        {
            int idx = -1, plusVieille = 0;
            for (int i = 0; i < ondes.Length; i++)
            {
                if (!ondeActive[i]) { idx = i; break; }
                if (ondeDebut[i] - ondeDebut[plusVieille] < 0) plusVieille = i;
            }
            if (idx < 0) idx = plusVieille;
            ondeActive[idx] = true;
            ondeDebut[idx] = Environment.TickCount;
            ondeCentre[idx] = pt;
            ondeGenre[idx] = genre;
            ondeEchelle[idx] = Natif.Echelle(pt);
            if (!anim.Enabled) anim.Start();
        }

        void Animer(object s, EventArgs ev)
        {
            try
            {
                bool encore = false;
                int t = Environment.TickCount;
                Color c = Rendu.Couleur(reglages.Couleur);
                for (int i = 0; i < ondes.Length; i++)
                {
                    if (!ondeActive[i]) continue;
                    float f = (t - ondeDebut[i]) / (float)DUREE_ONDE;
                    if (f >= 1f) { ondeActive[i] = false; ondes[i].Cacher(); continue; }
                    using (Bitmap b = Rendu.Onde(Math.Max(0f, f), ondeEchelle[i], c, ondeGenre[i]))
                        ondes[i].Montrer(b, ondeCentre[i].X - b.Width / 2, ondeCentre[i].Y - b.Height / 2, 255);
                    encore = true;
                }

                if (alphaClaude != alphaVoulu)
                {
                    int a = alphaVoulu > alphaClaude ? Math.Min(alphaVoulu, alphaClaude + 45) : Math.Max(alphaVoulu, alphaClaude - 7);
                    alphaClaude = (byte)a;
                    if (alphaClaude == 0) claude.Cacher();
                    else if (claude.Affiche) claude.ChangerOpacite(alphaClaude);
                    else { cleClaude = null; DessinerClaude(); }
                    if (alphaClaude != alphaVoulu) encore = true;
                }

                if (demoPoints != null) { PasDemo(); encore = true; }
                if (!encore) anim.Stop();
            }
            catch (Exception ex) { Journal.Erreur("animation : " + ex); anim.Stop(); }
        }

        // ================= Entretien (deux fois par seconde) =================

        void Entretenir(object s, EventArgs ev)
        {
            try
            {
                tickEntretien++;
                int t = Environment.TickCount;
                if (frappeAffichee && t - frappeJusqua >= 0) DessinerClaude(); // « écrit… » s'efface

                byte voulu = Voulu() ? (byte)255 : (byte)0;
                if (voulu != alphaVoulu)
                {
                    alphaVoulu = voulu;
                    if (voulu == 255 && !claude.Affiche) { cleClaude = null; DessinerClaude(); }
                    if (!anim.Enabled) anim.Start();
                }

                VerifierCalques();

                long ms = curseurs.RechargerSiPrevu();
                if (ms >= 0) Journal.Ecrire("curseurs de Windows rechargés (" + ms + " ms)");

                if (tickEntretien % 4 == 0) SurveillerCrochets();

                if (tickEntretien % 10 == 0 && !modeClaude && !curseurs.RechargementPrevu)
                {
                    int n = curseurs.Rafraichir(false);
                    if (n > 0) { Journal.Ecrire(n + " curseur(s) orange refait(s) : les curseurs de Windows ont changé"); cleClaude = null; }
                }
            }
            catch (Exception ex) { Journal.Erreur("entretien : " + ex); }
        }

        void VerifierCalques()
        {
            if (fantome.Verifier()) Journal.Ecrire("ton curseur dessiné avait été réduit ou caché par un autre programme : remis");
            if (claude.Verifier()) Journal.Ecrire("le curseur de Claude avait été réduit ou caché par un autre programme : remis");
        }

        void SurveillerCrochets()
        {
            LASTINPUTINFO li = new LASTINPUTINFO();
            li.cbSize = Marshal.SizeOf(typeof(LASTINPUTINFO));
            if (!Natif.GetLastInputInfo(ref li)) return;
            int retard = unchecked((int)(li.dwTime - dernierAppel));
            if (retard > 1500 && Environment.TickCount - derniereReinstallation > 10000)
            {
                derniereReinstallation = Environment.TickCount;
                Journal.Ecrire("les crochets ne recevaient plus rien (" + retard + " ms) : je les repose");
                Accrocher();
            }
        }

        // ================= Démonstration =================

        void DemarrerDemo()
        {
            if (demoPoints != null || !actif) return;
            POINT c;
            if (modeClaude) c = posUser; else Natif.GetCursorPos(out c);
            RECT r = Natif.Ecran(c);
            float e = Natif.Echelle(c);
            Func<float, float, PointF> P = (dx, dy) => new PointF(
                Math.Max(r.Left + 60, Math.Min(r.Right - 200, c.X + dx * e)),
                Math.Max(r.Top + 60, Math.Min(r.Bottom - 90, c.Y + dy * e)));
            demoPoints = new PointF[] { P(30, 25), P(230, -120), P(430, -10), P(330, 160), P(140, 130), P(260, 50) };
            demoDebut = Environment.TickCount;
            demoDuree = 3400;
            demoClics = new float[] { 0.55f, 0.999f };
            demoClicIdx = 0;
            demoFrappe = false;
            Journal.Ecrire("démonstration");
            if (!anim.Enabled) anim.Start();
        }

        void PasDemo()
        {
            float f = (Environment.TickCount - demoDebut) / (float)demoDuree;
            if (f > 1f) f = 1f;
            float douce = f * f * (3 - 2 * f);
            PointF p = Courbe(demoPoints, douce);
            int x = (int)Math.Round(p.X), y = (int)Math.Round(p.Y);
            Natif.InjecterMouvement(x, y, MARQUE_DEMO);
            while (demoClicIdx < demoClics.Length && f >= demoClics[demoClicIdx])
            {
                DemarrerOnde(new POINT(x, y), 0);
                demoClicIdx++;
            }
            if (!demoFrappe && f >= 0.6f)
            {
                demoFrappe = true;
                frappeJusqua = Environment.TickCount + 1800;
                derniereActivite = Environment.TickCount;
                Programmer(DessinerClaude);
            }
            if (f >= 1f)
            {
                demoPoints = null;
                Journal.Ecrire("fin de la démonstration");
            }
        }

        /// Courbe douce qui passe par tous les points (Catmull-Rom).
        static PointF Courbe(PointF[] p, float f)
        {
            int n = p.Length - 1;
            float s = f * n;
            int i = Math.Min(n - 1, (int)s);
            float t = s - i, t2 = t * t, t3 = t2 * t;
            PointF p0 = p[Math.Max(0, i - 1)], p1 = p[i], p2 = p[i + 1], p3 = p[Math.Min(n, i + 2)];
            float x = 0.5f * (2 * p1.X + (-p0.X + p2.X) * t + (2 * p0.X - 5 * p1.X + 4 * p2.X - p3.X) * t2 + (-p0.X + 3 * p1.X - 3 * p2.X + p3.X) * t3);
            float y = 0.5f * (2 * p1.Y + (-p0.Y + p2.Y) * t + (2 * p0.Y - 5 * p1.Y + 4 * p2.Y - p3.Y) * t2 + (-p0.Y + 3 * p1.Y - 3 * p2.Y + p3.Y) * t3);
            return new PointF(x, y);
        }
    }
}
