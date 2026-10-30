package com.nebula.linkgate;

import org.json.JSONObject;
import java.util.*;

/** Counters belong to a single runtime generation. Only structured FRP hooks count. */
final class ConnectionDiagnostics {
    private static final Set<String> FIELDS = new HashSet<>(Arrays.asList("v","id","role","path","phase","tx","rx","setupMs","elapsedMs","reason"));
    private static final class Entry {
        String path, role, phase; long tx,rx,elapsed;
        Entry(String p,String r,String s,long t,long x,long e) {path=p;role=r;phase=s;tx=t;rx=x;elapsed=e;}
    }
    private final Map<Long,Entry> streams = new HashMap<>();
    private final ArrayDeque<long[]> samples = new ArrayDeque<>();
    private long directTx,directRx,relayTx,relayRx,setupMs;
    private String reason="none";
    private boolean natHole,incomplete;
    private String natPreparationFailure="none";
    private String stunStatus="not-started";
    private int stunObservations,stunAttemptedEndpoints;
    private boolean stunFallbackUsed;
    synchronized boolean observeStun(String line){
        String prefix="LINKGATE_STUN ";if(!line.startsWith(prefix)||line.length()>512)return false;
        try{
            String raw=line.substring(prefix.length());StrictJson.validate(raw);JSONObject j=new JSONObject(raw);
            Set<String> fields=new HashSet<>();j.keys().forEachRemaining(fields::add);
            if(!fields.equals(new HashSet<>(Arrays.asList("v","status","observations","attemptedEndpoints","fallbackUsed")))||integer(j,"v")!=1)return false;
            long observations=integer(j,"observations"),attempts=integer(j,"attemptedEndpoints");String status=j.getString("status");
            if(!(j.get("fallbackUsed") instanceof Boolean)||observations<0||observations>2||attempts<observations||attempts>8||
                (status.equals("ready")?observations!=2:status.equals("insufficient")?observations!=1:status.equals("unreachable")?observations!=0:true))return false;
            stunStatus=status;stunObservations=(int)observations;stunAttemptedEndpoints=(int)attempts;stunFallbackUsed=j.getBoolean("fallbackUsed");
            natPreparationFailure=status.equals("ready")?"none":status.equals("insufficient")?"stun_insufficient_observations":"stun_unreachable";return true;
        }catch(Exception e){return false;}
    }
    synchronized void natPreparationFailed(){natPreparationFailure="stun_insufficient_observations";}
    synchronized void natHoleObserved() {natHole=true;natPreparationFailure="none";}
    synchronized void endActive() {for(Entry e:streams.values()) {if(e.phase.equals("open"))incomplete=true;e.phase="closed";}}
    synchronized boolean observe(String line) {
        String prefix="LINKGATE_METRIC ";
        if(!line.startsWith(prefix)||line.length()>2048) return false;
        try {
            String raw=line.substring(prefix.length()); StrictJson.validate(raw);
            JSONObject j=new JSONObject(raw); Set<String> keys=new HashSet<>(); j.keys().forEachRemaining(keys::add);
            if(!keys.equals(FIELDS)||integer(j,"v")!=1) return false;
            long id=integer(j,"id"),tx=integer(j,"tx"),rx=integer(j,"rx"),setup=integer(j,"setupMs"),elapsed=integer(j,"elapsedMs");
            String path=j.getString("path"),role=j.getString("role"),phase=j.getString("phase"),why=j.getString("reason");
            if(id<1||tx<0||rx<0||tx>(1L<<50)||rx>(1L<<50)||setup<0||elapsed<setup||elapsed>31536000000L||
                !Arrays.asList("visitor","publisher").contains(role)||!Arrays.asList("open","closed","failed").contains(phase)||
                !Arrays.asList("direct","relay","unknown").contains(path)||phase.equals("failed")!=path.equals("unknown")||
                (phase.equals("failed")&&(tx!=0||rx!=0))||!Arrays.asList("","direct_tunnel_unavailable","connection_setup_failed","direct_unavailable_relay_failed").contains(why)) return false;
            Entry old=streams.get(id);
            if(old!=null&&(!old.phase.equals("open")||!old.path.equals(path)||!old.role.equals(role)||old.tx>tx||old.rx>rx||old.elapsed>elapsed)) return false;
            if(old==null&&streams.size()>=8192) {incomplete=true;return false;}
            long dt=tx-(old==null?0:old.tx),dr=rx-(old==null?0:old.rx);
            if(path.equals("direct")){directTx+=dt;directRx+=dr;}
            if(path.equals("relay")){relayTx+=dt;relayRx+=dr;}
            setupMs=setup; if(!why.isEmpty()) reason=why;
            streams.put(id,new Entry(path,role,phase,tx,rx,elapsed)); return true;
        } catch(Exception invalid) {return false;}
    }
    private static long integer(JSONObject j,String key) throws Exception {
        Object value=j.get(key); if(!(value instanceof Integer)&&!(value instanceof Long)) throw new Exception("integer");
        return ((Number)value).longValue();
    }
    synchronized JSONObject snapshot() {
        long now=System.nanoTime()/1000000,bytes=directTx+directRx+relayTx+relayRx;
        if(samples.isEmpty()||now-samples.peekLast()[0]>=200) samples.addLast(new long[]{now,bytes});
        while(samples.size()>1&&now-samples.peekFirst()[0]>3000) samples.removeFirst();
        long[] first=samples.peekFirst(); long rate=now-first[0]<200?0:(bytes-first[1])*1000/(now-first[0]);
        Set<String> paths=new TreeSet<>(); int active=0;
        for(Entry e:streams.values()) if(e.phase.equals("open")){active++;paths.add(e.path);}
        JSONObject j=new JSONObject();
        try {
            j.put("schemaVersion",1).put("currentPath",paths.isEmpty()?"idle":incomplete?"incomplete":String.join("+",paths))
             .put("natHoleObserved",natHole).put("businessDirectObserved",directTx+directRx>0)
             .put("directTx",directTx).put("directRx",directRx).put("relayTx",relayTx).put("relayRx",relayRx)
             .put("activeConnections",active).put("setupMs",setupMs).put("bytesPerSecond",rate)
             .put("fallbackReason",reason).put("complete",!incomplete).put("natPreparationFailure",natPreparationFailure)
             .put("stunStatus",stunStatus).put("stunObservations",stunObservations).put("stunAttemptedEndpoints",stunAttemptedEndpoints).put("stunFallbackUsed",stunFallbackUsed);
        } catch(Exception impossible) {throw new IllegalStateException(impossible);}
        return j;
    }
    synchronized String summary() {
        JSONObject j=snapshot();
        return "当前路径："+j.optString("currentPath")+" · 活跃连接 "+j.optInt("activeConnections")+
            "\n直连 ↑"+directTx+" ↓"+directRx+" B；中继 ↑"+relayTx+" ↓"+relayRx+" B"+
            "\n最近吞吐 "+j.optLong("bytesPerSecond")+" B/s · 最近建立 "+setupMs+" ms"+
            "\nNAT 打洞："+(natHole?"观察到":"未观察到")+"；业务直连："+(directTx+directRx>0?"有字节证据":"无字节证据")+
            "\n回退/失败："+reason+" · 统计"+(incomplete?"不完整":"完整")+
            "\nSTUN："+stunStatus+"，观测 "+stunObservations+"/2，端点 "+stunAttemptedEndpoints+"，备用"+(stunFallbackUsed?"已尝试":"未使用")+
            "\nNAT 准备："+(natPreparationFailure.equals("none")?"未记录失败":natPreparationFailure.equals("stun_unreachable")?"未获取有效 STUN 响应":"STUN 映射观测不足，打洞尚未开始");
    }
}
