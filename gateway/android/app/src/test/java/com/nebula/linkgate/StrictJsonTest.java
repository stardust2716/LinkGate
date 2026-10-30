package com.nebula.linkgate;

import org.junit.Test;
import static org.junit.Assert.assertThrows;

public class StrictJsonTest {
    @Test public void duplicateDecodedKeysAreRejectedAtEveryDepth() {
        for (String text : new String[]{
                "{\"a\":1,\"a\":2}",
                "{\"a\":1,\"\\u0061\":2}",
                "{\"server\":{\"token\":\"a\",\"token\":\"b\"}}",
                "{\"services\":[{\"id\":\"a\",\"id\":\"b\"}]}"
        }) assertThrows(Exception.class, () -> StrictJson.validate(text));
    }
    @Test public void lenientOrgJsonExtensionsAreNotAccepted() {
        for (String text : new String[]{
                "{unquoted:1}", "{'a':1}", "{\"a\":1,}", "[1,]", "{} {}",
                "{/*comment*/\"a\":1}", "{\"a\":01}", "{\"a\":+1}",
                "{\"a\":.1}", "{\"a\":1.}", "{\"a\":1e}",
                "{\"a\":\"\\u１２３４\"}", "{\"a\":\"\\q\"}"
        }) assertThrows(Exception.class, () -> StrictJson.validate(text));
    }
    @Test public void validUnicodeEscapesAndSeparateObjectKeysAreAccepted() throws Exception {
        StrictJson.validate("{\"name\":\"\\ud83d\\ude03\",\"x\":[1,-2.3e+4,true,false,null,{\"name\":\"other\"}]}");
        assertThrows(Exception.class, () -> StrictJson.validate("{\"x\":\"\\ud800\"}"));
        assertThrows(Exception.class, () -> StrictJson.validate("{\"x\":\"\\udc00\"}"));
    }
    @Test public void inputNestingIsBounded() {
        assertThrows(Exception.class, () -> StrictJson.validate("[".repeat(20) + "0" + "]".repeat(20)));
    }
}
