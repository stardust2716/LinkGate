package dev.linkgate.authority;

import dev.linkgate.contracts.WireJson;
import java.math.BigInteger;
import java.util.Map;
import java.util.Objects;

public final class AuthoritySnapshot {
    static final BigInteger MAX=new BigInteger("18446744073709551615");
    public static final class Target {
        public final String publisherDevice,publisherApp,service;
        public Target(String device,String app,String service){publisherDevice=WireJson.key(device);publisherApp=app(app);this.service=service(service);}
        @Override public boolean equals(Object value){if(!(value instanceof Target))return false;Target other=(Target)value;return publisherDevice.equals(other.publisherDevice)&&publisherApp.equals(other.publisherApp)&&service.equals(other.service);}
        @Override public int hashCode(){return Objects.hash(publisherDevice,publisherApp,service);}
    }
    public static final class Scope {
        public final String visitorDevice,visitorApp;public final Target target;
        public Scope(String device,String app,Target target){visitorDevice=WireJson.key(device);visitorApp=app(app);this.target=Objects.requireNonNull(target);}
        @Override public boolean equals(Object value){if(!(value instanceof Scope))return false;Scope other=(Scope)value;return visitorDevice.equals(other.visitorDevice)&&visitorApp.equals(other.visitorApp)&&target.equals(other.target);}
        @Override public int hashCode(){return Objects.hash(visitorDevice,visitorApp,target);}
    }
    public final Scope scope;
    public final BigInteger visitorRevision,publisherRevision,serviceRevision,grantRevision;
    public final String protocol,authority;
    private AuthoritySnapshot(Scope scope,BigInteger visitor,BigInteger publisher,BigInteger service,BigInteger grant,String protocol,String authority){this.scope=scope;visitorRevision=visitor;publisherRevision=publisher;serviceRevision=service;grantRevision=grant;this.protocol=protocol;this.authority=authority;}
    public static AuthoritySnapshot parse(byte[] json)throws LeaseRejectedException {
        try{return parse(WireJson.read(json,65536));}catch(RuntimeException failure){throw new LeaseRejectedException();}
    }
    public static AuthoritySnapshot parse(Map<String,Object> root)throws LeaseRejectedException {
        try {
            WireJson.fields(root,"grant","service");Map<String,Object> grant=WireJson.object(root.get("grant")),service=WireJson.object(root.get("service"));WireJson.fields(grant,"scope","revision","visitorRevision","publisherRevision","serviceRevision","active");WireJson.fields(service,"scope","revision","publisherRevision","active","protocol","authority");
            if(!Boolean.TRUE.equals(grant.get("active"))||!Boolean.TRUE.equals(service.get("active")))throw new IllegalArgumentException();Scope scope=scope(grant.get("scope"));Target target=target(service.get("scope"));BigInteger visitor=counter(grant.get("visitorRevision"),true),publisher=counter(grant.get("publisherRevision"),true),revision=counter(service.get("revision"),true),grantRevision=counter(grant.get("revision"),true);
            if(!scope.target.equals(target)||!revision.equals(counter(grant.get("serviceRevision"),true))||!publisher.equals(counter(service.get("publisherRevision"),true)))throw new IllegalArgumentException();String protocol=WireJson.text(service.get("protocol"),5),authority=text(service.get("authority"),259);
            if(!java.util.Arrays.asList("tcp","http","https","ws","wss").contains(protocol)||((protocol.equals("https")||protocol.equals("wss"))&&authority.isEmpty()))throw new IllegalArgumentException();return new AuthoritySnapshot(scope,visitor,publisher,revision,grantRevision,protocol,authority);
        }catch(RuntimeException failure){throw new LeaseRejectedException();}
    }
    static String text(Object value,int limit){if(!(value instanceof String))throw new IllegalArgumentException();String text=(String)value;if(text.length()>limit||text.codePoints().anyMatch(Character::isISOControl))throw new IllegalArgumentException();return text;}
    static String app(String value){if(value==null||value.length()>128||!value.matches("[a-z][a-z0-9]*(?:[.-][a-z0-9]+)+"))throw new IllegalArgumentException();return value;}
    static String service(String value){if(value==null||!value.matches("[a-z][a-z0-9-]{0,31}"))throw new IllegalArgumentException();return value;}
    static BigInteger counter(Object value,boolean active){String text=WireJson.text(value,20);if(!text.matches("[1-9][0-9]{0,19}"))throw new IllegalArgumentException();BigInteger number=new BigInteger(text);if(number.compareTo(MAX)>0||(active&&number.equals(MAX)))throw new IllegalArgumentException();return number;}
    static java.util.UUID uuid(Object value){java.util.UUID id=WireJson.uuid(value);if(!id.toString().equals(value))throw new IllegalArgumentException();return id;}
    static Target target(Object value){Map<String,Object> fields=WireJson.object(value);WireJson.fields(fields,"publisherDevice","publisherApp","service");return new Target(WireJson.text(fields.get("publisherDevice"),64),WireJson.text(fields.get("publisherApp"),128),WireJson.text(fields.get("service"),32));}
    static Scope scope(Object value){Map<String,Object> fields=WireJson.object(value);WireJson.fields(fields,"visitorDevice","visitorApp","target");return new Scope(WireJson.text(fields.get("visitorDevice"),64),WireJson.text(fields.get("visitorApp"),128),target(fields.get("target")));}
}
