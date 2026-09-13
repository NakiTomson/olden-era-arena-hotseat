using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace OldenEraMods
{
    public class ModFile { public string source; public string target; public string sha256; }
    public class Mod
    {
        public string id; public string name; public string version; public string description;
        public string gameAssemblySha256; public string validation; public bool requiresBepInEx;
        public List<ModFile> files = new List<ModFile>();
        [ScriptIgnore] public string folder;
        public override string ToString() { return name + "   ·   " + version; }
    }
    public class Entry { public string target; public string hash; public string backup; public string backupHash; }
    public class State
    {
        public string gamePath;
        public List<string> enabled = new List<string>();
        public List<Entry> entries = new List<Entry>();
    }
    public class Settings { public string gamePath = @"D:\SteamLibrary\steamapps\common\Heroes of Might and Magic Olden Era"; }
    public class Snapshot { public string target; public string file; public string beforeHash; public string afterHash; }
    public class Journal { public string gamePath; public State previous; public List<Snapshot> snapshots = new List<Snapshot>(); }
    public class Planned { public string target; public string source; public string hash; }

    public static class Disk
    {
        static JavaScriptSerializer Serializer() { return new JavaScriptSerializer { MaxJsonLength = 16000000 }; }
        public static T Read<T>(string path) { return Serializer().Deserialize<T>(File.ReadAllText(path, Encoding.UTF8)); }
        public static string Json(object data) { return Serializer().Serialize(data); }
        public static string Hash(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
        public static string TextHash(string text)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant();
        }
        public static void AtomicText(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temp = Path.Combine(Path.GetDirectoryName(path), "." + Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllText(temp, text, new UTF8Encoding(false));
            Commit(temp, path);
        }
        public static void AtomicCopy(string source, string target)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            var temp = Path.Combine(Path.GetDirectoryName(target), "." + Guid.NewGuid().ToString("N") + ".tmp");
            File.Copy(source, temp);
            Commit(temp, target);
        }
        static void Commit(string temp, string target)
        {
            try { if (File.Exists(target)) File.Replace(temp, target, null); else File.Move(temp, target); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        public static string SafePath(string root, string relative)
        {
            if (String.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.IndexOf(':') >= 0)
                throw new IOException("Недопустимый путь в пакете: " + relative);
            var parts = relative.Replace('/', '\\').Split('\\');
            if (parts.Any(p => p == ".." || p == "." || p.Length == 0 || p.EndsWith(".") || p.EndsWith(" ")))
                throw new IOException("Недопустимый путь: " + relative);
            string canonical = Path.GetFullPath(root).TrimEnd('\\') + "\\";
            var path = Path.GetFullPath(Path.Combine(canonical, relative));
            if (!path.StartsWith(canonical, StringComparison.OrdinalIgnoreCase)) throw new IOException("Путь вне папки: " + relative);
            CheckReparse(canonical);
            string current = canonical;
            foreach (string part in parts) { current = Path.Combine(current, part); CheckReparse(current); }
            return path;
        }
        public static void CheckReparse(string path)
        {
            if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Символические ссылки и junction не поддерживаются: " + path);
        }
    }

    public class Manager
    {
        public readonly string Home;
        readonly bool fixture;
        string StatePath { get { return Path.Combine(Home, "state.json"); } }
        string JournalPath { get { return Path.Combine(Home, "pending-transaction.json"); } }
        public Manager(string home, bool testFixture = false) { Home = Path.GetFullPath(home).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar); fixture = testFixture; }
        public State GetState() { return File.Exists(StatePath) ? Disk.Read<State>(StatePath) : new State(); }
        public List<Mod> Discover()
        {
            var mods = new List<Mod>();
            foreach (var folder in Directory.GetDirectories(Directory.GetParent(Home).FullName))
            {
                var path = Path.Combine(folder, "mod.json");
                if (!File.Exists(path)) continue;
                var mod = Disk.Read<Mod>(path);
                if (mod == null || String.IsNullOrWhiteSpace(mod.id) || mod.files == null || mod.files.Count == 0)
                    throw new IOException("Неполный пакет мода: " + path);
                if (mods.Any(m => String.Equals(m.id, mod.id, StringComparison.OrdinalIgnoreCase)))
                    throw new IOException("Повторяющийся id мода: " + mod.id);
                mod.folder = folder; mods.Add(mod);
            }
            return mods.OrderBy(m => m.name).ToList();
        }
        public static void EnsureStopped()
        {
            if (Process.GetProcessesByName("HeroesOldenEra").Length != 0)
                throw new IOException("Закройте Olden Era перед изменением модов.");
        }
        public string CheckGame(string root)
        {
            if (!File.Exists(Path.Combine(root, "HeroesOldenEra.exe")) || !File.Exists(Path.Combine(root, "GameAssembly.dll")))
                throw new IOException("Выберите папку, содержащую HeroesOldenEra.exe и GameAssembly.dll.");
            Disk.CheckReparse(root);
            return Disk.Hash(Disk.SafePath(root, "GameAssembly.dll"));
        }
        List<Planned> BuildPlan(string game, List<Mod> mods, string assemblyHash)
        {
            var plan = new Dictionary<string, Planned>(StringComparer.OrdinalIgnoreCase);
            Action<string, IEnumerable<ModFile>> add = (folder, files) => {
                foreach (var f in files)
                {
                    string source = Disk.SafePath(folder, f.source);
                    Disk.SafePath(game, f.target);
                    if (String.IsNullOrEmpty(f.sha256) || f.sha256.Length != 64 || !File.Exists(source) || !String.Equals(Disk.Hash(source), f.sha256, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Файл пакета повреждён: " + source);
                    string key = f.target.Replace('/', '\\');
                    Planned previous;
                    if (plan.TryGetValue(key, out previous) && !String.Equals(previous.hash, f.sha256, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Моды изменяют один файл по-разному: " + key);
                    plan[key] = new Planned { target = key, source = source, hash = f.sha256.ToLowerInvariant() };
                }
            };
            foreach (var mod in mods)
            {
                if (!String.IsNullOrEmpty(mod.gameAssemblySha256) && !String.Equals(assemblyHash, mod.gameAssemblySha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Мод «" + mod.name + "» не поддерживает установленную сборку игры. Нужна обновлённая версия мода.");
                add(mod.folder, mod.files);
            }
            if (mods.Any(m => m.requiresBepInEx))
            {
                var runtime = Disk.Read<Mod>(Path.Combine(Home, "runtime", "runtime.json"));
                add(Path.Combine(Home, "runtime"), runtime.files);
            }
            return plan.Values.OrderBy(p => p.target).ToList();
        }
        void ValidateTracked(State state)
        {
            foreach (var e in state.entries)
            {
                var target = Disk.SafePath(state.gamePath, e.target);
                if (!File.Exists(target) || Disk.Hash(target) != e.hash)
                    throw new IOException("Файл изменён вне лаунчера: " + e.target + ". Операция отменена; резервные копии сохранены.");
                if (e.backup != null)
                {
                    var backup = Disk.SafePath(Home, e.backup);
                    if (!File.Exists(backup) || Disk.Hash(backup) != e.backupHash) throw new IOException("Повреждена резервная копия: " + e.target);
                }
            }
        }
        public void Apply(string gameRoot, IEnumerable<string> selected)
        {
            string game = Path.GetFullPath(gameRoot).TrimEnd('\\');
            using (var mutex = new Mutex(false, "Local\\OldenEraMods-" + Disk.TextHash(game.ToLowerInvariant())))
            {
                bool acquired;
                try { acquired = mutex.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) throw new IOException("Другой лаунчер уже меняет моды этой игры.");
                try
                {
                    if (!fixture) EnsureStopped();
                    RecoverInternal();
                    string gameHash = CheckGame(game);
                    State old = GetState();
                    if (old.entries.Count != 0 && !String.Equals(old.gamePath, game, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Сначала отключите моды в предыдущей папке игры: " + old.gamePath);
                    ValidateTracked(old);
                    var ids = new HashSet<string>(selected, StringComparer.OrdinalIgnoreCase);
                    var mods = Discover().Where(m => ids.Contains(m.id)).ToList();
                    if (mods.Count != ids.Count) throw new IOException("Не найден один из выбранных модов.");
                    var plan = BuildPlan(game, mods, gameHash);
                    var before = old.entries.ToDictionary(e => e.target, StringComparer.OrdinalIgnoreCase);
                    var desired = plan.ToDictionary(e => e.target, StringComparer.OrdinalIgnoreCase);
                    var next = new State { gamePath = game, enabled = mods.Select(m => m.id).ToList() };
                    foreach (var file in plan)
                    {
                        Entry existing;
                        var entry = new Entry { target = file.target, hash = file.hash };
                        if (before.TryGetValue(file.target, out existing)) { entry.backup = existing.backup; entry.backupHash = existing.backupHash; }
                        else
                        {
                            string target = Disk.SafePath(game, file.target);
                            if (File.Exists(target))
                            {
                                // Refuse to take ownership of a foreign BepInEx installation.
                                if (file.target.StartsWith("BepInEx\\", StringComparison.OrdinalIgnoreCase) || file.target == "winhttp.dll" || file.target == "doorstop_config.ini")
                                    throw new IOException("Обнаружен сторонний загрузчик: " + file.target + ". Используйте чистую копию игры.");
                                entry.backupHash = Disk.Hash(target);
                                entry.backup = "backups\\" + Disk.TextHash(game.ToLowerInvariant()).Substring(0,16) + "\\" + entry.backupHash + ".bak";
                                string backup = Disk.SafePath(Home, entry.backup);
                                if (!File.Exists(backup)) Disk.AtomicCopy(target, backup);
                                if (Disk.Hash(backup) != entry.backupHash) throw new IOException("Ошибка резервного копирования.");
                            }
                        }
                        next.entries.Add(entry);
                    }
                    string transaction = "transactions\\" + Guid.NewGuid().ToString("N");
                    var journal = new Journal { gamePath = game, previous = old };
                    foreach (var key in before.Keys.Union(desired.Keys, StringComparer.OrdinalIgnoreCase))
                    {
                        string target = Disk.SafePath(game, key);
                        string oldHash = File.Exists(target) ? Disk.Hash(target) : null;
                        Planned install; Entry removed;
                        string newHash = desired.TryGetValue(key, out install) ? install.hash : before.TryGetValue(key, out removed) ? removed.backupHash : null;
                        if (oldHash == newHash) continue;
                        var snapshot = new Snapshot { target = key, beforeHash = oldHash, afterHash = newHash };
                        if (oldHash != null)
                        {
                            snapshot.file = transaction + "\\" + Disk.TextHash(key.ToLowerInvariant()) + ".bak";
                            Disk.AtomicCopy(target, Disk.SafePath(Home, snapshot.file));
                            if (Disk.Hash(Disk.SafePath(Home, snapshot.file)) != oldHash) throw new IOException("Ошибка снимка файла.");
                        }
                        journal.snapshots.Add(snapshot);
                    }
                    Disk.AtomicText(JournalPath, Disk.Json(journal));
                    try
                    {
                        if (!fixture) EnsureStopped();
                        foreach (var snapshot in journal.snapshots)
                        {
                            var target = Disk.SafePath(game, snapshot.target);
                            if ((File.Exists(target) ? Disk.Hash(target) : null) != snapshot.beforeHash) throw new IOException("Файл изменился во время операции: " + snapshot.target);
                            Planned install;
                            if (desired.TryGetValue(snapshot.target, out install)) Disk.AtomicCopy(install.source, target);
                            else
                            {
                                var previous = before[snapshot.target];
                                if (previous.backup != null) Disk.AtomicCopy(Disk.SafePath(Home, previous.backup), target);
                                else if (File.Exists(target)) File.Delete(target);
                            }
                            if ((File.Exists(target) ? Disk.Hash(target) : null) != snapshot.afterHash) throw new IOException("Ошибка проверки записанного файла: " + snapshot.target);
                        }
                        Disk.AtomicText(StatePath, Disk.Json(next));
                        File.Delete(JournalPath);
                        Log("APPLY " + String.Join(",", next.enabled));
                        CleanSnapshots(journal);
                    }
                    catch { RecoverInternal(); throw; }
                }
                finally { mutex.ReleaseMutex(); }
            }
        }
        void CleanSnapshots(Journal journal)
        {
            // Only explicitly recorded temporary files; no recursive directory removal.
            foreach (var s in journal.snapshots)
                if (s.file != null) { try { File.Delete(Disk.SafePath(Home, s.file)); } catch (IOException) { } }
        }
        void RecoverInternal()
        {
            if (!File.Exists(JournalPath)) return;
            if (!fixture) EnsureStopped();
            var journal = Disk.Read<Journal>(JournalPath);
            foreach (var s in journal.snapshots)
            {
                string target = Disk.SafePath(journal.gamePath, s.target);
                string hash = File.Exists(target) ? Disk.Hash(target) : null;
                if (hash != s.beforeHash && hash != s.afterHash) throw new IOException("Восстановление требует проверки изменённого файла: " + s.target);
                if (s.beforeHash != null && (s.file == null || Disk.Hash(Disk.SafePath(Home, s.file)) != s.beforeHash)) throw new IOException("Повреждён снимок: " + s.target);
            }
            foreach (var s in journal.snapshots)
            {
                string target = Disk.SafePath(journal.gamePath, s.target);
                if (s.beforeHash == null) { if (File.Exists(target)) File.Delete(target); }
                else Disk.AtomicCopy(Disk.SafePath(Home, s.file), target);
            }
            Disk.AtomicText(StatePath, Disk.Json(journal.previous));
            File.Delete(JournalPath);
            Log("RECOVERED interrupted transaction");
            CleanSnapshots(journal);
        }
        public bool HasPendingTransaction { get { return File.Exists(JournalPath); } }
        public void Log(string text) { File.AppendAllText(Path.Combine(Home, "launcher.log"), DateTime.Now.ToString("s") + " " + text + Environment.NewLine, Encoding.UTF8); }
        public void VerifyForLaunch(string game)
        {
            if (HasPendingTransaction) throw new IOException("Незавершённая операция: нажмите «Применить» для восстановления.");
            var state = GetState();
            string hash = CheckGame(game);
            if (state.entries.Count > 0 && !String.Equals(Path.GetFullPath(game).TrimEnd('\\'), state.gamePath, StringComparison.OrdinalIgnoreCase)) throw new IOException("Папка игры не совпадает с установленными модами.");
            ValidateTracked(state);
            BuildPlan(game, Discover().Where(m => state.enabled.Contains(m.id)).ToList(), hash);
        }
    }

    public class MainWindow : Form
    {
        readonly Manager manager;
        readonly TextBox game = new TextBox();
        readonly CheckedListBox list = new CheckedListBox();
        readonly TextBox details = new TextBox();
        readonly Label status = new Label();
        readonly Button apply, play, vanilla;
        readonly Color background = Color.FromArgb(18, 25, 33), panel = Color.FromArgb(29, 39, 49);
        List<Mod> mods = new List<Mod>();
        bool busy;
        public MainWindow(Manager manager)
        {
            this.manager = manager;
            Text = "Olden Era · Мои моды"; ClientSize = new Size(1050, 690); MinimumSize = new Size(900, 600);
            StartPosition = FormStartPosition.CenterScreen; BackColor = background; ForeColor = Color.FromArgb(230, 236, 242);
            Font = new Font("Segoe UI", 10); AutoScaleMode = AutoScaleMode.Dpi;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(28), ColumnCount = 1, RowCount = 5, BackColor = background };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 80));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));
            Controls.Add(layout);
            var header = new Panel { Dock = DockStyle.Fill };
            header.Controls.Add(new Label { Text = "OLDEN ERA", ForeColor = Color.FromArgb(218, 184, 117), Font = new Font("Segoe UI", 26, FontStyle.Bold), AutoSize = true, Location = new Point(0, 0) });
            header.Controls.Add(new Label { Text = "Мои моды  /  Ваша игра, ваши правила", AutoSize = true, ForeColor = Color.FromArgb(153, 174, 191), Location = new Point(2, 60) });
            layout.Controls.Add(header, 0, 0);
            var pathRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
            pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 126));
            pathRow.Controls.Add(new Label { Text = "Папка игры", AutoSize = true }, 0, 0);
            game.Dock = DockStyle.Fill; game.BackColor = panel; game.ForeColor = ForeColor; game.BorderStyle = BorderStyle.FixedSingle;
            var settingsPath = Path.Combine(manager.Home, "settings.json");
            game.Text = File.Exists(settingsPath) ? Disk.Read<Settings>(settingsPath).gamePath : new Settings().gamePath;
            pathRow.Controls.Add(game, 0, 1);
            var browse = Button("Выбрать…", false);
            browse.Dock = DockStyle.Fill;
            browse.Click += (s,e) => { using (var dialog = new FolderBrowserDialog { SelectedPath = game.Text, Description = "Папка с HeroesOldenEra.exe" }) if (dialog.ShowDialog() == DialogResult.OK) game.Text = dialog.SelectedPath; };
            pathRow.Controls.Add(browse, 1, 1); layout.Controls.Add(pathRow, 0, 1);
            var content = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
            content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45)); content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 32)); content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            content.Controls.Add(new Label { Text = "БИБЛИОТЕКА МОДОВ", ForeColor = Color.FromArgb(153,174,191), AutoSize = true }, 0, 0);
            content.Controls.Add(new Label { Text = "О ВЫБРАННОМ МОДЕ", ForeColor = Color.FromArgb(153,174,191), AutoSize = true }, 1, 0);
            list.Dock = DockStyle.Fill; list.CheckOnClick = true; list.BackColor = panel; list.ForeColor = ForeColor; list.BorderStyle = BorderStyle.None; list.IntegralHeight = false; list.Font = new Font("Segoe UI", 11);
            details.Dock = DockStyle.Fill; details.Multiline = true; details.ReadOnly = true; details.ScrollBars = ScrollBars.Vertical; details.BackColor = panel; details.ForeColor = ForeColor; details.BorderStyle = BorderStyle.None; details.Margin = new Padding(14,0,0,0);
            content.Controls.Add(list, 0, 1); content.Controls.Add(details, 1, 1); layout.Controls.Add(content, 0, 2);
            list.SelectedIndexChanged += (s,e) => ShowDetails();
            status.Dock = DockStyle.Fill; status.AutoEllipsis = true; status.TextAlign = ContentAlignment.MiddleLeft; status.ForeColor = Color.FromArgb(163, 203, 184); layout.Controls.Add(status,0,3);
            var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, RowCount = 1 };
            for (int i=0;i<5;i++) actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,20));
            apply = Button("Применить", true); play = Button("Играть", true); vanilla = Button("Отключить все", false);
            var refresh = Button("Обновить", false); var folderButton = Button("Папка модов", false);
            foreach (var b in new[] { apply, play, vanilla, refresh, folderButton }) { b.Dock = DockStyle.Fill; b.Margin = new Padding(0, 8, 10, 6); actions.Controls.Add(b); }
            apply.Click += async (s,e) => await ApplySelection(false);
            vanilla.Click += async (s,e) => { for(int i=0;i<list.Items.Count;i++) list.SetItemChecked(i,false); await ApplySelection(false); };
            play.Click += async (s,e) => await ApplySelection(true);
            refresh.Click += (s,e) => Reload();
            folderButton.Click += (s,e) => Process.Start("explorer.exe", '"' + Directory.GetParent(manager.Home).FullName + '"');
            layout.Controls.Add(actions,0,4);
            FormClosing += (s,e) => { if (busy) e.Cancel = true; };
            Shown += (s,e) => Reload();
        }
        Button Button(string text, bool accent)
        {
            var b = new Button { Text = text, FlatStyle = FlatStyle.Flat, AutoSize = false, Height = 32, Dock = DockStyle.None, BackColor = accent ? Color.FromArgb(56, 95, 88) : panel, ForeColor = ForeColor, Cursor = Cursors.Hand };
            b.FlatAppearance.BorderColor = Color.FromArgb(71, 93, 105); return b;
        }
        public void Reload()
        {
            try
            {
                mods = manager.Discover(); var state = manager.GetState(); list.Items.Clear();
                foreach (var mod in mods) list.Items.Add(mod, state.enabled.Contains(mod.id));
                if (list.Items.Count > 0) list.SelectedIndex = 0;
                list.Focus();
                status.Text = manager.HasPendingTransaction ? "Нужно восстановить незавершённую операцию. Нажмите «Применить»." : "Включено модов: " + state.enabled.Count + ". Изменения применяются при нажатии «Применить» или «Играть».";
            }
            catch (Exception e) { Error(e); }
        }
        void ShowDetails()
        {
            var mod = list.SelectedItem as Mod;
            details.Text = mod == null ? "Положите пакет мода в отдельную папку рядом с Launcher." : mod.name + "\r\nВерсия " + mod.version + "\r\n\r\n" + mod.description + "\r\n\r\nПроверка: " + mod.validation;
        }
        async Task ApplySelection(bool start)
        {
            if (busy) return;
            var selected = list.CheckedItems.Cast<Mod>().Select(m => m.id).ToArray(); string path = game.Text.Trim();
            busy = true; Enabled = false; status.Text = "Проверяю файлы и применяю моды…"; UseWaitCursor = true;
            try
            {
                await Task.Run(() => manager.Apply(path, selected));
                Disk.AtomicText(Path.Combine(manager.Home, "settings.json"), Disk.Json(new Settings { gamePath = path }));
                status.Text = "Готово. Включено модов: " + selected.Length + ".";
                if (start)
                {
                    manager.VerifyForLaunch(path);
                    Process.Start(new ProcessStartInfo { FileName = Path.Combine(path, "HeroesOldenEra.exe"), WorkingDirectory = path, UseShellExecute = true });
                    status.Text = "Игра запускается. Первый запуск с загрузчиком может занять несколько минут.";
                }
            }
            catch (Exception e) { Error(e); }
            finally { busy = false; Enabled = true; UseWaitCursor = false; }
        }
        void Error(Exception e) { status.Text = e.Message; manager.Log("ERROR " + e); MessageBox.Show(this, e.Message, "Моды не изменены / требуется проверка", MessageBoxButtons.OK, MessageBoxIcon.Information); }
    }

    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            var home = AppDomain.CurrentDomain.BaseDirectory;
            try
            {
                var manager = new Manager(home);
                if (args.Length > 0)
                {
                    if (args[0] == "--status") { File.WriteAllText(Path.Combine(home,"status-output.json"), Disk.Json(manager.GetState())); return 0; }
                    if (args[0] == "--apply") { manager.Apply(args[1], args.Skip(2)); return 0; }
                    if (args[0] == "--check") { manager.VerifyForLaunch(args[1]); return 0; }
                    if (args[0] == "--smoke") { Smoke.Run(args[1]); return 0; }
                    if (args[0] == "--preview")
                    {
                        Application.EnableVisualStyles();
                        using (var form = new MainWindow(manager))
                        { form.StartPosition=FormStartPosition.Manual; form.Location=new Point(-32000,-32000); form.ShowInTaskbar=false; form.Show(); form.Reload(); Application.DoEvents(); using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(args[1]); } form.Close(); }
                        return 0;
                    }
                    throw new ArgumentException("Unknown argument");
                }
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false); Application.Run(new MainWindow(manager)); return 0;
            }
            catch (Exception e) { File.WriteAllText(Path.Combine(home, "last-error.txt"), e.ToString()); return 1; }
        }
    }
}
