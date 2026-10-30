package dev.linkgate.agent.android;

import android.content.Context;
import android.net.ConnectivityManager;
import android.net.LinkProperties;
import android.net.Network;
import android.net.NetworkCapabilities;
import java.io.Closeable;
import java.util.ArrayList;
import java.util.Collections;
import java.util.concurrent.ArrayBlockingQueue;
import java.util.concurrent.ThreadPoolExecutor;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;

final class NetworkObserver implements Closeable {
    private final ConnectivityManager manager;private final Runnable changed;private final AtomicBoolean closed=new AtomicBoolean(),dirty=new AtomicBoolean(),scheduled=new AtomicBoolean();
    private final ThreadPoolExecutor worker=new ThreadPoolExecutor(1,1,0,TimeUnit.SECONDS,new ArrayBlockingQueue<>(1),task->{Thread thread=new Thread(task,"LinkGate-Network");thread.setDaemon(true);return thread;});
    private String previous;
    private final ConnectivityManager.NetworkCallback callback=new ConnectivityManager.NetworkCallback(){
        @Override public void onAvailable(Network network){signal();}
        @Override public void onLost(Network network){signal();}
        @Override public void onCapabilitiesChanged(Network network,NetworkCapabilities capabilities){signal();}
        @Override public void onLinkPropertiesChanged(Network network,LinkProperties properties){signal();}
    };
    NetworkObserver(Context context,Runnable changed){manager=context.getSystemService(ConnectivityManager.class);if(manager==null)throw new IllegalStateException("Network observation unavailable.");this.changed=changed;previous=stamp();try{manager.registerDefaultNetworkCallback(callback);signal();}catch(RuntimeException failure){worker.shutdownNow();throw failure;}}
    private String stamp(){Network network=manager.getActiveNetwork();if(network==null)return "none";NetworkCapabilities capabilities=manager.getNetworkCapabilities(network);LinkProperties properties=manager.getLinkProperties(network);StringBuilder value=new StringBuilder().append(network.getNetworkHandle());if(capabilities!=null)for(int transport:new int[]{NetworkCapabilities.TRANSPORT_WIFI,NetworkCapabilities.TRANSPORT_CELLULAR,NetworkCapabilities.TRANSPORT_ETHERNET,NetworkCapabilities.TRANSPORT_VPN})value.append('|').append(capabilities.hasTransport(transport));if(properties!=null){ArrayList<String> links=new ArrayList<>();for(var address:properties.getLinkAddresses())links.add(address.toString());for(var route:properties.getRoutes())links.add(route.toString());Collections.sort(links);value.append('|').append(properties.getInterfaceName()).append(links);}return value.toString();}
    private void signal(){if(closed.get())return;dirty.set(true);if(!scheduled.compareAndSet(false,true))return;try{worker.execute(()->{try{while(dirty.getAndSet(false)&&!closed.get()){String current=stamp();if(!current.equals(previous)){previous=current;changed.run();}}}finally{scheduled.set(false);if(dirty.get()&&!closed.get())signal();}});}catch(RuntimeException stopped){scheduled.set(false);}}
    @Override public void close(){if(!closed.compareAndSet(false,true))return;try{manager.unregisterNetworkCallback(callback);}catch(RuntimeException ignored){}worker.shutdownNow();}
}
