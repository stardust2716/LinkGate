package dev.linkgate.agent;

import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.util.ArrayList;
import java.util.Collections;
import java.util.HashSet;
import java.util.List;

public final class SubjectIdentity {
    public final String scope,provenance;
    public SubjectIdentity(int uid,String packageName,long installed,List<String> current,List<String> orderedHistory,boolean multiple) {
        if(uid<0||packageName==null||packageName.length()>255||!packageName.matches("[A-Za-z][A-Za-z0-9_]*(?:\\.[A-Za-z0-9_]+)+")||installed<=0)throw new SecurityException("AUTH_REQUIRED");
        List<String> signers=validated(current),history=validated(orderedHistory);Collections.sort(signers);
        String anchor;
        if(multiple){if(signers.size()<2)throw new SecurityException("AUTH_REQUIRED");List<String> sorted=new ArrayList<>(history);Collections.sort(sorted);if(!signers.equals(sorted))throw new SecurityException("AUTH_REQUIRED");anchor="multiple:"+String.join(",",signers);}
        else{if(signers.size()!=1||!signers.get(0).equals(history.get(history.size()-1)))throw new SecurityException("AUTH_REQUIRED");anchor="single:"+history.get(0);}
        String identity=uid+"\n"+packageName+"\n"+installed+"\n"+anchor;
        scope=hash(identity);provenance=uid+"\n"+packageName+"\n"+installed+"\n"+String.join(",",signers)+"\n"+String.join(",",history);
    }
    private static List<String> validated(List<String> values) {if(values==null||values.isEmpty()||values.size()>32)throw new SecurityException("AUTH_REQUIRED");List<String> result=new ArrayList<>();for(String value:values){if(value==null||!value.matches("[0-9a-f]{64}"))throw new SecurityException("AUTH_REQUIRED");result.add(value);}if(new HashSet<>(result).size()!=result.size())throw new SecurityException("AUTH_REQUIRED");return result;}
    private static String hash(String value){try{StringBuilder text=new StringBuilder(64);for(byte part:MessageDigest.getInstance("SHA-256").digest(value.getBytes(StandardCharsets.UTF_8))){text.append(Character.forDigit((part>>>4)&15,16));text.append(Character.forDigit(part&15,16));}return text.toString();}catch(java.security.GeneralSecurityException e){throw new SecurityException("AUTH_REQUIRED");}}
}
