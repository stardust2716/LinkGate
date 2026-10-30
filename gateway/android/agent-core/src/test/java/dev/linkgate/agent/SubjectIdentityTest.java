package dev.linkgate.agent;

import java.util.Arrays;
import org.junit.Test;
import static org.junit.Assert.*;

public class SubjectIdentityTest {
    private static final String A="aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",B="bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    @Test public void verifiedRotationPreservesScopeButChangesProvenance(){SubjectIdentity before=new SubjectIdentity(10001,"org.example.client",1000,Arrays.asList(A),Arrays.asList(A),false),after=new SubjectIdentity(10001,"org.example.client",1000,Arrays.asList(B),Arrays.asList(A,B),false);assertEquals(before.scope,after.scope);assertNotEquals(before.provenance,after.provenance);}
    @Test public void reinstallAndUnrelatedSignerHaveDifferentScopes(){SubjectIdentity before=new SubjectIdentity(10001,"org.example.client",1000,Arrays.asList(A),Arrays.asList(A),false);assertNotEquals(before.scope,new SubjectIdentity(10001,"org.example.client",2000,Arrays.asList(A),Arrays.asList(A),false).scope);assertNotEquals(before.scope,new SubjectIdentity(10001,"org.example.client",1000,Arrays.asList(B),Arrays.asList(B),false).scope);}
    @Test public void brokenLineageAndDuplicateSignersFailClosed(){try{new SubjectIdentity(10001,"org.example.client",1000,Arrays.asList(A),Arrays.asList(A,B),false);fail();}catch(SecurityException expected){assertEquals("AUTH_REQUIRED",expected.getMessage());}try{new SubjectIdentity(10001,"org.example.client",1000,Arrays.asList(A,A),Arrays.asList(A,A),true);fail();}catch(SecurityException expected){assertEquals("AUTH_REQUIRED",expected.getMessage());}}
}
