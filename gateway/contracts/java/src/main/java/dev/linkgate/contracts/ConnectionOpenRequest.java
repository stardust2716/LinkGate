package dev.linkgate.contracts;

import java.math.BigInteger;
import java.nio.charset.StandardCharsets;
import java.util.Map;

/** Reference validation is independent from caller/grant resolution. */
public final class ConnectionOpenRequest {
    public final String applicationRef,importAlias,deviceId,publisherAppId,remoteServiceId;
    public final ConnectionPolicy connectionPolicy;
    private ConnectionOpenRequest(String application,String alias,String device,String publisher,String service,ConnectionPolicy policy){applicationRef=application;importAlias=alias;deviceId=device;publisherAppId=publisher;remoteServiceId=service;connectionPolicy=policy;}
    public static ConnectionOpenRequest parse(byte[] input){
        try {
            StrictJson.ConnectionDocument document=StrictJson.parseConnection(input,16*1024);Map<String,Object> fields=WireJson.object(document.value);WireJson.fields(fields,"applicationRef","importAlias","deviceId","publisherAppId","remoteServiceId","connectionPolicy");
            String application=WireJson.uuid(fields.get("applicationRef")).toString(),alias=text(fields.get("importAlias"),32,"[a-z][a-z0-9-]{0,31}"),device=WireJson.key(fields.get("deviceId")),publisher=text(fields.get("publisherAppId"),128,"[a-z][a-z0-9]*(?:[.-][a-z0-9]+)+"),service=text(fields.get("remoteServiceId"),32,"[a-z][a-z0-9-]{0,31}");
            ConnectionPolicy policy=ManifestContract.normalizePolicy(document.policySource.getBytes(StandardCharsets.UTF_8));return new ConnectionOpenRequest(application,alias,device,publisher,service,policy);
        }catch(ContractException error){throw error;}catch(RuntimeException error){throw new ContractException();}
    }
    private static String text(Object value,int max,String pattern){String text=WireJson.text(value,max);if(!text.matches(pattern))throw new ContractException();return text;}
    private static boolean bounded(String request,String limit){return limit==null||request!=null&&new BigInteger(request).compareTo(new BigInteger(limit))<=0;}
    public boolean withinApproved(ConnectionPolicy approved){
        if(approved==null)return false;ConnectionPolicy request=connectionPolicy;
        return request.connectTimeoutMs<=approved.connectTimeoutMs&&(!request.allowRelay||approved.allowRelay&&request.directWaitMs>=Math.min(approved.directWaitMs,request.connectTimeoutMs))
            &&(!request.prewarm||approved.prewarm&&request.prewarmTimeoutMs<=approved.prewarmTimeoutMs&&request.prewarmIdleTimeoutMs<=approved.prewarmIdleTimeoutMs)
            &&bounded(request.relayBudgetBytes,approved.relayBudgetBytes)&&bounded(request.relayWarningBytes,approved.relayWarningBytes);
    }
    public String toJson(){return StrictJson.write(WireJson.map("applicationRef",applicationRef,"importAlias",importAlias,"deviceId",deviceId,"publisherAppId",publisherAppId,"remoteServiceId",remoteServiceId,"connectionPolicy",connectionPolicy.normalized()));}
}
