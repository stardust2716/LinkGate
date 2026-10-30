package dev.linkgate.contracts;

import java.math.BigInteger;
import java.time.Instant;
import java.util.Arrays;
import java.util.Map;

/** Immutable status; references are not authorization or public dial targets. */
public final class ConnectionStatus {
    private static final BigInteger MAX=new BigInteger("18446744073709551615");
    public final String connectionId,ownerId,deviceId,publisherAppId,serviceId,bootId,networkGeneration,flowState,selectedPath,businessPath,acceptedUtc,readyUtc,closedUtc,errorCode;
    public final BigInteger directTx,directRx,relayTx,relayRx;public final boolean complete;public final Integer setupMs;
    private final String encoded;
    public static ConnectionStatus parse(byte[] input){try{return new ConnectionStatus(WireJson.read(input,16384));}catch(ContractException error){throw error;}catch(RuntimeException error){throw new ContractException();}}
    private ConnectionStatus(Map<String,Object> value){
        WireJson.fields(value,"connectionId","serviceRef","bootId","networkGeneration","flowState","selectedPath","businessPath","directTx","directRx","relayTx","relayRx","complete","error","acceptedUtc","readyUtc","closedUtc","setupMs");
        connectionId=uuid(value.get("connectionId"));bootId=uuid(value.get("bootId"));Map<String,Object> service=WireJson.object(value.get("serviceRef"));WireJson.fields(service,"ownerId","deviceId","publisherAppId","serviceId");ownerId=WireJson.key(service.get("ownerId"));deviceId=WireJson.key(service.get("deviceId"));publisherAppId=text(service.get("publisherAppId"),128,"[a-z][a-z0-9]*(?:[.-][a-z0-9]+)+");serviceId=text(service.get("serviceId"),32,"[a-z][a-z0-9-]{0,31}");
        networkGeneration=decimal(value.get("networkGeneration")).toString();if(networkGeneration.equals("0"))throw new ContractException();flowState=choice(value.get("flowState"),"pending","attaching","open","draining","closed","failed");selectedPath=choice(value.get("selectedPath"),"none","direct","relay");businessPath=choice(value.get("businessPath"),"none","direct","relay");
        directTx=decimal(value.get("directTx"));directRx=decimal(value.get("directRx"));relayTx=decimal(value.get("relayTx"));relayRx=decimal(value.get("relayRx"));if(!(value.get("complete") instanceof Boolean))throw new ContractException();complete=(Boolean)value.get("complete");
        acceptedUtc=time(value.get("acceptedUtc"));readyUtc=time(value.get("readyUtc"));closedUtc=time(value.get("closedUtc"));setupMs=value.get("setupMs")==null?null:(int)WireJson.integer(value.get("setupMs"),0,60000);
        if(value.get("error")==null)errorCode=null;else{Map<String,Object> error=WireJson.object(value.get("error"));WireJson.fields(error,"code","stage","retryable","message","details");errorCode=text(error.get("code"),64,"[A-Z][A-Z0-9_]{0,63}");if(!"connection".equals(error.get("stage"))||!Boolean.FALSE.equals(error.get("retryable"))||!"Connection ended.".equals(error.get("message"))||!WireJson.object(error.get("details")).isEmpty())throw new ContractException();}
        boolean payload=directTx.signum()+directRx.signum()+relayTx.signum()+relayRx.signum()>0,terminal=flowState.equals("closed")||flowState.equals("failed");
        if((readyUtc==null)!=(setupMs==null)||(selectedPath.equals("none"))!=(readyUtc==null)||terminal!=(closedUtc!=null)||!terminal&&errorCode!=null||flowState.equals("failed")&&errorCode==null||complete&&(!flowState.equals("closed")||errorCode!=null||readyUtc==null))throw new ContractException();
        if(selectedPath.equals("direct")&&(relayTx.signum()!=0||relayRx.signum()!=0)||selectedPath.equals("relay")&&(directTx.signum()!=0||directRx.signum()!=0)||payload&&!businessPath.equals(selectedPath)||!payload&&!businessPath.equals("none")||selectedPath.equals("none")&&payload)throw new ContractException();
        if(flowState.equals("pending")&&(!selectedPath.equals("none")||payload)||flowState.equals("attaching")&&(readyUtc==null||payload)||Arrays.asList("open","draining").contains(flowState)&&readyUtc==null)throw new ContractException();
        if(acceptedUtc!=null&&readyUtc!=null&&Instant.parse(readyUtc).isBefore(Instant.parse(acceptedUtc))||acceptedUtc!=null&&closedUtc!=null&&Instant.parse(closedUtc).isBefore(Instant.parse(acceptedUtc))||readyUtc!=null&&closedUtc!=null&&Instant.parse(closedUtc).isBefore(Instant.parse(readyUtc)))throw new ContractException();
        encoded=new String(WireJson.write(value,16384),java.nio.charset.StandardCharsets.UTF_8);
    }
    private static String uuid(Object raw){String value=WireJson.text(raw,36);if(!WireJson.uuid(value).toString().equals(value))throw new ContractException();return value;}
    private static String text(Object raw,int limit,String pattern){String value=WireJson.text(raw,limit);if(!value.matches(pattern))throw new ContractException();return value;}
    private static String choice(Object raw,String... allowed){String value=WireJson.text(raw,32);if(!Arrays.asList(allowed).contains(value))throw new ContractException();return value;}
    private static BigInteger decimal(Object raw){String value=WireJson.text(raw,20);if(!value.matches("0|[1-9][0-9]*"))throw new ContractException();BigInteger result=new BigInteger(value);if(result.compareTo(MAX)>0)throw new ContractException();return result;}
    private static String time(Object raw){if(raw==null)return null;String value=text(raw,24,"[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\\.[0-9]{3}Z");if(value.startsWith("0000-"))throw new ContractException();java.time.LocalDateTime.parse(value,java.time.format.DateTimeFormatter.ofPattern("uuuu-MM-dd'T'HH:mm:ss.SSS'Z'").withResolverStyle(java.time.format.ResolverStyle.STRICT));return value;}
    public String toJson(){return encoded;}
    public Map<String,Object> wire(){return WireJson.read(encoded.getBytes(java.nio.charset.StandardCharsets.UTF_8),16384);}
}
