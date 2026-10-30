package dev.linkgate.contracts;

import java.nio.ByteBuffer;
import java.nio.charset.CharacterCodingException;
import java.nio.charset.CodingErrorAction;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

/** Small bounded JSON reader preserving integer lexical form and decoded keys. */
final class StrictJson {
    static final class NumberToken {
        final String raw;
        NumberToken(String value) { raw = value; }
    }
    private final String text;
    private final int maxDepth, maxValues;
    private int cursor, values;
    private final boolean captureManifest;
    private final boolean captureConnection;
    private String manifestSource;
    private String policySource;
    private String paramsSource;
    private StrictJson(String input, int depth, int count) { this(input,depth,count,false); }
    private StrictJson(String input,int depth,int count,boolean capture) { this(input,depth,count,capture,false); }
    private StrictJson(String input,int depth,int count,boolean capture,boolean connection) { text=input;maxDepth=depth;maxValues=count;captureManifest=capture;captureConnection=connection; }

    static Object parse(byte[] utf8, int byteLimit) {
        return parseText(decode(utf8,byteLimit),16,10000);
    }
    static final class RequestDocument {
        final Object value;
        final String manifestSource;
        final String paramsSource;
        RequestDocument(Object value,String source,String params) { this.value=value;manifestSource=source;paramsSource=params; }
    }
    static RequestDocument parseRequest(byte[] utf8,int byteLimit) {
        String decoded=decode(utf8,byteLimit);
        StrictJson reader=new StrictJson(decoded,16,10000,true);
        Object value=reader.value(0);reader.whitespace();
        if(reader.cursor!=decoded.length())throw new ContractException();
        return new RequestDocument(value,reader.manifestSource,reader.paramsSource);
    }
    static final class ConnectionDocument {
        final Object value;final String policySource;
        ConnectionDocument(Object value,String policy){this.value=value;policySource=policy;}
    }
    static ConnectionDocument parseConnection(byte[] utf8,int byteLimit){
        String decoded=decode(utf8,byteLimit);StrictJson reader=new StrictJson(decoded,16,10000,false,true);
        Object value=reader.value(0);reader.whitespace();if(reader.cursor!=decoded.length())throw new ContractException();return new ConnectionDocument(value,reader.policySource);
    }
    private static String decode(byte[] utf8,int byteLimit) {
        if (utf8==null || utf8.length==0 || utf8.length>byteLimit) throw new ContractException();
        final String decoded;
        try {
            decoded=StandardCharsets.UTF_8.newDecoder().onMalformedInput(CodingErrorAction.REPORT)
                .onUnmappableCharacter(CodingErrorAction.REPORT).decode(ByteBuffer.wrap(utf8)).toString();
        } catch (CharacterCodingException e) { throw new ContractException(); }
        return decoded;
    }

    static Object parseText(String text, int depth, int count) {
        if (text==null || text.isEmpty() || text.charAt(0)=='\ufeff') throw new ContractException();
        StrictJson reader=new StrictJson(text,depth,count);
        Object result=reader.value(0);reader.whitespace();
        if(reader.cursor!=text.length())throw new ContractException();
        return result;
    }

    private Object value(int depth) {
        if(++values>maxValues)throw new ContractException();
        whitespace();if(cursor>=text.length())throw new ContractException();
        char next=text.charAt(cursor);
        if(next=='{' || next=='[') {
            if(depth>=maxDepth)throw new ContractException();
            return next=='{'?object(depth+1):array(depth+1);
        }
        if(next=='"')return string();
        if(next=='t'){literal("true");return Boolean.TRUE;}
        if(next=='f'){literal("false");return Boolean.FALSE;}
        if(next=='n'){literal("null");return null;}
        if(next=='-' || (next>='0'&&next<='9'))return number();
        throw new ContractException();
    }
    private Map<String,Object> object(int depth) {
        cursor++;Map<String,Object> out=new LinkedHashMap<>();whitespace();
        if(consume('}'))return out;
        while(true){
            whitespace();if(cursor>=text.length()||text.charAt(cursor)!='"')throw new ContractException();
            String key=string();whitespace();require(':');
            if(out.containsKey(key))throw new ContractException();
            whitespace();int start=cursor;
            out.put(key,value(depth));
            if(captureManifest&&depth==2&&key.equals("manifest"))manifestSource=text.substring(start,cursor);
            if(captureManifest&&depth==1&&key.equals("params"))paramsSource=text.substring(start,cursor);
            if(captureConnection&&depth==1&&key.equals("connectionPolicy"))policySource=text.substring(start,cursor);
            whitespace();
            if(consume('}'))return out;
            require(',');
        }
    }
    private List<Object> array(int depth) {
        cursor++;List<Object> out=new ArrayList<>();whitespace();if(consume(']'))return out;
        while(true){out.add(value(depth));whitespace();if(consume(']'))return out;require(',');}
    }
    private String string() {
        require('"');StringBuilder out=new StringBuilder();boolean finished=false;
        while(cursor<text.length()){
            char ch=text.charAt(cursor++);
            if(ch=='"'){finished=true;break;}
            if(ch<0x20)throw new ContractException();
            if(ch!='\\'){out.append(ch);continue;}
            if(cursor>=text.length())throw new ContractException();
            char escape=text.charAt(cursor++);
            switch(escape){
                case '"':out.append('"');break;case '\\':out.append('\\');break;case '/':out.append('/');break;
                case 'b':out.append('\b');break;case 'f':out.append('\f');break;case 'n':out.append('\n');break;
                case 'r':out.append('\r');break;case 't':out.append('\t');break;
                case 'u':
                    if(cursor+4>text.length())throw new ContractException();
                    int code=0;
                    for(int i=0;i<4;i++){
                        char digit=text.charAt(cursor++);int n=digit>='0'&&digit<='9'?digit-'0':digit>='a'&&digit<='f'?digit-'a'+10:digit>='A'&&digit<='F'?digit-'A'+10:-1;
                        if(n<0)throw new ContractException();code=code*16+n;
                    }
                    out.append((char)code);break;
                default:throw new ContractException();
            }
        }
        if(!finished)throw new ContractException();
        String value=out.toString();scalars(value);return value;
    }
    static int scalars(String value){
        int count=0;
        for(int i=0;i<value.length();i++,count++){
            char ch=value.charAt(i);
            if(Character.isHighSurrogate(ch)){
                if(i+1>=value.length()||!Character.isLowSurrogate(value.charAt(i+1)))throw new ContractException();i++;
            }else if(Character.isLowSurrogate(ch))throw new ContractException();
        }
        return count;
    }
    private NumberToken number(){
        int start=cursor;consume('-');
        if(consume('0')){if(cursor<text.length()&&digit(text.charAt(cursor)))throw new ContractException();}
        else{if(cursor>=text.length()||text.charAt(cursor)<'1'||text.charAt(cursor)>'9')throw new ContractException();while(cursor<text.length()&&digit(text.charAt(cursor)))cursor++;}
        if(consume('.')){int begin=cursor;while(cursor<text.length()&&digit(text.charAt(cursor)))cursor++;if(cursor==begin)throw new ContractException();}
        if(cursor<text.length()&&(text.charAt(cursor)=='e'||text.charAt(cursor)=='E')){
            cursor++;if(cursor<text.length()&&(text.charAt(cursor)=='+'||text.charAt(cursor)=='-'))cursor++;
            int begin=cursor;while(cursor<text.length()&&digit(text.charAt(cursor)))cursor++;if(cursor==begin)throw new ContractException();
        }
        return new NumberToken(text.substring(start,cursor));
    }
    private static boolean digit(char c){return c>='0'&&c<='9';}
    private void literal(String value){if(!text.startsWith(value,cursor))throw new ContractException();cursor+=value.length();}
    private void whitespace(){while(cursor<text.length()){char c=text.charAt(cursor);if(c!=' '&&c!='\t'&&c!='\r'&&c!='\n')break;cursor++;}}
    private boolean consume(char c){if(cursor<text.length()&&text.charAt(cursor)==c){cursor++;return true;}return false;}
    private void require(char c){if(!consume(c))throw new ContractException();}

    static String write(Object value){
        StringBuilder out=new StringBuilder();writeValue(out,value);return out.toString();
    }
    private static void writeValue(StringBuilder out,Object value){
        if(value==null){out.append("null");return;}
        if(value instanceof String){quote(out,(String)value);return;}
        if(value instanceof NumberToken){out.append(((NumberToken)value).raw);return;}
        if(value instanceof Number||value instanceof Boolean){out.append(value.toString());return;}
        if(value instanceof Map<?,?>){
            out.append('{');boolean first=true;for(Map.Entry<?,?> e:((Map<?,?>)value).entrySet()){
                if(!first)out.append(',');first=false;quote(out,(String)e.getKey());out.append(':');writeValue(out,e.getValue());
            }out.append('}');return;
        }
        if(value instanceof List<?>){out.append('[');boolean first=true;for(Object v:(List<?>)value){if(!first)out.append(',');first=false;writeValue(out,v);}out.append(']');return;}
        throw new IllegalStateException("Unsupported normalized value");
    }
    private static void quote(StringBuilder out,String value){
        out.append('"');for(int i=0;i<value.length();i++){
            char c=value.charAt(i);switch(c){
                case '"':out.append("\\\"");break;case '\\':out.append("\\\\");break;
                case '\b':out.append("\\b");break;case '\f':out.append("\\f");break;case '\n':out.append("\\n");break;
                case '\r':out.append("\\r");break;case '\t':out.append("\\t");break;
                default:if(c<0x20){out.append("\\u");String h=Integer.toHexString(c);for(int n=h.length();n<4;n++)out.append('0');out.append(h);}else out.append(c);
            }
        }out.append('"');
    }
}
