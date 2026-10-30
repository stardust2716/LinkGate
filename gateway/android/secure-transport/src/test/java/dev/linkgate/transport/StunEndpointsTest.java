package dev.linkgate.transport;

import java.util.ArrayList;
import java.util.Arrays;
import java.util.Collections;
import org.junit.Test;
import static org.junit.Assert.*;

public final class StunEndpointsTest {
    @Test public void preservesExactConfiguredEndpointsAndImmutableDisabledMode(){
        ArrayList<String> input=new ArrayList<>(Arrays.asList("127.0.0.1:3478","192.0.2.10:5349"));var selected=StunEndpoints.snapshot(input);input.clear();assertEquals(2,selected.size());assertEquals("[\"127.0.0.1:3478\",\"192.0.2.10:5349\"]",StunEndpoints.environment(selected));assertEquals("[]",StunEndpoints.environment(Collections.emptyList()));try{selected.clear();fail();}catch(UnsupportedOperationException expected){}
    }
    @Test public void rejectsNonCanonicalAndUnapprovedAddressClasses(){
        for(String invalid:Arrays.asList("198.18.0.1:3478","198.19.1.1:3478","0.0.0.0:3478","255.255.255.255:3478","224.0.0.1:3478","239.1.1.1:3478","169.254.1.1:3478","[::1]:3478","stun.example.invalid:3478","127.0.0.1:0","127.0.0.1:65536","127.0.0.1:03478","127.00.0.1:3478","127.0.0.1:3478 ","127.0.0.1:3478\n","127.0.0.1","127.0.0.256:3478")){
            try{StunEndpoints.snapshot(Collections.singletonList(invalid));fail(invalid);}catch(IllegalArgumentException expected){}
        }
    }
    @Test public void rejectsDuplicatesWrongShapeAndExcessDestinations(){
        for(Object invalid:Arrays.asList("[]",null,Collections.singletonList(1),Arrays.asList("127.0.0.1:3478","127.0.0.1:3478"),Arrays.asList("127.0.0.1:1","127.0.0.1:2","127.0.0.1:3","127.0.0.1:4","127.0.0.1:5"))){try{StunEndpoints.snapshot(invalid);fail();}catch(IllegalArgumentException expected){}}
    }
}
