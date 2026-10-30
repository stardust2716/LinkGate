package dev.linkgate.contracts;

import java.math.BigInteger;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.HashSet;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Set;

/** Public API for strict, side-effect-free contract parsing. */
public final class ManifestContract {
    public static final int MAX_MANIFEST_BYTES=48*1024,MAX_POLICY_BYTES=8*1024;
    private static final BigInteger UINT64_MAX=new BigInteger("18446744073709551615");
    private ManifestContract(){}

    public static ApplicationManifest parse(byte[] utf8){
        try{return manifest(object(StrictJson.parse(utf8,MAX_MANIFEST_BYTES)));}
        catch(ContractException e){throw e;}
        catch(RuntimeException e){throw new ContractException();}
    }
    public static ConnectionPolicy normalizePolicy(byte[] utf8){
        try{return policy(object(StrictJson.parse(utf8,MAX_POLICY_BYTES)));}
        catch(ContractException e){throw e;}
        catch(RuntimeException e){throw new ContractException();}
    }
    private static ApplicationManifest manifest(Map<String,Object> m){
        fields(m,"manifestVersion","applicationId","displayName","exports","imports");
        if(integer(required(m,"manifestVersion"),1,1)!=1)throw invalid();
        String app=text(required(m,"applicationId"),3,128);
        if(!app.matches("[a-z][a-z0-9]*(?:[.-][a-z0-9]+)+"))throw invalid();
        String name=label(required(m,"displayName"));List<Object> exports=array(required(m,"exports")),imports=array(required(m,"imports"));
        if(exports.size()>16||imports.size()>16||exports.isEmpty()&&imports.isEmpty())throw invalid();
        List<ApplicationManifest.Export> ex=new ArrayList<>();List<ApplicationManifest.Import> im=new ArrayList<>();Set<String> ids=new HashSet<>(),aliases=new HashSet<>();
        for(Object raw:exports){
            Map<String,Object> e=object(raw);fields(e,"serviceId","label","protocol","localTarget","logicalAuthority","healthCheck");
            String id=identifier(required(e,"serviceId"));if(!ids.add(id))throw invalid();String protocol=protocol(required(e,"protocol"));
            Map<String,Object> target=object(required(e,"localTarget"));fields(target,"host","port");
            if(!"127.0.0.1".equals(required(target,"host")))throw invalid();int port=integer(required(target,"port"),1024,65535);
            String authority=optionalAuthority(e,protocol);ApplicationManifest.HealthCheck health=e.containsKey("healthCheck")?health(object(e.get("healthCheck"))):null;
            ex.add(new ApplicationManifest.Export(id,label(required(e,"label")),protocol,port,authority,health));
        }
        for(Object raw:imports){
            Map<String,Object> i=object(raw);fields(i,"alias","protocol","endpointMode","logicalAuthority","connectionPolicy");
            String alias=identifier(required(i,"alias"));if(!aliases.add(alias))throw invalid();String protocol=protocol(required(i,"protocol"));
            String endpoint=text(required(i,"endpointMode"),1,16);if(!endpoint.equals("brokered")&&!endpoint.equals("loopback"))throw invalid();
            im.add(new ApplicationManifest.Import(alias,protocol,endpoint,optionalAuthority(i,protocol),policy(object(required(i,"connectionPolicy")))));
        }
        return new ApplicationManifest(app,name,ex,im);
    }
    private static ConnectionPolicy policy(Map<String,Object> p){
        fields(p,"mode","connectTimeoutMs","directWaitMs","allowRelay","prewarm","prewarmTimeoutMs","prewarmIdleTimeoutMs","relayBudgetBytes","relayWarningBytes","relayBudgetAction");
        String mode=text(required(p,"mode"),1,32);if(!mode.equals("fast-connect")&&!mode.equals("direct-first")&&!mode.equals("direct-only"))throw invalid();
        int total=p.containsKey("connectTimeoutMs")?integer(p.get("connectTimeoutMs"),1000,60000):15000;
        int defaultWait=mode.equals("fast-connect")?1500:mode.equals("direct-first")?8000:total;
        int wait=p.containsKey("directWaitMs")?integer(p.get("directWaitMs"),0,60000):Math.min(defaultWait,total);if(wait>total)throw invalid();
        boolean relay=p.containsKey("allowRelay")?bool(p.get("allowRelay")):true;
        if(mode.equals("direct-only")&&(!p.containsKey("allowRelay")||relay||wait!=total))throw invalid();
        boolean prewarm=p.containsKey("prewarm")?bool(p.get("prewarm")):false;
        int warm=p.containsKey("prewarmTimeoutMs")?integer(p.get("prewarmTimeoutMs"),1000,60000):8000;
        int idle=p.containsKey("prewarmIdleTimeoutMs")?integer(p.get("prewarmIdleTimeoutMs"),1000,600000):60000;
        String budget=decimal(p.get("relayBudgetBytes")),warning=decimal(p.get("relayWarningBytes"));
        String action=p.containsKey("relayBudgetAction")?text(p.get("relayBudgetAction"),1,32):null;
        if(budget==null&&action!=null||budget!=null&&!"stop-relay".equals(action))throw invalid();
        if(budget!=null&&warning!=null&&new BigInteger(warning).compareTo(new BigInteger(budget))>0)throw invalid();
        return new ConnectionPolicy(mode,total,wait,relay,prewarm,warm,idle,budget,warning,action);
    }
    private static String decimal(Object raw){
        if(raw==null)return null;String value=text(raw,1,20);
        if(!value.matches("0|[1-9][0-9]*")||new BigInteger(value).compareTo(UINT64_MAX)>0)throw invalid();return value;
    }
    private static ApplicationManifest.HealthCheck health(Map<String,Object> h){
        String type=text(required(h,"type"),1,8);
        if(type.equals("tcp")){fields(h,"type");return new ApplicationManifest.HealthCheck(type,null,new ArrayList<Integer>());}
        if(!type.equals("http"))throw invalid();fields(h,"type","path","expectedStatus");
        String path=text(required(h,"path"),1,1024);
        if(!path.startsWith("/")||path.startsWith("//")||path.indexOf('#')>=0)throw invalid();
        for(int n=0;n<path.length();){int cp=path.codePointAt(n);if(whitespace(cp))throw invalid();n+=Character.charCount(cp);}
        List<Object> input=array(required(h,"expectedStatus"));if(input.isEmpty()||input.size()>10)throw invalid();
        List<Integer> statuses=new ArrayList<>();Set<Integer> seen=new HashSet<>();
        for(Object raw:input){int status=integer(raw,100,599);if(!seen.add(status))throw invalid();statuses.add(status);}
        return new ApplicationManifest.HealthCheck(type,path,statuses);
    }
    private static String optionalAuthority(Map<String,Object> m,String protocol){
        if(!m.containsKey("logicalAuthority")){if(protocol.equals("https")||protocol.equals("wss"))throw invalid();return null;}
        String input=text(m.get("logicalAuthority"),1,259);int colon=input.indexOf(':');String host=colon<0?input:input.substring(0,colon),suffix="";
        if(colon>=0){String p=input.substring(colon+1);if(!p.matches("[1-9][0-9]{0,4}"))throw invalid();int port=Integer.parseInt(p);if(port>65535)throw invalid();suffix=":"+p;}
        if(host.length()<1||host.length()>253||!host.matches("[A-Za-z0-9.-]+")||host.endsWith("."))throw invalid();
        String[] labels=host.split("\\.",-1);
        for(String part:labels)if(part.length()<1||part.length()>63||!part.matches("[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?"))throw invalid();
        if(host.matches("(?i)(?:0x[0-9a-f]+|[0-9]+)(?:\\.(?:0x[0-9a-f]+|[0-9]+))*"))throw invalid();
        return host.toLowerCase(Locale.ROOT)+suffix;
    }
    private static String protocol(Object raw){String p=text(raw,1,5);if(!Arrays.asList("tcp","http","https","ws","wss").contains(p))throw invalid();return p;}
    private static String identifier(Object raw){String s=text(raw,1,32);if(!s.matches("[a-z][a-z0-9-]{0,31}"))throw invalid();return s;}
    private static String label(Object raw){String s=text(raw,1,64);boolean blank=true;for(int i=0;i<s.length();){int cp=s.codePointAt(i);if(!whitespace(cp))blank=false;i+=Character.charCount(cp);}if(blank)throw invalid();return s;}
    private static boolean whitespace(int cp){return Character.isWhitespace(cp)||Character.isSpaceChar(cp)||cp==0x85;}
    private static String text(Object raw,int min,int max){
        if(!(raw instanceof String))throw invalid();String s=(String)raw;int count=StrictJson.scalars(s);if(count<min||count>max)throw invalid();
        for(int i=0;i<s.length();i++){char c=s.charAt(i);if(c<0x20||c==0x7f)throw invalid();}return s;
    }
    private static int integer(Object raw,int min,int max){
        if(!(raw instanceof StrictJson.NumberToken))throw invalid();String lexical=((StrictJson.NumberToken)raw).raw;
        if(!lexical.matches("-?(?:0|[1-9][0-9]*)"))throw invalid();
        try{long value=Long.parseLong(lexical);if(value<min||value>max)throw invalid();return (int)value;}catch(NumberFormatException e){throw invalid();}
    }
    private static boolean bool(Object raw){if(!(raw instanceof Boolean))throw invalid();return (Boolean)raw;}
    private static Object required(Map<String,Object> m,String key){if(!m.containsKey(key))throw invalid();return m.get(key);}
    @SuppressWarnings("unchecked") private static Map<String,Object> object(Object raw){if(!(raw instanceof Map<?,?>))throw invalid();return (Map<String,Object>)raw;}
    @SuppressWarnings("unchecked") private static List<Object> array(Object raw){if(!(raw instanceof List<?>))throw invalid();return (List<Object>)raw;}
    private static void fields(Map<String,Object> m,String... allowed){Set<String> set=new HashSet<>(Arrays.asList(allowed));for(String key:m.keySet())if(!set.contains(key))throw invalid();}
    private static ContractException invalid(){return new ContractException();}
}
