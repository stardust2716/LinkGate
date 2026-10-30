package dev.linkgate.agent.android;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.Service;
import android.content.Intent;
import android.os.IBinder;
import android.os.SystemClock;
import dev.linkgate.authority.ControlClient;
import dev.linkgate.authority.LeaseClock;
import java.util.concurrent.ThreadPoolExecutor;
import java.util.concurrent.ArrayBlockingQueue;
import java.util.concurrent.TimeUnit;

public final class GatewayService extends Service {
    static final String STOP="dev.linkgate.agent.OWNER_STOP",START="dev.linkgate.agent.OWNER_START";
    private static GatewayService owner;
    static void requestStart(android.content.Context context){context.getSharedPreferences("owner",MODE_PRIVATE).edit().putBoolean("enabled",true).commit();context.startForegroundService(new Intent(context,GatewayService.class).setAction(START));}
    static void requestStop(android.content.Context context){context.getSharedPreferences("owner",MODE_PRIVATE).edit().putBoolean("enabled",false).commit();context.startService(new Intent(context,GatewayService.class).setAction(STOP));}
    private static volatile AgentConnections running;
    private static volatile String state="GATEWAY_STOPPED";
    private static long generation;
    private static boolean starting;
    private final ThreadPoolExecutor setup=new ThreadPoolExecutor(1,1,0,TimeUnit.SECONDS,new ArrayBlockingQueue<>(1),task->{Thread thread=new Thread(task,"LinkGate-Startup");thread.setDaemon(true);return thread;});
    static synchronized boolean startingOrRunning(){return starting||running!=null;}
    static AgentConnections current(){return running;}
    static String state(){return state;}
    @Override public void onCreate(){
        super.onCreate();foreground();synchronized(GatewayService.class){if(owner!=this){stopRuntime();owner=this;}}
    }
    private void foreground(){
        NotificationManager manager=getSystemService(NotificationManager.class);manager.createNotificationChannel(new NotificationChannel("gateway","设备连接网关",NotificationManager.IMPORTANCE_LOW));
        Intent stop=new Intent(this,GatewayService.class).setAction(STOP);PendingIntent stopIntent=PendingIntent.getService(this,1,stop,PendingIntent.FLAG_IMMUTABLE|PendingIntent.FLAG_UPDATE_CURRENT);
        PendingIntent open=PendingIntent.getActivity(this,2,new Intent(this,OwnerActivity.class),PendingIntent.FLAG_IMMUTABLE|PendingIntent.FLAG_UPDATE_CURRENT);
        Notification notification=new Notification.Builder(this,"gateway").setSmallIcon(com.nebula.linkgate.agent.R.drawable.ic_linkgate_notification).setContentTitle("LinkGate").setContentText("网关已由你启动，可在此停止").setContentIntent(open).setOngoing(true).addAction(new Notification.Action.Builder(null,"停止网关",stopIntent).build()).build();
        startForeground(7041,notification);
    }
    private void ensureStart(int startId){
        if(!getSharedPreferences("owner",MODE_PRIVATE).getBoolean("enabled",false)){stopSelf(startId);return;}
        final long selected;
        synchronized(GatewayService.class){if(owner!=this||running!=null||starting)return;selected=++generation;starting=true;state="starting";}
        setup.getQueue().clear();
        try{setup.execute(()->startRuntime(selected));}catch(RuntimeException failure){synchronized(GatewayService.class){if(owner==this&&selected==generation){starting=false;state="STARTUP_UNAVAILABLE";stopSelf(startId);}}}
    }
    private void startRuntime(long selected) {
        AgentConnections created=null;ControlClient authority=null;FrpRoutes routes=null;
        try {
            AgentRepository repository=AgentRepository.get(this);var identity=repository.identity.load();var root=repository.identity.root();repository.registry.bindIdentity(identity.deviceId(),AndroidIdentity.fingerprint(root));
            ServerSettings settings=ServerSettings.load(this,repository.identity);
            if(settings!=null){
                LeaseClock clock=new LeaseClock(new LeaseClock.Source(){public long utcMillis(){return System.currentTimeMillis();}public long nanos(){return SystemClock.elapsedRealtimeNanos();}});
                authority=ControlClient.connect(settings.address().getAddress(),settings.controlPort,identity,settings.certificate,root,clock);
                if(FrpRuntimeBridge.available(this))routes=new FrpRoutes(this,repository.registry,identity,settings,root);
            }
            synchronized(GatewayService.class){
                if(owner!=this||selected!=generation||!getSharedPreferences("owner",MODE_PRIVATE).getBoolean("enabled",false))return;
                created=new AgentConnections(this,repository.registry,identity,authority,routes);authority=null;routes=null;
                running=created;created=null;starting=false;state=settings==null?"running-preview":"running-server";
            }
        }catch(Exception failure){synchronized(GatewayService.class){if(owner==this&&selected==generation){starting=false;state="IDENTITY_STORAGE_OR_SERVER_REQUIRED";new android.os.Handler(android.os.Looper.getMainLooper()).post(()->{synchronized(GatewayService.class){if(owner==this&&selected==generation)stopSelf();}});}}}
        finally{if(created!=null)created.close();if(routes!=null)routes.close();if(authority!=null)authority.close();}
    }

    @Override public int onStartCommand(Intent intent,int flags,int startId){
        if(intent!=null&&STOP.equals(intent.getAction())){getSharedPreferences("owner",MODE_PRIVATE).edit().putBoolean("enabled",false).commit();synchronized(GatewayService.class){if(owner==this)stopRuntime();}stopForeground(STOP_FOREGROUND_REMOVE);stopSelf(startId);return START_NOT_STICKY;}
        foreground();
        if(intent!=null&&START.equals(intent.getAction()))getSharedPreferences("owner",MODE_PRIVATE).edit().putBoolean("enabled",true).commit();
        ensureStart(startId);return getSharedPreferences("owner",MODE_PRIVATE).getBoolean("enabled",false)?START_STICKY:START_NOT_STICKY;
    }
    private static synchronized void stopRuntime(){generation++;starting=false;AgentConnections prior=running;running=null;if(prior!=null)prior.close();if(state.startsWith("running")||state.equals("starting"))state="GATEWAY_STOPPED";}
    @Override public IBinder onBind(Intent intent){return null;}
    @Override public void onDestroy(){synchronized(GatewayService.class){if(owner==this){stopRuntime();owner=null;}}setup.shutdownNow();super.onDestroy();}
}
