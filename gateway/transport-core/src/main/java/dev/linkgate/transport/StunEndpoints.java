package dev.linkgate.transport;

import dev.linkgate.contracts.WireJson;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.Collections;
import java.util.HashSet;
import java.util.List;

/** Agent-only public endpoint configuration, not application-supplied routes. */
public final class StunEndpoints {
    private StunEndpoints(){}
    public static List<String> snapshot(Object value) {
        if(!(value instanceof List<?>))throw invalid();List<?> input=(List<?>)value;if(input.size()>4)throw invalid();
        List<String> result=new ArrayList<>();HashSet<String> seen=new HashSet<>();
        for(Object part:input){
            if(!(part instanceof String))throw invalid();String endpoint=(String)part;if(endpoint.length()>21)throw invalid();String[] pieces=endpoint.split(":",-1);if(pieces.length!=2||!pieces[1].matches("[1-9][0-9]{0,4}")||Integer.parseInt(pieces[1])>65535)throw invalid();
            String[] fields=pieces[0].split("\\.",-1);if(fields.length!=4)throw invalid();int[] ip=new int[4];
            for(int i=0;i<4;i++){if(!fields[i].matches("0|[1-9][0-9]{0,2}"))throw invalid();ip[i]=Integer.parseInt(fields[i]);if(ip[i]>255)throw invalid();}
            if(pieces[0].equals("0.0.0.0")||pieces[0].equals("255.255.255.255")||ip[0]>=224&&ip[0]<=239||ip[0]==198&&(ip[1]==18||ip[1]==19)||ip[0]==169&&ip[1]==254||!seen.add(endpoint))throw invalid();result.add(endpoint);
        }
        return Collections.unmodifiableList(result);
    }
    public static String environment(List<String> endpoints){return new String(WireJson.write(snapshot(endpoints),512),StandardCharsets.UTF_8);}
    private static IllegalArgumentException invalid(){return new IllegalArgumentException("Explicit distinct canonical IPv4 STUN endpoints required.");}
}
