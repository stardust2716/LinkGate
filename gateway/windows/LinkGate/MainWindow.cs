namespace LinkGate;

internal sealed class MainWindow : Form
{
    private readonly GatewayHost host = new();
    private readonly PrivateStore store;
    private GatewayProfile? profile;
    private readonly Label device = new() { AutoSize = true, Text = "未导入配对配置" };
    private readonly Label status = new() { AutoSize = true };
    private readonly Label evidence = new() { AutoSize = true };
    private readonly ListView services = new() { View = View.Details, FullRowSelect = true, Dock = DockStyle.Fill };
    private readonly TextBox events = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
    private readonly Button import = new() { Text = "导入配置…", AutoSize = true };
    private readonly Button publish = new() { Text = "发布本机服务", AutoSize = true };
    private readonly Button visit = new() { Text = "访问远端服务", AutoSize = true };
    private readonly Button verify = new() { Text = "检查配置", AutoSize = true };
    private readonly Button stop = new() { Text = "停止", AutoSize = true };
    private bool operation;
    private readonly System.Windows.Forms.Timer refresh = new() { Interval = 1000 };
    private readonly bool emitDiagnostics;

    public MainWindow(GatewayProfile? initialProfile = null, GatewayRole? initialRole = null, PrivateStore? initialStore = null)
    {
        Text = "LinkGate 0.2.3 — 多端点 STUN 与物理网卡绕行";
        Size = new Size(860, 620);
        MinimumSize = new Size(660, 480);
        StartPosition = FormStartPosition.CenterScreen;
        store = initialStore ?? new PrivateStore();
        emitDiagnostics = initialProfile is not null;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, Padding = new Padding(16) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 48));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 52));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        buttons.Controls.AddRange([import, publish, visit, verify, stop]);
        var copy = new Button { Text = "复制诊断 JSON", AutoSize = true };
        copy.Click += (_, _) => Clipboard.SetText(System.Text.Json.JsonSerializer.Serialize(host.DiagnosticsSnapshot()));
        buttons.Controls.Add(copy);
        layout.Controls.Add(device, 0, 0);
        layout.Controls.Add(status, 0, 1);
        layout.Controls.Add(evidence, 0, 2);
        services.Columns.Add("服务", 230); services.Columns.Add("本机发布目标", 200); services.Columns.Add("访客本地入口", 210);
        layout.Controls.Add(services, 0, 3);
        layout.Controls.Add(buttons, 0, 4);
        layout.Controls.Add(events, 0, 5);
        layout.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(790, 0), Text = "统计业务明文字节，不含 TLS/QUIC 和协调流量。路径按每条连接选定的 XTCP/STCP 记录；零字节连接尚无业务证据。建立耗时：访客含直连和回退，发布端仅本地服务建立。退出停止本实例网关。" }, 0, 6);
        Controls.Add(layout);
        import.Click += async (_, _) => await OperationAsync(async () =>
        {
            using var picker = new OpenFileDialog { Filter = "配对配置 (*.json)|*.json", CheckFileExists = true };
            if (picker.ShowDialog(this) != DialogResult.OK) return;
            store.Import(picker.FileName);
            profile = GatewayProfile.Load(store.ProfilePath);
            ShowProfile();
            await Task.CompletedTask;
        });
        publish.Click += async (_, _) => await OperationAsync(() => host.StartAsync(profile!, GatewayRole.Publisher, store));
        visit.Click += async (_, _) => await OperationAsync(() => host.StartAsync(profile!, GatewayRole.Visitor, store));
        verify.Click += async (_, _) => await OperationAsync(async () =>
        {
            await host.VerifyAsync(profile!, GatewayRole.Publisher, store);
            await host.VerifyAsync(profile!, GatewayRole.Visitor, store);
            MessageBox.Show(this, "配置与运行时检查通过。此检查未验证公网连接或直连。", "LinkGate");
        });
        stop.Click += (_, _) => { host.Stop(); RefreshStatus(); };
        host.Changed += HostChanged;
        if (initialProfile is not null)
        {
            profile = initialProfile; ShowProfile();
            Shown += async (_, _) => await OperationAsync(() => host.StartAsync(profile, initialRole!.Value, store));
        }
        else if (File.Exists(store.ProfilePath))
        {
            try { profile = GatewayProfile.Load(store.ProfilePath); ShowProfile(); }
            catch (GatewayException) { status.Text = "已有配置无效，请重新导入。"; }
        }
        RefreshStatus();
        refresh.Tick += (_, _) => RefreshStatus(); refresh.Start();
    }

    private async Task OperationAsync(Func<Task> action)
    {
        if (operation) return;
        operation = true; RefreshStatus();
        try { await action(); }
        catch (GatewayException error) { MessageBox.Show(this, "操作失败（" + error.Message + "）。原配对配置会保留。", "LinkGate"); }
        catch (Exception) { MessageBox.Show(this, "操作失败（operation_failed）。请确认文件和端口可用。", "LinkGate"); }
        finally { operation = false; if (!IsDisposed) RefreshStatus(); }
    }
    private void ShowProfile()
    {
        device.Text = profile!.DeviceName + " · " + profile.Services.Count + " 项服务";
        services.Items.Clear();
        foreach (var service in profile.Services)
            services.Items.Add(new ListViewItem([service.Label, "127.0.0.1:" + service.LocalPort, "127.0.0.1:" + service.VisitorPort]));
    }
    private void HostChanged()
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(RefreshStatus); } catch (InvalidOperationException) { }
    }
    private void RefreshStatus()
    {
        if (IsDisposed) return;
        status.Text = "状态：" + host.Status;
        evidence.Text = "路径：" + host.PathEvidence;
        events.Lines = host.Events.ToArray();
        if (emitDiagnostics) Program.Emit(new { timeUtc = DateTimeOffset.UtcNow, diagnostics = host.DiagnosticsSnapshot() });
        var running = host.IsRunning;
        import.Enabled = !operation && !running;
        publish.Enabled = visit.Enabled = verify.Enabled = profile is not null && !operation && !running;
        stop.Enabled = !operation && running;
    }
    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        host.Changed -= HostChanged;
        refresh.Dispose();
        host.Dispose();
        base.OnFormClosed(e);
    }
}
