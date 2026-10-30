package dev.linkgate.contracts;

import java.util.LinkedHashMap;
import java.util.Map;

/** Immutable validated policy; validation does not apply a running transport policy. */
public final class ConnectionPolicy {
    public final String mode,relayBudgetBytes,relayWarningBytes,relayBudgetAction;
    public final int connectTimeoutMs,directWaitMs,prewarmTimeoutMs,prewarmIdleTimeoutMs;
    public final boolean allowRelay,prewarm;
    ConnectionPolicy(String mode,int total,int wait,boolean relay,boolean prewarm,int warm,int idle,String budget,String warning,String action){
        this.mode=mode;connectTimeoutMs=total;directWaitMs=wait;allowRelay=relay;this.prewarm=prewarm;
        prewarmTimeoutMs=warm;prewarmIdleTimeoutMs=idle;relayBudgetBytes=budget;relayWarningBytes=warning;relayBudgetAction=action;
    }
    Map<String,Object> normalized(){
        Map<String,Object> m=new LinkedHashMap<>();m.put("mode",mode);m.put("connectTimeoutMs",connectTimeoutMs);m.put("directWaitMs",directWaitMs);
        m.put("allowRelay",allowRelay);m.put("prewarm",prewarm);m.put("prewarmTimeoutMs",prewarmTimeoutMs);m.put("prewarmIdleTimeoutMs",prewarmIdleTimeoutMs);
        m.put("relayBudgetBytes",relayBudgetBytes);m.put("relayWarningBytes",relayWarningBytes);if(relayBudgetAction!=null)m.put("relayBudgetAction",relayBudgetAction);return m;
    }
    public String toJson(){return StrictJson.write(normalized());}
}
