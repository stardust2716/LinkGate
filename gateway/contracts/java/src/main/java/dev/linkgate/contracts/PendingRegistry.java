package dev.linkgate.contracts;

import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.UUID;

/** Host-only transaction core. Caller scopes must be supplied by the authenticated platform boundary. */
public final class PendingRegistry {
    public interface Database { Session open(); }
    public interface Session extends AutoCloseable {
        void begin();
        void execute(String sql,String... parameters);
        List<String[]> query(String sql,String... parameters);
        void commit();
        @Override void close();
    }
    public static final class Failure extends RuntimeException {
        private static final long serialVersionUID=1L;
        public final String code;
        public Failure(String code) {
            super("Registration rejected.");
            this.code=Arrays.asList("OPERATION_CONFLICT","OPERATION_UNKNOWN","RESOURCE_EXHAUSTED","STORAGE_UNAVAILABLE").contains(code)?code:"STORAGE_UNAVAILABLE";
        }
    }
    private final Database database;
    private final int callerLimit,totalLimit,applicationLimit;
    public PendingRegistry(Database database) { this(database,500000,1000000,1024); }
    public PendingRegistry(Database database,int callerLimit,int totalLimit,int applicationLimit) {
        if(database==null||callerLimit<1||callerLimit>500000||totalLimit<1||totalLimit>1000000||applicationLimit<1||applicationLimit>1024)throw new IllegalArgumentException("Invalid registry limits.");
        this.database=database;this.callerLimit=callerLimit;this.totalLimit=totalLimit;this.applicationLimit=applicationLimit;
        try(Session session=database.open()) {
            session.begin();long version=count(session,"PRAGMA user_version");
            if(version!=0&&version!=1)throw storage();
            if(version==0&&count(session,"SELECT COUNT(*) FROM sqlite_master WHERE name NOT LIKE 'sqlite_%'")!=0)throw storage();
            session.execute("CREATE TABLE IF NOT EXISTS applications(caller TEXT NOT NULL,app TEXT NOT NULL,reference TEXT NOT NULL,manifest TEXT NOT NULL,PRIMARY KEY(caller,app))");
            session.execute("CREATE TABLE IF NOT EXISTS operations(caller TEXT NOT NULL,operation TEXT NOT NULL,digest TEXT NOT NULL,result TEXT NOT NULL,PRIMARY KEY(caller,operation))");
            session.execute("PRAGMA user_version=1");session.commit();
        } catch(Failure e){throw e;}catch(RuntimeException e){throw storage();}
    }

    public synchronized String register(String caller,UUID operation,ApplicationManifest manifest) {
        requireCaller(caller);if(operation==null||manifest==null)throw new IllegalArgumentException("Registration arguments required.");
        String normalized=manifest.toJson();
        try(Session session=database.open()) {
            session.begin();String hash=digest("app.register\n"+normalized);
            List<String[]> previous=session.query("SELECT digest,result FROM operations WHERE caller=? AND operation=?",caller,operation.toString());
            if(!previous.isEmpty()) {
                String[] row=single(previous,2);
                if(!hash.equals(row[0]))throw new Failure("OPERATION_CONFLICT");
                validateResult(row[1]);return row[1];
            }
            if(count(session,"SELECT COUNT(*) FROM operations WHERE caller=?",caller)>=callerLimit||count(session,"SELECT COUNT(*) FROM operations")>=totalLimit)throw new Failure("RESOURCE_EXHAUSTED");
            List<String[]> oldRows=session.query("SELECT reference,manifest FROM applications WHERE caller=? AND app=?",caller,manifest.applicationId);
            String reference; ApplicationManifest old=null;String oldJson=null;
            if(oldRows.isEmpty()) {
                if(count(session,"SELECT COUNT(*) FROM applications")>=applicationLimit)throw new Failure("RESOURCE_EXHAUSTED");
                reference=UUID.randomUUID().toString();
            } else {
                String[] row=single(oldRows,2);reference=row[0];ControlRequest.uuid(reference);oldJson=row[1];
                old=ManifestContract.parse(oldJson.getBytes(StandardCharsets.UTF_8));
                if(!old.applicationId.equals(manifest.applicationId))throw storage();
            }
            Map<String,String> oldExports=exports(old),newExports=exports(manifest),oldImports=imports(old),newImports=imports(manifest);
            String result=StrictJson.write(ControlRequest.map("applicationRef",reference,"permissionDiff",ControlRequest.map(
                "addedExports",added(newExports,oldExports),"removedExports",added(oldExports,newExports),
                "addedImports",added(newImports,oldImports),"removedImports",added(oldImports,newImports),
                "changedExports",changed(newExports,oldExports),"changedImports",changed(newImports,oldImports),
                "declarationChanged",!normalized.equals(oldJson)),"approvalRequired",true));
            if(oldRows.isEmpty())session.execute("INSERT INTO applications(caller,app,reference,manifest) VALUES(?,?,?,?)",caller,manifest.applicationId,reference,normalized);
            else session.execute("UPDATE applications SET manifest=? WHERE caller=? AND app=?",normalized,caller,manifest.applicationId);
            session.execute("INSERT INTO operations(caller,operation,digest,result) VALUES(?,?,?,?)",caller,operation.toString(),hash,result);
            session.commit();return result;
        } catch(Failure e){throw e;}catch(RuntimeException e){throw storage();}
    }

    public synchronized String status(String caller,UUID operation) {
        requireCaller(caller);if(operation==null)throw new IllegalArgumentException("Operation ID required.");
        try(Session session=database.open()) {
            List<String[]> rows=session.query("SELECT result FROM operations WHERE caller=? AND operation=?",caller,operation.toString());
            if(rows.isEmpty())throw new Failure("OPERATION_UNKNOWN");
            Object result=validateResult(single(rows,1)[0]);
            return StrictJson.write(ControlRequest.map("operationId",operation.toString(),"status","succeeded","result",result));
        } catch(Failure e){throw e;}catch(RuntimeException e){throw storage();}
    }
    private static Object validateResult(String json) {
        Map<String,Object> result=ControlRequest.object(StrictJson.parse(json.getBytes(StandardCharsets.UTF_8),65536));
        RegistrationReply.parsePending(result);
        return result;
    }
    private static void requireCaller(String caller){if(caller==null||caller.isEmpty()||caller.length()>128)throw new IllegalArgumentException("Caller required.");}
    private static long count(Session session,String sql,String... args){return Long.parseLong(single(session.query(sql,args),1)[0]);}
    private static String[] single(List<String[]> rows,int columns){if(rows.size()!=1||rows.get(0).length!=columns)throw storage();return rows.get(0);}
    private static String digest(String input) {
        try { byte[] bytes=MessageDigest.getInstance("SHA-256").digest(input.getBytes(StandardCharsets.UTF_8));StringBuilder text=new StringBuilder(64);for(byte value:bytes){text.append(Character.forDigit((value>>>4)&15,16));text.append(Character.forDigit(value&15,16));}return text.toString(); }
        catch(java.security.NoSuchAlgorithmException e){throw storage();}
    }
    private static Map<String,String> exports(ApplicationManifest manifest){Map<String,String> values=new LinkedHashMap<>();if(manifest!=null)for(ApplicationManifest.Export item:manifest.exports)values.put(item.serviceId,StrictJson.write(item.normalized()));return values;}
    private static Map<String,String> imports(ApplicationManifest manifest){Map<String,String> values=new LinkedHashMap<>();if(manifest!=null)for(ApplicationManifest.Import item:manifest.imports)values.put(item.alias,StrictJson.write(item.normalized()));return values;}
    private static List<String> added(Map<String,String> next,Map<String,String> old){List<String> values=new ArrayList<>();for(String key:next.keySet())if(!old.containsKey(key))values.add(key);return values;}
    private static List<String> changed(Map<String,String> next,Map<String,String> old){List<String> values=new ArrayList<>();for(String key:next.keySet())if(old.containsKey(key)&&!next.get(key).equals(old.get(key)))values.add(key);return values;}
    private static Failure storage(){return new Failure("STORAGE_UNAVAILABLE");}
}
