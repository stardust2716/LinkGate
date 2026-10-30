package dev.linkgate.agent;

import dev.linkgate.contracts.ConnectionStatus;
import dev.linkgate.contracts.WireJson;
import java.math.BigInteger;
import java.time.Instant;
import java.time.ZoneOffset;
import java.time.format.DateTimeFormatter;
import java.util.Map;
import java.util.concurrent.TimeUnit;
import java.util.function.LongSupplier;

public final class ConnectionMeter {
    private static final BigInteger MAX=new BigInteger("18446744073709551615");
    private static final DateTimeFormatter UTC=DateTimeFormatter.ofPattern("uuuu-MM-dd'T'HH:mm:ss.SSS'Z'").withZone(ZoneOffset.UTC);
    public static final class Snapshot {public final long sequence;public final ConnectionStatus status;private Snapshot(long sequence,ConnectionStatus status){this.sequence=sequence;this.status=status;}}
    private final String reference,owner,device,publisher,service,boot,generation;private final long admitted,accepted;private final LongSupplier clock;
    private long lastNanos,elapsed,sequence=1;private String state="pending",selected="none",business="none",error;private Long ready,closed;private Integer setup;private boolean complete,clockBad,outFin,inFin;private BigInteger tx=BigInteger.ZERO,rx=BigInteger.ZERO;
    public ConnectionMeter(String reference,String owner,String device,String publisher,String service,String boot,String generation,long accepted,long admitted,LongSupplier clock){this.reference=reference;this.owner=owner;this.device=device;this.publisher=publisher;this.service=service;this.boot=boot;this.generation=generation;this.accepted=accepted;this.admitted=admitted;this.clock=clock;lastNanos=admitted;snapshot();}
    private long tick(){long now=clock.getAsLong();if(now<lastNanos){clockBad=true;return elapsed;}lastNanos=now;elapsed=TimeUnit.NANOSECONDS.toMillis(now-admitted);return elapsed;}
    public synchronized boolean clockValid(){tick();return !clockBad;}
    private boolean terminal(){return closed!=null;}
    public synchronized void ready(String path,long setupMs){if(terminal())return;if(!state.equals("pending")||!path.equals("direct")&&!path.equals("relay")||setupMs<0||setupMs>60000)throw new IllegalStateException("Invalid connection transition.");tick();if(clockBad)throw new IllegalStateException("CLOCK_UNTRUSTED");selected=path;ready=Math.addExact(accepted,elapsed);setup=(int)setupMs;state="attaching";sequence++;}
    public synchronized void attached(){if(terminal())return;if(!state.equals("attaching"))throw new IllegalStateException("Invalid connection transition.");state="open";sequence++;}
    public synchronized void draining(){if(terminal())return;if(state.equals("open")){state="draining";sequence++;}}
    public synchronized void fin(boolean outbound){if(terminal())return;if(!state.equals("open")&&!state.equals("draining"))throw new IllegalStateException("FIN before attachment.");if(outbound){if(outFin)return;outFin=true;}else{if(inFin)return;inFin=true;}draining();sequence++;}
    public synchronized boolean bytes(boolean outbound,long amount){if(amount<0)throw new IllegalArgumentException("Nonnegative byte count required.");if(terminal())return false;if(!state.equals("open")&&!state.equals("draining"))throw new IllegalStateException("Payload before attachment.");if(amount==0)return true;BigInteger value=(outbound?tx:rx).add(BigInteger.valueOf(amount));if(value.compareTo(MAX)>0){end(false,"COUNTER_OVERFLOW");return false;}if(outbound)tx=value;else rx=value;business=selected;sequence++;return true;}
    public synchronized void end(boolean clean,String code){if(terminal())return;tick();if(clockBad){clean=false;code="CLOCK_UNTRUSTED";}if(clean&&(!outFin||!inFin)||ready==null&&code==null){clean=false;code="CONNECTION_CLOSED";}complete=clean&&ready!=null&&code==null;error=code;closed=Math.addExact(accepted,elapsed);state=code==null||code.equals("CONNECTION_CLOSED")||code.equals("GATEWAY_STOPPED")?"closed":"failed";sequence++;}
    public synchronized Snapshot snapshot(){
        tick();Map<String,Object> value=WireJson.map("connectionId",reference,"serviceRef",WireJson.map("ownerId",owner,"deviceId",device,"publisherAppId",publisher,"serviceId",service),"bootId",boot,"networkGeneration",generation,"flowState",state,"selectedPath",selected,"businessPath",business,"directTx",selected.equals("direct")?tx.toString():"0","directRx",selected.equals("direct")?rx.toString():"0","relayTx",selected.equals("relay")?tx.toString():"0","relayRx",selected.equals("relay")?rx.toString():"0","complete",complete,"error",error==null?null:WireJson.map("code",error,"stage","connection","retryable",false,"message","Connection ended.","details",WireJson.map()),"acceptedUtc",UTC.format(Instant.ofEpochMilli(accepted)),"readyUtc",ready==null?null:UTC.format(Instant.ofEpochMilli(ready)),"closedUtc",closed==null?null:UTC.format(Instant.ofEpochMilli(closed)),"setupMs",setup);
        return new Snapshot(sequence,ConnectionStatus.parse(WireJson.write(value,16384)));
    }
    public static ConnectionStatus recovered(ConnectionStatus previous,String code,long utc){Map<String,Object> value=previous.wire();value.put("flowState",code.equals("CONNECTION_CLOSED")||code.equals("GATEWAY_STOPPED")?"closed":"failed");value.put("complete",false);long floor=Instant.parse(previous.readyUtc!=null?previous.readyUtc:previous.acceptedUtc).toEpochMilli();value.put("closedUtc",UTC.format(Instant.ofEpochMilli(Math.max(utc,floor))));value.put("error",WireJson.map("code",code,"stage","connection","retryable",false,"message","Connection ended.","details",WireJson.map()));return ConnectionStatus.parse(WireJson.write(value,16384));}
}
