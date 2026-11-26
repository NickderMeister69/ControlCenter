using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace NickControlCenter;

public partial class SharePage : UserControl, IPage
{
    private bool _visible;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private long _lastSent = -1, _lastRecv = -1;
    private DateTime _lastTick;
    private string _linksKey = "";
    private string _filesKey = "";

    public sealed record LinkRow(string Kind, string Url);

    // statisch: laufende Übertragungen bleiben sichtbar, auch wenn die Seite neu geladen wird
    private static readonly ObservableCollection<Transfer> _transfers = new();
    private static readonly ObservableCollection<Transfer> _hostTransfers = new();
    private List<RemoteFile> _friendFiles = new();
    private readonly DispatcherTimer _friendTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private bool _friendBusy;
    private string _reqKey = "", _memKey = "";
    private List<string> _hostCats = new() { "Sonstiges" }, _friendCats = new() { "Sonstiges" };
    private ShareClient.Found? _found;
    private CancellationTokenSource? _joinCts;

    public SharePage()
    {
        InitializeComponent();
        FillSettings();
        ShareServer.Changed += () => Dispatcher.BeginInvoke(() => { if (_visible) Refresh(); });
        ShareServer.Activity += line => Dispatcher.BeginInvoke(() =>
        {
            bool bottom = ActivityBox.VerticalOffset + ActivityBox.ViewportHeight >= ActivityBox.ExtentHeight - 20;
            ActivityBox.AppendText((ActivityBox.Text.Length > 0 ? "\n" : "") + line);
            if (bottom) ActivityBox.ScrollToEnd();
        });
        ActivityBox.Text = string.Join("\n", ShareServer.Recent);
        _timer.Tick += (_, _) => Tick();

        FTransfers.ItemsSource = _transfers;
        if (_transfers.Count > 0) FTransfersCard.Visibility = Visibility.Visible;
        HTransfers.ItemsSource = _hostTransfers;
        HFolder.Text = ShareServer.Settings.DownloadFolder;
        FMyName.Text = ShareClient.MyName;
        FFolder.Text = ShareServer.Settings.DownloadFolder;
        _friendTimer.Tick += (_, _) => { if (_visible && FriendView.Visibility == Visibility.Visible && !_friendBusy) _ = FriendRefresh(quiet: true); };
        if (ShareServer.Settings.Tab == "friend") TabFriend.IsChecked = true; else TabHost.IsChecked = true;
    }

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (HostView == null || sender is not RadioButton { Tag: string t }) return;
        HostView.Visibility = t == "host" ? Visibility.Visible : Visibility.Collapsed;
        FriendView.Visibility = t == "friend" ? Visibility.Visible : Visibility.Collapsed;
        if (ShareServer.Settings.Tab != t) { ShareServer.Settings.Tab = t; ShareServer.Save(); }
        if (t == "friend" && _visible) _ = FriendRefresh();
    }

    public void OnVisibleChanged(bool visible)
    {
        _visible = visible;
        if (visible)
        {
            Refresh(); _timer.Start(); _friendTimer.Start();
            if (FriendView.Visibility == Visibility.Visible) _ = FriendRefresh();
        }
        else { _timer.Stop(); _friendTimer.Stop(); }
    }

    private void FillSettings()
    {
        var s = ShareServer.Settings;
        TitleBox.Text = s.Title;
        PwBox.Password = PwPlain.Text = s.Password ?? "";
        AdminBox.Password = AdminPlain.Text = s.AdminPassword ?? "";
        FolderBox.Text = s.StoragePath;
        PortBox.Text = s.Port.ToString();
        MaxBox.Text = s.MaxUploadGB.ToString();
        InternetBox.IsChecked = s.Internet;
    }

    private void Refresh()
    {
        var s = ShareServer.Settings;
        bool on = ShareServer.Running;
        Toggle.IsChecked = s.Enabled;
        StateText.Text = on ? "An – deine Freunde können sich mit ihrem Control Center verbinden" :
                         s.Enabled ? "Startet nicht – siehe unten" :
                         ShareServer.Problems(s).Count > 0 ? "Aus – leg zuerst unten die beiden Passwörter fest" : "Aus";
        var err = s.Enabled && !on ? ShareServer.LastError : null;
        ErrorBox.Visibility = string.IsNullOrEmpty(err) ? Visibility.Collapsed : Visibility.Visible;
        ErrorText.Text = err ?? "";

        BrowserToggle.IsChecked = s.ShowBrowserLinks;
        BrowserCard.Visibility = s.ShowBrowserLinks ? Visibility.Visible : Visibility.Collapsed;
        TunnelText.Text = !on ? "Schalte die Freigabe oben ein, dann erscheint hier der Code." :
                          !s.Internet ? "Nur Heimnetz und Tailscale (Internet ist in den Einstellungen aus)." :
                          ShareServer.PublicUrl != null ? "✔ Übers Internet erreichbar" :
                          ShareServer.TunnelState.Length > 0 ? ShareServer.TunnelState : "Starte Internet-Tunnel …";

        InviteBox.Text = on ? ShareServer.InviteCode() : "";
        InviteHint.Text = !on ? "Erscheint, sobald die Freigabe läuft." :
                          ShareServer.PublicUrl != null && ShareServer.CanPublish ? "✔ Der Code bleibt gültig, auch wenn sich der Internet-Link ändert – deine App veröffentlicht die aktuelle Adresse über dein GitHub-Repo, die Apps deiner Freunde finden dich automatisch." :
                          ShareServer.PublicUrl != null ? "Der Internet-Link ändert sich bei jedem Start der Freigabe – dann den neuen Code schicken." :
                          s.Internet ? "Warte auf den Internet-Link … (bis dahin enthält der Code die Tailscale-/Heimnetz-Adresse)" :
                          "Der Code enthält die Tailscale-/Heimnetz-Adresse (Internet-Tunnel ist aus).";

        var reqs = ShareServer.PendingRequests();
        RequestsCard.Visibility = reqs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var reqKey = string.Join("|", reqs.Select(r => r.Id));
        if (reqKey != _reqKey) { _reqKey = reqKey; RequestList.ItemsSource = reqs; }
        var members = ShareServer.Settings.Members.OrderBy(m => m.Name).ToList();
        var memKey = string.Join("|", members.Select(m => m.Id + m.LastSeen.Ticks));
        if (memKey != _memKey) { _memKey = memKey; MemberList.ItemsSource = members; }
        NoMembers.Visibility = members.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (!OwnerBox.IsKeyboardFocusWithin) OwnerBox.Text = ShareServer.OwnerName;

        var links = on ? ShareServer.Links().Select(l => new LinkRow(l.Kind, l.Url)).ToList() : new List<LinkRow>();
        var key = string.Join("|", links.Select(l => l.Url));
        if (key != _linksKey) { _linksKey = key; LinkList.ItemsSource = links; }
        Tick();
        RefreshFiles();
        RefreshSpeed();
    }

    private void RefreshFiles()
    {
        var files = ShareServer.Files();
        var key = string.Join("|", files.Select(f => f.Id + f.Downloads));
        if (key == _filesKey) return;
        _filesKey = key;
        FileList.ItemsSource = files;
        HFilesTitle.Text = $"DEINE GETEILTEN DATEIEN ({files.Count})";
        var cats = files.Select(f => f.Category).Append("Sonstiges").Distinct().OrderBy(c => c).ToList();
        _hostCats = cats;
        NoFiles.Visibility = files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Tick()
    {
        var st = ShareServer.GetStats();
        SFiles.Text = st.Files.ToString();
        SUsed.Text = ShareServer.Fmt(st.Used);
        SFree.Text = st.Total > 0 ? ShareServer.Fmt(st.Free) : "–";
        SActive.Text = (ShareServer.ActiveDownloads + ShareServer.ActiveUploads).ToString();

        var now = DateTime.Now;
        long sent = ShareServer.BytesSent, recv = ShareServer.BytesReceived;
        if (_lastSent >= 0)
        {
            var sec = Math.Max(0.2, (now - _lastTick).TotalSeconds);
            SUp.Text = Rate((sent - _lastSent) / sec);
            SDown.Text = Rate((recv - _lastRecv) / sec);
        }
        _lastSent = sent; _lastRecv = recv; _lastTick = now;
        if (_visible && now.Second % 5 == 0) RefreshFiles();
    }

    private static string Rate(double bps) => bps < 1024 ? "0 MB/s" : ShareServer.Fmt((long)bps) + "/s";

    private void RefreshSpeed()
    {
        var last = SpeedTest.History.Results.OrderByDescending(r => r.Time).FirstOrDefault();
        if (last == null || last.UpMbit <= 0)
        {
            SpeedHead.Text = "Noch kein Speedtest – mach einen, dann steht hier, wie lange deine Freunde brauchen.";
            SpeedTable.Text = "";
            return;
        }
        double mbs = last.UpMbit * 0.9 / 8; // ~10 % gehen in der Praxis verloren
        SpeedHead.Text = $"Dein Upload: {last.UpMbit:0} Mbit/s (Speedtest vom {last.Time:dd.MM.}) → ca. {mbs:0.0} MB/s";
        string T(double gb)
        {
            var sec = gb * 1024 / mbs;
            return sec < 60 ? $"{sec:0} s" : sec < 3600 ? $"{sec / 60:0} min" : $"{sec / 3600:0.0} h".Replace('.', ',');
        }
        SpeedTable.Text = $"   1 GB  ≈ {T(1)}\n  10 GB  ≈ {T(10)}\n  40 GB  ≈ {T(40)}\n 100 GB  ≈ {T(100)}";
    }

    // ---------- Aktionen ----------
    private void Toggle_Click(object sender, RoutedEventArgs e)
    {
        var s = ShareServer.Settings;
        s.Enabled = Toggle.IsChecked == true;
        if (s.Enabled)
        {
            var problems = ShareServer.Problems(s);
            if (problems.Count > 0)
            {
                s.Enabled = false; Toggle.IsChecked = false;
                Ui.Toast(string.Join(" · ", problems), true);
                PwBox.Focus();
                return;
            }
            ShareServer.Save();
            if (!ShareServer.Start()) Ui.Toast("Freigabe konnte nicht starten: " + ShareServer.LastError, true);
        }
        else { ShareServer.Save(); ShareServer.Stop(); }
        Refresh();
    }

    private void ShowPw_Click(object sender, RoutedEventArgs e)
    {
        bool show = ShowPw.IsChecked == true;
        if (show) { PwPlain.Text = PwBox.Password; AdminPlain.Text = AdminBox.Password; }
        else { PwBox.Password = PwPlain.Text; AdminBox.Password = AdminPlain.Text; }
        PwPlain.Visibility = AdminPlain.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        PwBox.Visibility = AdminBox.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Folder_Click(object sender, RoutedEventArgs e)
    {
        var d = new Microsoft.Win32.OpenFolderDialog { Title = "Speicherordner für die Dateifreigabe", InitialDirectory = Directory.Exists(FolderBox.Text) ? FolderBox.Text : null };
        if (d.ShowDialog() == true) { FolderBox.Text = d.FolderName; SaveMsg.Text = "Vorhandene Dateien werden nicht verschoben. Zum Übernehmen „Speichern“ klicken."; }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        bool show = ShowPw.IsChecked == true;
        var cur = ShareServer.Settings;
        var next = new ShareSettings
        {
            Enabled = cur.Enabled,
            Title = string.IsNullOrWhiteSpace(TitleBox.Text) ? "Download Center" : TitleBox.Text.Trim(),
            Password = show ? PwPlain.Text : PwBox.Password,
            AdminPassword = show ? AdminPlain.Text : AdminBox.Password,
            StoragePath = FolderBox.Text,
            Port = int.TryParse(PortBox.Text, out var p) ? p : 0,
            MaxUploadGB = int.TryParse(MaxBox.Text, out var g) ? Math.Clamp(g, 1, 2000) : 100,
            Internet = InternetBox.IsChecked == true,
            SessionSecret = cur.SessionSecret
        };
        var problems = ShareServer.Problems(next);
        if (problems.Count > 0) { SaveMsg.Text = string.Join(" · ", problems); return; }
        try { Directory.CreateDirectory(next.StoragePath); }
        catch (Exception ex) { SaveMsg.Text = "Speicherordner geht nicht: " + ex.Message; return; }

        bool pwChanged = next.Password != cur.Password || next.AdminPassword != cur.AdminPassword;
        bool firstTime = string.IsNullOrEmpty(cur.Password);
        cur.Title = next.Title; cur.Password = next.Password; cur.AdminPassword = next.AdminPassword;
        cur.StoragePath = next.StoragePath; cur.Port = next.Port; cur.MaxUploadGB = next.MaxUploadGB; cur.Internet = next.Internet;
        if (firstTime) cur.Enabled = true; // beim ersten Speichern gleich einschalten
        ShareServer.Save();
        if (cur.Enabled)
        {
            ShareServer.Restart();
            if (!ShareServer.Running) { SaveMsg.Text = "Gespeichert, aber Start fehlgeschlagen: " + ShareServer.LastError; Refresh(); return; }
        }
        SaveMsg.Text = "Gespeichert ✓" + (pwChanged && !firstTime ? " – alle Freunde müssen sich mit dem neuen Passwort anmelden" : "");
        _linksKey = ""; _filesKey = "";
        Refresh();
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is string url) { try { Clipboard.SetText(url); Ui.Toast("Link kopiert: " + url); } catch { } }
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is string url) OpenUrl(url);
    }

    private void OpenLocal_Click(object sender, RoutedEventArgs e)
    {
        if (!ShareServer.Running) { Ui.Toast("Schalte die Freigabe zuerst ein", true); return; }
        OpenUrl($"http://localhost:{ShareServer.Settings.Port}");
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var dir = ShareServer.Settings.StoragePath;
        try { Directory.CreateDirectory(dir); Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true }); } catch { }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not ShareServer.FileRow f) return;
        if (MessageBox.Show($"„{f.Name}“ ({f.SizeText}) wirklich löschen?\nDeine Freunde können sie dann nicht mehr herunterladen.",
                "Datei löschen", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        ShareServer.DeleteLocal(f.Id);
        _filesKey = "";
        RefreshFiles();
        Ui.Toast("Gelöscht: " + f.Name);
    }

    private void Speed_Click(object sender, RoutedEventArgs e) => Ui.Go("Werkzeuge");

    // ---------- Meine Freigabe: am PC hinzufügen / herunterladen ----------
    private void HPick_Click(object sender, RoutedEventArgs e)
    {
        var d = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "Dateien für deine Freunde hinzufügen" };
        if (d.ShowDialog() == true) AddLocal(d.FileNames);
    }

    private static string DisplayName(string p) => Directory.Exists(p) ? new DirectoryInfo(p).Name + " (Ordner)" : Path.GetFileName(p);

    /// <summary>Ein Knopf „Hochladen ▾“: Menü mit Dateien oder Ordner.</summary>
    private void UploadMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b) return;
        bool host = (string)b.Tag == "host";
        var menu = new ContextMenu { PlacementTarget = b, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        var files = new MenuItem { Header = "Dateien …" };
        files.Click += (_, _) => { if (host) HPick_Click(b, e); else FPick_Click(b, e); };
        var folders = new MenuItem { Header = "Ordner …" };
        folders.Click += (_, _) => { if (host) HPickFolder_Click(b, e); else FPickFolder_Click(b, e); };
        menu.Items.Add(files);
        menu.Items.Add(folders);
        menu.IsOpen = true;
    }

    private void HPickFolder_Click(object sender, RoutedEventArgs e)
    {
        var d = new Microsoft.Win32.OpenFolderDialog { Multiselect = true, Title = "Ordner für deine Freunde hinzufügen" };
        if (d.ShowDialog() == true) AddLocal(d.FolderNames);
    }

    private void FPickFolder_Click(object sender, RoutedEventArgs e)
    {
        var d = new Microsoft.Win32.OpenFolderDialog { Multiselect = true, Title = "Ordner zum Hochladen wählen" };
        if (d.ShowDialog() == true) StartUploads(d.FolderNames);
    }

    private void HDeleteAll_Click(object sender, RoutedEventArgs e)
    {
        var st = ShareServer.GetStats();
        if (st.Files == 0) { Ui.Toast("Es sind keine Dateien da"); return; }
        if (MessageBox.Show($"Wirklich ALLE {st.Files} Dateien ({ShareServer.Fmt(st.Used)}) löschen?\nDeine Freunde können dann nichts mehr herunterladen. Das lässt sich nicht rückgängig machen.",
                "Alles löschen", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var n = ShareServer.DeleteAllLocal();
        _filesKey = ""; RefreshFiles();
        Ui.Toast($"{n} Datei(en) gelöscht");
    }

    private async void FDeleteOne_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not RemoteFile f) return;
        if (MessageBox.Show($"„{f.Name}“ ({ShareServer.Fmt(f.Size)}) löschen?", "Datei löschen", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try { await ShareClient.DeleteAsync(f); Ui.Toast("Gelöscht: " + f.Name); }
        catch (Exception ex) { Ui.Toast("Löschen fehlgeschlagen: " + ex.Message, true); }
        await FriendRefresh(quiet: true);
    }

    private async void FDeleteAll_Click(object sender, RoutedEventArgs e)
    {
        if (!ShareClient.Connected) { Ui.Toast("Nicht mit deinem Freund verbunden", true); return; }
        var friend = ShareServer.Settings.FriendName ?? "deinem Freund";
        var mine = _friendFiles.Where(f => f.Mine).ToList();
        var text = mine.Count > 0
            ? $"Alles löschen, was du bei {friend} hochgeladen hast ({mine.Count} Datei(en), {ShareServer.Fmt(mine.Sum(f => f.Size))})?"
            : $"Alles löschen, was du bei {friend} hochgeladen hast?";
        if (MessageBox.Show(text + $"\nDateien von {friend} selbst kannst du nicht löschen.", "Alles löschen",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            var n = await ShareClient.DeleteMineAsync();
            Ui.Toast(n == 0 ? "Du hast dort nichts hochgeladen" : $"{n} Datei(en) gelöscht");
        }
        catch (Exception ex) { Ui.Toast("Löschen fehlgeschlagen: " + ex.Message, true); }
        await FriendRefresh(quiet: true);
    }

    private void HDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) AddLocal(paths.Where(p => File.Exists(p) || Directory.Exists(p)).ToArray());
        e.Handled = true;
    }

    private async void AddLocal(string[] paths)
    {
        if (paths.Length == 0) return;
        var category = string.IsNullOrWhiteSpace(HCategory.Text) ? "Sonstiges" : HCategory.Text.Trim();
        var jobs = paths.Select(p => (Path: p, T: new Transfer { Name = DisplayName(p), IsUpload = true, Status = "Wartet …" })).ToList();
        foreach (var j in jobs) _hostTransfers.Insert(0, j.T);
        foreach (var (path, t) in jobs)
        {
            if (t.Cts.IsCancellationRequested) { t.Status = "Abgebrochen"; t.Running = false; continue; }
            var p = Progress(t);
            try
            {
                bool dir = Directory.Exists(path);
                await Task.Run(() => ShareServer.AddLocalAsync(path, category,
                    (d, tot) => p(d, tot, dir ? $"Packe Ordner … {(tot > 0 ? d * 100.0 / tot : 100):0} % ({ShareServer.Fmt(d)} / {ShareServer.Fmt(tot)})" : ""), t.Cts.Token));
                t.Progress = 100; t.Status = "Fertig – deine Freunde sehen die Datei jetzt";
                _ = AutoRemove(t);
            }
            catch (OperationCanceledException) { t.Status = "Abgebrochen"; }
            catch (Exception ex) { t.Status = "Fehler: " + ex.Message; }
            finally { t.Running = false; }
        }
        _filesKey = "";
        RefreshFiles();
    }

    private async void HExport_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not ShareServer.FileRow f) return;
        var t = new Transfer { Name = f.Name };
        _hostTransfers.Insert(0, t);
        var p = Progress(t);
        try
        {
            var path = await Task.Run(() => ShareServer.ExportAsync(f.Id, ShareServer.Settings.DownloadFolder, (d, tot) => p(d, tot, ""), t.Cts.Token));
            t.ResultPath = path; t.Progress = 100; t.Status = "Fertig – gespeichert: " + path;
            Ui.Toast("Gespeichert: " + path);
            _ = AutoRemove(t);
        }
        catch (OperationCanceledException) { t.Status = "Abgebrochen"; }
        catch (Exception ex) { t.Status = "Fehler: " + ex.Message; }
        finally { t.Running = false; }
    }

    private void BrowserToggle_Click(object sender, RoutedEventArgs e)
    {
        ShareServer.Settings.ShowBrowserLinks = BrowserToggle.IsChecked == true;
        ShareServer.Save();
        _linksKey = "";
        Refresh();
    }

    private void CopyInvite_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(InviteBox.Text)) { Ui.Toast("Schalte die Freigabe zuerst ein", true); return; }
        try { Clipboard.SetText(InviteBox.Text); Ui.Toast("Einladungs-Code kopiert – nur an Freunde schicken (enthält das Passwort)"); } catch { }
    }

    // ================= Bei einem Freund =================
    private async Task FriendRefresh(bool quiet = false)
    {
        if (_friendBusy) return;
        var st = ShareServer.Settings;
        FSetup.Visibility = ShareClient.HasFriend ? Visibility.Collapsed : Visibility.Visible;
        FRefreshBtn.IsEnabled = FForgetBtn.IsEnabled = ShareClient.HasFriend;
        if (!ShareClient.HasFriend)
        {
            FState.Text = "Noch nicht verbunden";
            FSub.Text = "";
            _friendFiles = new(); ApplySearch(); FFilesTitle.Text = "DATEIEN"; FFree.Text = "";
            if (_joinCts == null && (!quiet || _found == null)) await Discover();
            return;
        }
        _friendBusy = true;
        try
        {
            if (!quiet || !ShareClient.Connected)
            {
                if (!quiet) { FState.Text = $"Verbinde mit {st.FriendName} …"; FSub.Text = ""; }
                var err = ShareClient.Connected ? null : await ShareClient.ConnectAsync();
                if (err != null) { FState.Text = "Nicht verbunden"; FSub.Text = err; return; }
            }
            var list = await ShareClient.ListAsync();
            _friendFiles = list.Files;
            FState.Text = $"Verbunden mit {st.FriendName}" + (list.Title.Length > 0 && list.Title != "Download Center" ? $" – „{list.Title}“" : "");
            FSub.Text = $"Angemeldet als {ShareClient.MyName} · {ShareClient.BaseUrl}";
            FFilesTitle.Text = $"DATEIEN VON {st.FriendName?.ToUpperInvariant()} ({list.Files.Count})";
            FFree.Text = $"Frei bei {st.FriendName}: {ShareServer.Fmt(list.Free)} · max. {ShareServer.Fmt(list.MaxUpload)} pro Datei";
            _friendCats = list.Files.Select(f => f.Category).Append("Sonstiges").Distinct().OrderBy(c => c).ToList();
            ApplySearch();
        }
        catch (Exception ex)
        {
            ShareClientReset();
            if (!quiet) { FState.Text = "Verbindung verloren"; FSub.Text = ex.Message; }
        }
        finally { _friendBusy = false; }
    }

    private static void ShareClientReset() => ShareClient.Disconnect();

    private void ApplySearch()
    {
        var q = FSearch.Text.Trim();
        var shown = q.Length == 0 ? _friendFiles :
            _friendFiles.Where(f => (f.Name + " " + f.Category + " " + f.Uploader + " " + f.Note).Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        FFiles.ItemsSource = shown;
        FNoFiles.Visibility = ShareClient.HasFriend && shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        FNoFiles.Text = _friendFiles.Count == 0 ? "Noch keine Dateien – lade die erste hoch!" : "Nichts gefunden.";
    }

    private async Task Discover()
    {
        FFoundText.Text = "Suche Download Center …";
        FJoinBtn.IsEnabled = false;
        _found = await ShareClient.DiscoverAsync();
        FJoinBtn.IsEnabled = true;
        if (_found != null)
        {
            FFoundText.Text = $"✔ Download Center von {_found.Owner} gefunden";
            FJoinBtn.Content = "Verbinden";
            FJoinState.Text = $"Klick auf „Verbinden“ – {_found.Owner} bekommt eine Anfrage und muss sie einmal erlauben. Danach bist du dauerhaft verbunden.";
        }
        else
        {
            FFoundText.Text = "Gerade kein Download Center gefunden";
            FJoinBtn.Content = "Nochmal suchen";
            FJoinState.Text = "Entweder ist der PC deines Freundes gerade aus, oder seine Dateifreigabe ist nicht eingeschaltet.";
        }
    }

    private async void FJoin_Click(object sender, RoutedEventArgs e)
    {
        if (_found == null) { await Discover(); return; }
        var name = FMyName.Text.Trim();
        if (name.Length > 0 && name != (ShareServer.Settings.MyName ?? "")) { ShareServer.Settings.MyName = name; ShareServer.Save(); }
        _joinCts = new CancellationTokenSource();
        FJoinBtn.IsEnabled = false; FJoinCancel.Visibility = Visibility.Visible;
        try
        {
            var err = await ShareClient.RequestJoinAsync(_found, msg => Dispatcher.BeginInvoke(() => FJoinState.Text = msg), _joinCts.Token);
            if (err == null) { Ui.Toast($"Verbunden mit {_found.Owner}"); _joinCts = null; await FriendRefresh(); return; }
            FJoinState.Text = err;
        }
        catch (OperationCanceledException) { FJoinState.Text = "Anfrage abgebrochen."; }
        catch (Exception ex) { FJoinState.Text = "Fehler: " + ex.Message; }
        finally { _joinCts = null; FJoinBtn.IsEnabled = true; FJoinCancel.Visibility = Visibility.Collapsed; }
    }

    private void FJoinCancel_Click(object sender, RoutedEventArgs e) => _joinCts?.Cancel();

    private void FShowCode_Click(object sender, RoutedEventArgs e) =>
        FCodeRow.Visibility = FShowCode.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>▾ neben der Kategorie: vorhandene Kategorien zum Anklicken (plus ein paar Vorschläge).</summary>
    private void CategoryPick_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b) return;
        var box = (string)b.Tag == "HCategory" ? HCategory : FCategory;
        var list = ((string)b.Tag == "HCategory" ? _hostCats : _friendCats)
            .Concat(new[] { "Spiele", "Mods", "Musik", "Videos", "Bilder", "Programme", "Dokumente", "Sonstiges" })
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var menu = new ContextMenu { PlacementTarget = b, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var c in list)
        {
            var item = new MenuItem { Header = c };
            item.Click += (_, _) => { box.Text = c; box.Focus(); box.CaretIndex = c.Length; };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    // ---------- Anfragen & Freunde (Meine Freigabe) ----------
    private void Approve_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is string id) { ShareServer.Approve(id); Ui.Toast("Erlaubt – dein Freund ist jetzt verbunden"); _memKey = ""; Refresh(); }
    }

    private void Deny_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is string id) { ShareServer.Deny(id); Refresh(); }
    }

    private void RemoveMember_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not ShareMember m) return;
        if (MessageBox.Show($"Zugang von {m.Name} entfernen?\n{m.Name} kann dann nicht mehr hoch- oder herunterladen, bis du eine neue Anfrage erlaubst.", "Zugang entfernen",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        ShareServer.RemoveMember(m.Id);
        _memKey = ""; Refresh();
    }

    private void OwnerBox_LostFocus(object sender, RoutedEventArgs e)
    {
        var n = OwnerBox.Text.Trim();
        if (n == ShareServer.OwnerName) return;
        ShareServer.Settings.OwnerName = n.Length == 0 ? null : n;
        ShareServer.Save();
        Ui.Toast("Gespeichert – so heißt du jetzt bei deinen Freunden");
    }

    private void FSearch_Changed(object sender, TextChangedEventArgs e) => ApplySearch();

    private async void FConnect_Click(object sender, RoutedEventArgs e)
    {
        var inv = ShareClient.ParseInvite(FCodeBox.Text);
        if (inv == null) { Ui.Toast("Das ist kein gültiger Code – er beginnt mit CCDC-", true); return; }
        ShareClient.SetFriend(inv);
        FCodeBox.Text = "";
        await FriendRefresh();
        if (ShareClient.Connected) Ui.Toast($"Verbunden mit {inv.n}");
    }

    private void FForget_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show($"Verbindung zu {ShareServer.Settings.FriendName} trennen?\nDu brauchst dann wieder einen Einladungs-Code.", "Trennen",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        ShareClient.Forget();
        _ = FriendRefresh();
    }

    private void FRefresh_Click(object sender, RoutedEventArgs e) => _ = FriendRefresh();

    private void FMyName_LostFocus(object sender, RoutedEventArgs e)
    {
        var n = FMyName.Text.Trim();
        if (n == (ShareServer.Settings.MyName ?? "")) return;
        ShareServer.Settings.MyName = n.Length == 0 ? null : n[..Math.Min(30, n.Length)];
        ShareServer.Save();
        if (ShareClient.HasFriend) ShareClient.SetFriend(new ShareClient.Invite(ShareServer.Settings.FriendName ?? "", ShareServer.Settings.FriendUrl ?? "", ShareServer.Settings.FriendPassword ?? "", ShareServer.Settings.FriendPublishes));
        if (ShareClient.HasFriend) _ = FriendRefresh();
    }

    private void FFolder_Click(object sender, RoutedEventArgs e)
    {
        var d = new Microsoft.Win32.OpenFolderDialog { Title = "Downloads speichern in …", InitialDirectory = Directory.Exists(FFolder.Text) ? FFolder.Text : null };
        if (d.ShowDialog() != true) return;
        ShareServer.Settings.DownloadFolder = FFolder.Text = HFolder.Text = d.FolderName;
        ShareServer.Save();
    }

    private void FOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var dir = ShareServer.Settings.DownloadFolder;
        try { Directory.CreateDirectory(dir); Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true }); } catch { }
    }

    /// <summary>Fortschritt mit Tempo und Restzeit (aus beliebigem Thread).</summary>
    private Action<long, long, string> Progress(Transfer t)
    {
        long lastBytes = -1; var lastTime = DateTime.Now; double speed = 0; var lastUi = DateTime.MinValue;
        var gate = new object();
        return (done, total, note) =>
        {
            string text; double pct; string speedText, amount;
            lock (gate)
            {
                var now = DateTime.Now;
                if (lastBytes < 0 || done < lastBytes) { lastBytes = done; lastTime = now; }
                else if ((now - lastTime).TotalSeconds >= 1)
                {
                    var cur = (done - lastBytes) / (now - lastTime).TotalSeconds;
                    speed = speed == 0 ? cur : speed * 0.6 + cur * 0.4;
                    lastBytes = done; lastTime = now;
                }
                if (note.Length == 0 && (now - lastUi).TotalMilliseconds < 250 && done < total) return;
                lastUi = now;
                pct = total > 0 ? done * 100.0 / total : 0;
                var left = speed > 0 ? TimeSpan.FromSeconds((total - done) / speed) : (TimeSpan?)null;
                text = note.Length > 0 ? note :
                    $"{pct:0.0} % fertig" + (speed > 0 ? $" · noch {Dur(left!.Value)}" : "");
                speedText = speed > 0 && done < total ? $"{speed / 1048576:0.0} MB/s" : "";
                amount = $"{ShareServer.Fmt(done)} von {ShareServer.Fmt(total)}";
            }
            Dispatcher.BeginInvoke(() => { t.Progress = pct; t.Status = text; t.Speed = speedText; t.Amount = amount; });
        };
    }

    private static string Dur(TimeSpan t) => t.TotalSeconds < 60 ? $"{t.TotalSeconds:0} s" : t.TotalMinutes < 60 ? $"{(int)t.TotalMinutes} min {t.Seconds} s" : $"{(int)t.TotalHours} h {t.Minutes} min";

    /// <summary>Fertige Übertragung nach ein paar Sekunden aus der Liste nehmen (Fehler bleiben stehen).</summary>
    private async Task AutoRemove(Transfer t)
    {
        await Task.Delay(TimeSpan.FromSeconds(8));
        while (t.Running) await Task.Delay(1000);
        _transfers.Remove(t);
        _hostTransfers.Remove(t);
        if (_transfers.Count == 0) FTransfersCard.Visibility = Visibility.Collapsed;
    }

    private void AddTransfer(Transfer t)
    {
        _transfers.Insert(0, t);
        FTransfersCard.Visibility = Visibility.Visible;
    }

    private async void FDownload_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not RemoteFile f) return;
        if (_transfers.Any(x => x.Running && !x.IsUpload && x.Name == f.Name)) { Ui.Toast("Wird schon heruntergeladen"); return; }
        var t = new Transfer { Name = f.Name };
        AddTransfer(t);
        try
        {
            var path = await Task.Run(() => ShareClient.DownloadAsync(f, ShareServer.Settings.DownloadFolder, Progress(t), t.Cts.Token));
            t.ResultPath = path; t.Progress = 100; t.Status = "Fertig – gespeichert: " + path;
            Ui.Toast("Heruntergeladen: " + f.Name);
            _ = AutoRemove(t);
        }
        catch (OperationCanceledException) { t.Status = "Abgebrochen – nochmal „Herunterladen“ macht an der Stelle weiter."; }
        catch (Exception ex) { t.Status = "Fehler: " + ex.Message; }
        finally { t.Running = false; }
    }

    private void FPick_Click(object sender, RoutedEventArgs e)
    {
        var d = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "Dateien zum Hochladen wählen" };
        if (d.ShowDialog() == true) StartUploads(d.FileNames);
    }

    private void FDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void FDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) StartUploads(paths.Where(p => File.Exists(p) || Directory.Exists(p)).ToArray());
    }

    private async void StartUploads(string[] paths)
    {
        if (paths.Length == 0) return;
        if (!ShareClient.Connected) { await FriendRefresh(); if (!ShareClient.Connected) { Ui.Toast("Nicht mit deinem Freund verbunden", true); return; } }
        var category = string.IsNullOrWhiteSpace(FCategory.Text) ? "Sonstiges" : FCategory.Text.Trim();
        var jobs = paths.Select(p => (Path: p, T: new Transfer { Name = DisplayName(p), IsUpload = true, Status = "Wartet …" })).ToList();
        foreach (var j in jobs) AddTransfer(j.T);
        // nacheinander – sonst teilen sich alle die Leitung und keiner wird fertig
        foreach (var (path, t) in jobs)
        {
            if (t.Cts.IsCancellationRequested) { t.Status = "Abgebrochen"; t.Running = false; continue; }
            try
            {
                await Task.Run(() => ShareClient.UploadAsync(path, category, "", Progress(t), t.Cts.Token));
                t.Progress = 100; t.Status = "Fertig – deine Freunde sehen die Datei jetzt";
                Ui.Toast("Hochgeladen: " + t.Name);
                _ = AutoRemove(t);
            }
            catch (OperationCanceledException) { t.Status = "Pausiert – dieselbe Datei nochmal hochladen macht an der Stelle weiter."; }
            catch (Exception ex) { t.Status = "Fehler: " + ex.Message; }
            finally { t.Running = false; }
        }
        _ = FriendRefresh(quiet: true);
    }

    private void FCancel_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not Transfer t) return;
        if (t.Running) t.Cts.Cancel();
        else
        {
            _transfers.Remove(t);
            _hostTransfers.Remove(t);
            if (_transfers.Count == 0) FTransfersCard.Visibility = Visibility.Collapsed;
        }
    }

    private void FOpenResult_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not Transfer t) return;
        try
        {
            if (t.ResultPath != null && File.Exists(t.ResultPath)) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{t.ResultPath}\"") { UseShellExecute = true });
            else FOpenFolder_Click(sender, e);
        }
        catch { }
    }
}
