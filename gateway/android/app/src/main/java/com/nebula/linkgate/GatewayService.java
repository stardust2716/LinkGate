package com.nebula.linkgate;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.Service;
import android.content.Intent;
import android.content.pm.ServiceInfo;
import android.os.Build;
import android.os.IBinder;

import java.io.File;
import java.io.InputStream;
import java.util.ArrayDeque;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.TimeUnit;

/** User-started, app-owned process supervisor; never scans or kills unrelated PIDs. */
public final class GatewayService extends Service {
    static final Object LOCK = new Object();
    static final String START = "com.nebula.linkgate.START";
    static final String STOP = "com.nebula.linkgate.STOP";
    private static final String CHANNEL = "linkgate-running";
    private static String status = "未启动";
    private static ConnectionDiagnostics diagnostics = new ConnectionDiagnostics();
    private static final ArrayDeque<String> events = new ArrayDeque<>();
    private static boolean busy;
    private static Process process;
    private static long generation;
    private final ExecutorService worker = Executors.newSingleThreadExecutor();

    static boolean isBusy() { synchronized (LOCK) { return busy; } }
    static String summary() {
        synchronized (LOCK) {
            StringBuilder text = new StringBuilder("网关：").append(status).append("\n").append(diagnostics.summary());
            for (String event : events) text.append("\n• ").append(event);
            return text.toString();
        }
    }
    static org.json.JSONObject snapshot() { synchronized(LOCK) {return diagnostics.snapshot();} }
    private static void event(String value) {
        synchronized (LOCK) {
            if (events.size() == 12) events.removeFirst();
            events.addLast(value);
        }
    }
    @Override public void onCreate() {
        super.onCreate();
        NotificationManager manager = getSystemService(NotificationManager.class);
        manager.createNotificationChannel(new NotificationChannel(CHANNEL, "LinkGate 连接", NotificationManager.IMPORTANCE_LOW));
    }
    @Override public int onStartCommand(Intent intent, int flags, int startId) {
        if (intent != null && STOP.equals(intent.getAction())) { stopOwned(true); stopSelf(); return START_NOT_STICKY; }
        if (intent == null || !START.equals(intent.getAction())) { stopSelf(); return START_NOT_STICKY; }
        Notification notice = notification();
        if (Build.VERSION.SDK_INT >= 34) startForeground(1, notice, ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE);
        else startForeground(1, notice);
        final long mine;
        synchronized (LOCK) {
            if (busy) return START_NOT_STICKY;
            busy = true; status = "正在检查配置"; diagnostics = new ConnectionDiagnostics();
            events.clear(); mine = ++generation;
        }
        worker.execute(() -> runOwned(mine));
        return START_NOT_STICKY;
    }
    private Notification notification() {
        PendingIntent open = PendingIntent.getActivity(this, 0, new Intent(this, MainActivity.class), PendingIntent.FLAG_IMMUTABLE);
        PendingIntent stop = PendingIntent.getService(this, 1,
                new Intent(this, GatewayService.class).setAction(STOP), PendingIntent.FLAG_IMMUTABLE);
        return new Notification.Builder(this, CHANNEL)
                .setSmallIcon(android.R.drawable.stat_sys_upload_done).setContentTitle("LinkGate 本地网关")
                .setContentText("点击查看连接；停止只结束本应用的网关。")
                .setContentIntent(open).setOngoing(true)
                .addAction(new Notification.Action.Builder(null, "停止", stop).build()).build();
    }
    private void runOwned(long mine) {
        Process launched = null;
        Process verification = null;
        try {
            File executable = new File(getApplicationInfo().nativeLibraryDir, "libfrpc.so");
            File launcher = new File(getApplicationInfo().nativeLibraryDir, "liblinkgate-launcher.so");
            if (!executable.isFile() || !executable.canExecute() || !launcher.isFile() || !launcher.canExecute()) throw new Exception("missing_runtime");
            try(InputStream lock=getAssets().open("runtime.lock.json")) {
                org.json.JSONObject value=new org.json.JSONObject(new String(ProfileStore.readBounded(lock), java.nio.charset.StandardCharsets.UTF_8));
                String expected=value.getString("androidSha256");
                java.security.MessageDigest digest=java.security.MessageDigest.getInstance("SHA-256");
                try(InputStream runtime=new java.io.FileInputStream(executable)) {byte[] b=new byte[65536];int n;while((n=runtime.read(b))>0)digest.update(b,0,n);}
                StringBuilder actual=new StringBuilder();for(byte b:digest.digest())actual.append(String.format(java.util.Locale.ROOT,"%02x",b&255));
                if(!expected.equals(actual.toString()))throw new Exception("runtime_integrity");
            }
            final File config;
            synchronized (LOCK) {
                if (mine != generation || !busy) return;
                config = ProfileStore.prepare(this, ProfileStore.load(this));
            }
            verification = runtimeProcess(launcher,executable,"verify","-c",config.getAbsolutePath());
            synchronized (LOCK) {
                if (mine != generation || !busy) { destroy(verification); return; }
                process = verification;
            }
            Thread verifyDrain = drain(verification.getInputStream(), false, mine);
            boolean verified = verification.waitFor(10, TimeUnit.SECONDS);
            if (!verified) destroy(verification);
            verifyDrain.join(1500);
            if (!verified || verification.exitValue() != 0) throw new Exception("invalid_runtime_config");
            synchronized (LOCK) {
                if (mine != generation || !busy) return;
                launched = runtimeProcess(launcher,executable,"-c",config.getAbsolutePath());
                process = launched; status = "运行中，等待服务连接";
            }
            event("本地访客已启动；仅监听 127.0.0.1。");
            Thread output = drain(launched.getInputStream(), true, mine);
            int exit = launched.waitFor(); output.join(1500);
            synchronized (LOCK) {
                if (mine == generation) { status = exit == 0 ? "已停止" : "连接进程已退出，请检查服务器及 Clash 设置"; }
            }
        } catch (Exception error) {
            synchronized (LOCK) {
                if (mine == generation) {
                    status = "启动失败；请检查配对文件及运行时版本";
                    if ("missing_runtime".equals(error.getMessage())) status = "安装包缺少可执行的 FRP 运行时";
                }
            }
            event("未输出原始错误内容，避免泄露配对凭据。");
        } finally {
            if (verification != null && verification.isAlive()) destroy(verification);
            if (launched != null && launched.isAlive()) destroy(launched);
            synchronized (LOCK) {
                if (mine == generation) { process = null; busy = false; diagnostics.endActive(); stopForeground(STOP_FOREGROUND_REMOVE); stopSelf(); }
            }
        }
    }
    private Process runtimeProcess(File launcher,File executable,String... arguments) throws java.io.IOException {
        java.util.ArrayList<String> command=new java.util.ArrayList<>();
        command.add(launcher.getAbsolutePath());command.add(Integer.toString(android.os.Process.myPid()));command.add(executable.getAbsolutePath());
        java.util.Collections.addAll(command,arguments);
        ProcessBuilder builder=new ProcessBuilder(command).redirectErrorStream(true);
        builder.environment().put("LINKGATE_MULTI_STUN","1");
        return builder.start();
    }
    private Thread drain(InputStream input, boolean observe, long mine) {
        Thread thread = new Thread(() -> {
            // A bounded line buffer also protects against a malformed or enormous log line.
            byte[] buffer = new byte[2048];
            StringBuilder line = new StringBuilder();
            try {
                int count;
                while ((count = input.read(buffer)) != -1) {
                    for (int i = 0; i < count; i++) {
                        int c = buffer[i] & 255;
                        if (c == '\n') { if (observe) observe(line.toString(), mine); line.setLength(0); }
                        else if (line.length() < 4096 && c != '\r') line.append((char)c);
                    }
                }
            } catch (Exception ignored) { /* Process exit closes its own stream. */ }
        }, "linkgate-output");
        thread.setDaemon(true); thread.start(); return thread;
    }
    private static void observe(String raw, long mine) {
        // No raw log line ever reaches the UI, Android logcat, or persistent storage.
        FrpLogObservation.Event observed = FrpLogObservation.classify(raw);
        synchronized (LOCK) {
            if (generation != mine || !busy) return;
            if(raw.startsWith("LINKGATE_METRIC ")) {diagnostics.observe(raw);return;}
            if(raw.startsWith("LINKGATE_STUN ")) {diagnostics.observeStun(raw);return;}
            if (observed == FrpLogObservation.Event.COORDINATOR_LOGIN) {
                status = "已连接协调服务器，等待应用访问";
            } else if (observed == FrpLogObservation.Event.NAT_HOLE_OBSERVED) {
                diagnostics.natHoleObserved();
            } else if (observed == FrpLogObservation.Event.STUN_OBSERVATIONS_INSUFFICIENT) {
                diagnostics.natPreparationFailed();
            }
        }
    }
    private void stopOwned(boolean explicit) {
        Process owned;
        synchronized (LOCK) {
            ++generation; owned = process; process = null;
            if (explicit || busy) status = "已停止";
            busy = false;
            diagnostics.endActive();
        }
        if (owned != null) destroy(owned);
        stopForeground(STOP_FOREGROUND_REMOVE);
    }
    private static void destroy(Process owned) {
        owned.destroy();
        try { if (!owned.waitFor(1200, TimeUnit.MILLISECONDS)) owned.destroyForcibly(); }
        catch (InterruptedException error) { Thread.currentThread().interrupt(); owned.destroyForcibly(); }
    }
    @Override public void onDestroy() { stopOwned(false); worker.shutdownNow(); super.onDestroy(); }
    @Override public IBinder onBind(Intent intent) { return null; }
}
