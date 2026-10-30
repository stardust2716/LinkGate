package dev.linkgate.contracts;

import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.Paths;
import java.util.ArrayList;
import java.util.Base64;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

/** Standalone test runner; no Android or third-party dependency is required. */
public final class ConformanceMain {
    private ConformanceMain(){}
    public static void main(String[] args)throws Exception{
        Path vectors=null,output=null;
        for(int n=0;n<args.length;n++){
            if(n+1>=args.length)throw new IllegalArgumentException("Missing runner argument");
            String flag=args[n++];if(flag.equals("--vectors"))vectors=Paths.get(args[n]);else if(flag.equals("--output"))output=Paths.get(args[n]);else throw new IllegalArgumentException("Unknown runner argument");
        }
        if(vectors==null||output==null)throw new IllegalArgumentException("Use --vectors PATH --output PATH");
        Map<String,Object> corpus=object(StrictJson.parseText(new String(Files.readAllBytes(vectors),StandardCharsets.UTF_8),64,2000000));
        List<Object> cases=list(corpus.get("cases"));List<Object> results=new ArrayList<>();int passed=0;List<String> failed=new ArrayList<>();
        for(Object raw:cases){
            Map<String,Object> test=object(raw);String id=(String)test.get("id"),kind=(String)test.get("kind");
            if(!kind.equals("manifest")&&!kind.equals("policy")&&!kind.equals("connection-open")&&!kind.equals("connection-status")&&!kind.equals("connection-operation"))throw new IllegalStateException("Unknown corpus kind");
            byte[] input=Base64.getDecoder().decode((String)test.get("inputUtf8Base64"));
            Map<String,Object> actual=new LinkedHashMap<>();actual.put("id",id);
            try{
                String normalized;
                if(kind.equals("connection-status")){ConnectionStatus status=ConnectionStatus.parse(input);normalized=status.toJson();status.wire().clear();if(!status.toJson().equals(normalized))throw new IllegalStateException("Mutable status wire.");}
                else if(kind.equals("connection-operation")){ConnectionReply.Operation operation=ConnectionReply.parseOperation(input,java.util.UUID.fromString((String)test.get("expectedOperation")),(String)test.get("expectedKind"));normalized=operation.toJson();operation.wire().clear();if(!operation.toJson().equals(normalized))throw new IllegalStateException("Mutable operation wire.");}
                else if(kind.equals("connection-open")){
                    ConnectionOpenRequest request=ConnectionOpenRequest.parse(input);ConnectionPolicy approved=ManifestContract.normalizePolicy(Base64.getDecoder().decode((String)test.get("approvedPolicyUtf8Base64")));
                    normalized=StrictJson.write(WireJson.map("request",StrictJson.parseText(request.toJson(),16,10000),"withinApproved",request.withinApproved(approved)));
                }else normalized=kind.equals("manifest")?ManifestContract.parse(input).toJson():ManifestContract.normalizePolicy(input).toJson();
                actual.put("accepted",Boolean.TRUE);actual.put("normalized",StrictJson.parseText(normalized,16,10000));
            }catch(ContractException e){
                if(!e.code().equals("INVALID_REQUEST")||!e.getMessage().equals("INVALID_REQUEST")||e.getCause()!=null)throw new IllegalStateException("Unsafe validation error");
                actual.put("accepted",Boolean.FALSE);actual.put("errorCode",e.code());
            }
            boolean same=test.get("accepted").equals(actual.get("accepted"));
            if(Boolean.TRUE.equals(actual.get("accepted")))same=same&&equivalent(test.get("normalized"),actual.get("normalized"));
            else same=same&&test.get("errorCode").equals(actual.get("errorCode"));
            if(same)passed++;else failed.add(id);results.add(actual);
        }
        immutableChecks();
        Map<String,Object> result=new LinkedHashMap<>();result.put("contractVersion",1);result.put("total",cases.size());result.put("passed",passed);result.put("cases",results);
        Path parent=output.toAbsolutePath().getParent();if(parent!=null)Files.createDirectories(parent);
        Files.write(output,(StrictJson.write(result)+"\n").getBytes(StandardCharsets.UTF_8));
        System.out.println("Java contract conformance "+passed+"/"+cases.size()+"; immutable model checks passed");
        if(!failed.isEmpty())throw new IllegalStateException("Conformance mismatch IDs: "+String.join(",",failed));
    }
    private static void immutableChecks(){
        String input="{\"manifestVersion\":1,\"applicationId\":\"org.example.test\",\"displayName\":\"Test\",\"exports\":[{\"serviceId\":\"test\",\"label\":\"Test\",\"protocol\":\"http\",\"localTarget\":{\"host\":\"127.0.0.1\",\"port\":18480},\"healthCheck\":{\"type\":\"http\",\"path\":\"/health\",\"expectedStatus\":[200]}}],\"imports\":[]}";
        ApplicationManifest value=ManifestContract.parse(input.getBytes(StandardCharsets.UTF_8));String original=value.toJson();
        expectImmutable(()->value.exports.clear());expectImmutable(()->value.imports.clear());expectImmutable(()->value.exports.get(0).healthCheck.expectedStatus.set(0,500));
        if(!original.equals(value.toJson()))throw new IllegalStateException("Normalized model was mutated");
        byte[] mutable=input.getBytes(StandardCharsets.UTF_8);ApplicationManifest retained=ManifestContract.parse(mutable);java.util.Arrays.fill(mutable,(byte)'x');
        if(!original.equals(retained.toJson()))throw new IllegalStateException("Input byte array was retained");
        try{ManifestContract.parse("{\"privateToken\":\"do-not-reflect-this-value\"}".getBytes(StandardCharsets.UTF_8));throw new IllegalStateException("Invalid input accepted");}
        catch(ContractException e){if(!e.toString().equals("dev.linkgate.contracts.ContractException: INVALID_REQUEST"))throw new IllegalStateException("Unsafe error");}
    }
    private static void expectImmutable(Runnable action){try{action.run();}catch(UnsupportedOperationException expected){return;}throw new IllegalStateException("Mutable model collection");}
    private static boolean equivalent(Object expected,Object actual){
        if(expected==null||actual==null)return expected==actual;
        if(expected instanceof StrictJson.NumberToken && actual instanceof StrictJson.NumberToken)return ((StrictJson.NumberToken)expected).raw.equals(((StrictJson.NumberToken)actual).raw);
        if(expected instanceof Map<?,?>&&actual instanceof Map<?,?>){Map<?,?> a=(Map<?,?>)expected,b=(Map<?,?>)actual;if(!a.keySet().equals(b.keySet()))return false;for(Object k:a.keySet())if(!equivalent(a.get(k),b.get(k)))return false;return true;}
        if(expected instanceof List<?> && actual instanceof List<?>){List<?> a=(List<?>)expected,b=(List<?>)actual;if(a.size()!=b.size())return false;for(int n=0;n<a.size();n++)if(!equivalent(a.get(n),b.get(n)))return false;return true;}
        return expected.equals(actual);
    }
    @SuppressWarnings("unchecked") private static Map<String,Object> object(Object value){if(!(value instanceof Map<?,?>))throw new IllegalStateException("Invalid corpus");return (Map<String,Object>)value;}
    @SuppressWarnings("unchecked") private static List<Object> list(Object value){if(!(value instanceof List<?>))throw new IllegalStateException("Invalid corpus");return (List<Object>)value;}
}
