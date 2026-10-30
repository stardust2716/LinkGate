package dev.linkgate.contracts;

import java.util.ArrayList;
import java.util.Collections;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

/** Immutable public declarations; contains neither credentials nor user grants. */
public final class ApplicationManifest {
    public final int manifestVersion=1;
    public final String applicationId,displayName;
    public final List<Export> exports;
    public final List<Import> imports;
    ApplicationManifest(String app,String name,List<Export> exports,List<Import> imports){
        applicationId=app;displayName=name;this.exports=immutable(exports);this.imports=immutable(imports);
    }
    private static <T> List<T> immutable(List<T> values){return Collections.unmodifiableList(new ArrayList<>(values));}
    public static final class HealthCheck {
        public final String type,path;
        public final List<Integer> expectedStatus;
        HealthCheck(String type,String path,List<Integer> statuses){this.type=type;this.path=path;expectedStatus=immutable(statuses);}
        Map<String,Object> normalized(){Map<String,Object> out=new LinkedHashMap<>();out.put("type",type);if(path!=null){out.put("path",path);out.put("expectedStatus",expectedStatus);}return out;}
    }
    public static final class Export {
        public final String serviceId,label,protocol,logicalAuthority;
        public final int localPort;
        public final HealthCheck healthCheck;
        Export(String id,String label,String protocol,int port,String authority,HealthCheck health){serviceId=id;this.label=label;this.protocol=protocol;localPort=port;logicalAuthority=authority;healthCheck=health;}
        Map<String,Object> normalized(){
            Map<String,Object> out=new LinkedHashMap<>();out.put("serviceId",serviceId);out.put("label",label);out.put("protocol",protocol);
            Map<String,Object> target=new LinkedHashMap<>();target.put("host","127.0.0.1");target.put("port",localPort);out.put("localTarget",target);
            if(logicalAuthority!=null)out.put("logicalAuthority",logicalAuthority);if(healthCheck!=null)out.put("healthCheck",healthCheck.normalized());return out;
        }
    }
    public static final class Import {
        public final String alias,protocol,endpointMode,logicalAuthority;
        public final ConnectionPolicy connectionPolicy;
        Import(String alias,String protocol,String endpointMode,String authority,ConnectionPolicy policy){this.alias=alias;this.protocol=protocol;this.endpointMode=endpointMode;logicalAuthority=authority;connectionPolicy=policy;}
        Map<String,Object> normalized(){Map<String,Object> out=new LinkedHashMap<>();out.put("alias",alias);out.put("protocol",protocol);out.put("endpointMode",endpointMode);if(logicalAuthority!=null)out.put("logicalAuthority",logicalAuthority);out.put("connectionPolicy",connectionPolicy.normalized());return out;}
    }
    public String toJson(){
        Map<String,Object> out=new LinkedHashMap<>();out.put("manifestVersion",manifestVersion);out.put("applicationId",applicationId);out.put("displayName",displayName);
        List<Object> e=new ArrayList<>(),i=new ArrayList<>();for(Export x:exports)e.add(x.normalized());for(Import x:imports)i.add(x.normalized());out.put("exports",e);out.put("imports",i);return StrictJson.write(out);
    }
}
