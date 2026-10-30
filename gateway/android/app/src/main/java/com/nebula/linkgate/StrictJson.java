package com.nebula.linkgate;

import java.util.HashSet;
import java.util.Set;

/** Small RFC 8259 syntax gate so Android's lenient org.json cannot change policy. */
final class StrictJson {
    private final String input;
    private int offset, nodes;
    private StrictJson(String input) { this.input = input; }
    static void validate(String input) throws Exception {
        StrictJson parser = new StrictJson(input);
        parser.value(0); parser.space();
        if (parser.offset != input.length()) throw invalid();
    }
    private void value(int depth) throws Exception {
        if (depth > 8 || ++nodes > 4096) throw invalid();
        space(); if (offset >= input.length()) throw invalid();
        char c = input.charAt(offset);
        if (c == '{') object(depth + 1);
        else if (c == '[') array(depth + 1);
        else if (c == '"') string();
        else if (c == 't') literal("true");
        else if (c == 'f') literal("false");
        else if (c == 'n') literal("null");
        else number();
    }
    private void object(int depth) throws Exception {
        offset++; space(); if (take('}')) return;
        Set<String> keys = new HashSet<>();
        while (true) {
            space(); String key = string();
            if (!keys.add(key)) throw invalid();
            space(); require(':'); value(depth); space();
            if (take('}')) return;
            require(',');
        }
    }
    private void array(int depth) throws Exception {
        offset++; space(); if (take(']')) return;
        while (true) { value(depth); space(); if (take(']')) return; require(','); }
    }
    private String string() throws Exception {
        require('"'); StringBuilder decoded = new StringBuilder();
        while (offset < input.length()) {
            char c = input.charAt(offset++);
            if (c == '"') {
                String value = decoded.toString();
                for (int i = 0; i < value.length(); i++) {
                    char unit = value.charAt(i);
                    if (Character.isHighSurrogate(unit)) {
                        if (++i >= value.length() || !Character.isLowSurrogate(value.charAt(i))) throw invalid();
                    } else if (Character.isLowSurrogate(unit)) throw invalid();
                }
                return value;
            }
            if (c < 32) throw invalid();
            if (c == '\\') {
                if (offset >= input.length()) throw invalid();
                char escape = input.charAt(offset++);
                if (escape == '"' || escape == '\\' || escape == '/') c = escape;
                else if (escape == 'b') c = '\b';
                else if (escape == 'f') c = '\f';
                else if (escape == 'n') c = '\n';
                else if (escape == 'r') c = '\r';
                else if (escape == 't') c = '\t';
                else if (escape == 'u') {
                    if (offset + 4 > input.length()) throw invalid();
                    int code = 0;
                    for (int i = 0; i < 4; i++) {
                        char unit = input.charAt(offset++);
                        int digit = unit >= '0' && unit <= '9' ? unit - '0'
                                : unit >= 'a' && unit <= 'f' ? unit - 'a' + 10
                                : unit >= 'A' && unit <= 'F' ? unit - 'A' + 10 : -1;
                        if (digit < 0) throw invalid();
                        code = code * 16 + digit;
                    }
                    c = (char)code;
                } else throw invalid();
            }
            decoded.append(c);
        }
        throw invalid();
    }
    private void literal(String value) throws Exception {
        if (!input.startsWith(value, offset)) throw invalid();
        offset += value.length();
    }
    private void number() throws Exception {
        take('-');
        if (take('0')) { /* A subsequent digit is rejected by the enclosing delimiter. */ }
        else {
            if (offset >= input.length() || input.charAt(offset) < '1' || input.charAt(offset) > '9') throw invalid();
            digits();
        }
        if (take('.')) { int start = offset; digits(); if (offset == start) throw invalid(); }
        if (take('e') || take('E')) {
            if (!take('+')) take('-');
            int start = offset; digits(); if (offset == start) throw invalid();
        }
    }
    private void digits() { while (offset < input.length() && input.charAt(offset) >= '0' && input.charAt(offset) <= '9') offset++; }
    private void space() { while (offset < input.length() && " \t\r\n".indexOf(input.charAt(offset)) >= 0) offset++; }
    private boolean take(char expected) {
        if (offset < input.length() && input.charAt(offset) == expected) { offset++; return true; }
        return false;
    }
    private void require(char expected) throws Exception { if (!take(expected)) throw invalid(); }
    private static Exception invalid() { return new Exception("无效或含重复字段的 JSON 配对文件。"); }
}
