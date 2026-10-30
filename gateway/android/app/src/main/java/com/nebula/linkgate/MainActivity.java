package com.nebula.linkgate;

import android.Manifest;
import android.app.Activity;
import android.content.ClipData;
import android.content.ClipboardManager;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.graphics.Color;
import android.net.Uri;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.view.View;
import android.widget.Button;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;
import android.widget.Toast;

import java.io.InputStream;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

public final class MainActivity extends Activity {
    private static final int IMPORT = 10;
    private static final int NOTIFICATION_PERMISSION = 11;
    private final Handler handler = new Handler(Looper.getMainLooper());
    private final ExecutorService importer = Executors.newSingleThreadExecutor();
    private TextView status, services;
    private Button start, stop, importButton;
    private boolean importing;
    private final Runnable update = new Runnable() {
        @Override public void run() { refresh(); handler.postDelayed(this, 1000); }
    };

    @Override public void onCreate(Bundle state) {
        super.onCreate(state);
        LinearLayout body = new LinearLayout(this); body.setOrientation(LinearLayout.VERTICAL);
        int padding = (int)(20 * getResources().getDisplayMetrics().density);
        body.setPadding(padding, padding, padding, padding);
        body.setOnApplyWindowInsetsListener((view, insets) -> {
            view.setPadding(padding + insets.getSystemWindowInsetLeft(),
                    padding + insets.getSystemWindowInsetTop(),
                    padding + insets.getSystemWindowInsetRight(),
                    padding + insets.getSystemWindowInsetBottom());
            return insets;
        });
        TextView title = text("LinkGate", 28); body.addView(title);
        body.addView(text("连接诊断 · 预览版 0.2.3", 16));
        body.addView(text("优先自建 STUN；观测不足时使用 Cloudflare／FRP 默认公共 STUN 获取映射。它们不转发业务流量，失败仍回退私有中继。", 13));
        body.addView(text("先从电脑导出配对文件，导入后启动网关。现有应用连接下方本机地址。", 15));
        importButton = button("导入配对文件", v -> chooseProfile()); body.addView(importButton);
        start = button("启动网关", v -> startGateway()); body.addView(start);
        stop = button("停止网关", v -> startService(new Intent(this, GatewayService.class).setAction(GatewayService.STOP)));
        body.addView(stop);
        status = text("", 15); status.setTextIsSelectable(true); body.addView(status);
        services = text("", 16); services.setTextIsSelectable(true); body.addView(services);
        body.addView(button("复制本机服务地址", v -> copyAddresses()));
        body.addView(button("复制诊断 JSON", v -> getSystemService(ClipboardManager.class).setPrimaryClip(ClipData.newPlainText("LinkGate 诊断", GatewayService.snapshot().toString()))));
        body.addView(button("运行 60 秒文件和 WebSocket 测试", v -> {
            if(NetworkTest.running || !GatewayService.isBusy()) {Toast.makeText(this,"请先启动网关，并等待已有测试结束",Toast.LENGTH_SHORT).show();return;}
            String[] scenarios={"wifi-clash-off","wifi-clash-on","mobile-clash-off","mobile-clash-on"};
            new android.app.AlertDialog.Builder(this).setTitle("确认手机当前网络（请先手动切换）")
                .setItems(scenarios,(dialog,index)->{
                    try{Profile p=ProfileStore.load(this); NetworkTest.start(p.services.get(0).visitorPort,scenarios[index]);}
                    catch(Exception error){Toast.makeText(this,"请检查配置",Toast.LENGTH_SHORT).show();}
                }).show();
        }));
        body.addView(button("复制最近测试报告", v -> getSystemService(ClipboardManager.class).setPrimaryClip(ClipData.newPlainText("LinkGate 网络测试",NetworkTest.result))));
        body.addView(text("Clash 同时运行时，请先停止 Clash，在其访问控制中排除 LinkGate，再启动 Clash。已有排除列表请保留。此应用不申请 VPN 权限。", 14));
        body.addView(text("每条连接按实际 XTCP/STCP 转发入口计数。打洞与业务直连分别显示。统计明文字节，不含 TLS/QUIC、协调流量；中继仍消耗服务器带宽。建立耗时包含直连尝试及回退。", 14));
        body.addView(text("网关由你手动启动，运行期间显示通知。退出本页不会停止；请使用“停止网关”或通知中的停止按钮。系统结束后台后需要手动重新启动。", 14));
        ScrollView scroll = new ScrollView(this); scroll.setFillViewport(true); scroll.addView(body); setContentView(scroll);
        refresh();
    }
    private TextView text(String value, int size) {
        TextView view = new TextView(this); view.setText(value); view.setTextSize(size);
        view.setTextColor(Color.rgb(30, 40, 55)); view.setPadding(0, 12, 0, 12); return view;
    }
    private Button button(String title, View.OnClickListener listener) {
        Button button = new Button(this); button.setText(title); button.setOnClickListener(listener); return button;
    }
    private void refresh() {
        if (status == null) return;
        status.setText(GatewayService.summary()+"\n\n网络测试："+(NetworkTest.running?"运行中，请保持当前网络":"已停止，可复制最近报告"));
        boolean busy = GatewayService.isBusy();
        importButton.setEnabled(!busy && !importing); stop.setEnabled(busy);
        try {
            Profile profile = ProfileStore.load(this);
            start.setEnabled(!busy && !importing);
            StringBuilder items = new StringBuilder("设备：").append(profile.deviceName).append("\n\n");
            for (Profile.Service service : profile.services) {
                items.append(service.label).append("\n").append(service.localAddress()).append("\n\n");
            }
            services.setText(items.toString());
        } catch (Exception error) { start.setEnabled(false); services.setText("尚未导入有效配对文件。"); }
    }
    private void chooseProfile() {
        if (GatewayService.isBusy() || importing) return;
        Intent intent = new Intent(Intent.ACTION_OPEN_DOCUMENT).setType("*/*").addCategory(Intent.CATEGORY_OPENABLE);
        startActivityForResult(intent, IMPORT);
    }
    @Override protected void onActivityResult(int request, int result, Intent data) {
        super.onActivityResult(request, result, data);
        if (request != IMPORT || result != RESULT_OK || data == null || data.getData() == null) return;
        final Uri uri = data.getData(); importing = true; refresh();
        importer.execute(() -> {
            String message;
            try (InputStream input = getContentResolver().openInputStream(uri)) {
                if (input == null) throw new Exception();
                Profile value = Profile.parse(ProfileStore.readBounded(input));
                synchronized (GatewayService.LOCK) {
                    if (GatewayService.isBusy()) throw new Exception();
                    ProfileStore.save(this, value);
                }
                message = "配对文件已保存；原文件包含凭据，请妥善保管。";
            } catch (Exception error) { message = "导入失败，旧配对未改动。请检查文件、证书和端口，并先停止网关。"; }
            final String shown = message;
            handler.post(() -> { importing = false; if (!isFinishing() && !isDestroyed()) { Toast.makeText(this, shown, Toast.LENGTH_LONG).show(); refresh(); } });
        });
    }
    private void startGateway() {
        if (GatewayService.isBusy()) return;
        if (Build.VERSION.SDK_INT >= 33 && checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) {
            requestPermissions(new String[]{Manifest.permission.POST_NOTIFICATIONS}, NOTIFICATION_PERMISSION); return;
        }
        launch();
    }
    @Override public void onRequestPermissionsResult(int request, String[] permissions, int[] results) {
        super.onRequestPermissionsResult(request, permissions, results);
        if (request == NOTIFICATION_PERMISSION) launch();
    }
    private void launch() {
        try { startForegroundService(new Intent(this, GatewayService.class).setAction(GatewayService.START)); }
        catch (Exception error) { Toast.makeText(this, "系统暂不允许启动后台网关，请保持本页前台后重试。", Toast.LENGTH_LONG).show(); }
        handler.postDelayed(this::refresh, 300);
    }
    private void copyAddresses() {
        try {
            StringBuilder addresses = new StringBuilder();
            for (Profile.Service service : ProfileStore.load(this).services) addresses.append(service.label).append(": ").append(service.localAddress()).append('\n');
            getSystemService(ClipboardManager.class).setPrimaryClip(ClipData.newPlainText("LinkGate 本机地址", addresses.toString()));
            Toast.makeText(this, "已复制服务地址，不包含配对凭据。", Toast.LENGTH_SHORT).show();
        } catch (Exception error) { Toast.makeText(this, "请先导入配对文件。", Toast.LENGTH_SHORT).show(); }
    }
    @Override protected void onResume() { super.onResume(); handler.post(update); }
    @Override protected void onPause() { handler.removeCallbacks(update); super.onPause(); }
    @Override protected void onDestroy() { handler.removeCallbacksAndMessages(null); importer.shutdownNow(); super.onDestroy(); }
}
