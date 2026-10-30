package dev.linkgate.agent.android;

import android.content.pm.PackageInfo;
import android.content.pm.PackageManager;
import android.content.pm.Signature;
import android.os.Binder;
import android.os.Build;
import android.os.Process;
import dev.linkgate.agent.SubjectIdentity;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.util.ArrayList;
import java.util.Collections;
import java.util.HashMap;
import java.util.List;
import java.util.Map;

final class CallerPrincipal {
    final int uid;final String scope,provenance;
    private CallerPrincipal(int uid,SubjectIdentity value){this.uid=uid;scope=value.scope;provenance=value.provenance;}
    static CallerPrincipal capture(PackageManager manager)throws Exception {
        int uid=Binder.getCallingUid();if(!Process.myUserHandle().equals(Binder.getCallingUserHandle()))throw new SecurityException("AUTH_REQUIRED");
        SubjectIdentity before=snapshot(manager,uid);if(!before.provenance.equals(snapshot(manager,uid).provenance))throw new SecurityException("AUTH_REQUIRED");return new CallerPrincipal(uid,before);
    }
    @SuppressWarnings("deprecation") private static SubjectIdentity snapshot(PackageManager manager,int uid)throws Exception {
        String[] packages=manager.getPackagesForUid(uid);if(packages==null||packages.length!=1)throw new SecurityException("AUTH_REQUIRED");
        PackageInfo info=manager.getPackageInfo(packages[0],Build.VERSION.SDK_INT>=28?PackageManager.GET_SIGNING_CERTIFICATES:PackageManager.GET_SIGNATURES);
        if(info.applicationInfo==null||info.applicationInfo.uid!=uid||info.sharedUserId!=null||info.firstInstallTime<=0)throw new SecurityException("AUTH_REQUIRED");
        Signature[] current,history;boolean multiple;
        if(Build.VERSION.SDK_INT>=28){if(info.signingInfo==null)throw new SecurityException("AUTH_REQUIRED");current=info.signingInfo.getApkContentsSigners();multiple=info.signingInfo.hasMultipleSigners();history=multiple?current:info.signingInfo.getSigningCertificateHistory();}else{current=info.signatures;history=current;multiple=current!=null&&current.length>1;}
        return new SubjectIdentity(uid,packages[0],info.firstInstallTime,hashes(current),hashes(history),multiple);
    }
    private static List<String> hashes(Signature[] values)throws Exception {if(values==null||values.length==0||values.length>32)throw new SecurityException("AUTH_REQUIRED");List<String> hashes=new ArrayList<>();for(Signature value:values){if(value==null)throw new SecurityException("AUTH_REQUIRED");hashes.add(hash(value.toByteArray()));}return hashes;}
    private static String hash(byte[] bytes)throws Exception{StringBuilder text=new StringBuilder(64);for(byte value:MessageDigest.getInstance("SHA-256").digest(bytes)){text.append(Character.forDigit((value>>>4)&15,16));text.append(Character.forDigit(value&15,16));}return text.toString();}
    static final class Gate {
        private final Map<Integer,Integer> active=new HashMap<>();private int total;
        synchronized Lease acquire(int uid){int count=active.containsKey(uid)?active.get(uid):0;if(count>=8||total>=32)return null;active.put(uid,count+1);total++;return new Lease(uid);}
        final class Lease implements AutoCloseable {private final int uid;private boolean closed;Lease(int uid){this.uid=uid;}@Override public void close(){synchronized(Gate.this){if(closed)return;closed=true;int count=active.get(uid)-1;if(count==0)active.remove(uid);else active.put(uid,count);total--;}}}
    }
}
