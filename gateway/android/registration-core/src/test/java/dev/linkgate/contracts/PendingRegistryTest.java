package dev.linkgate.contracts;

import org.junit.After;
import org.junit.Before;
import org.junit.Test;
import static org.junit.Assert.*;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.Paths;
import java.sql.Connection;
import java.sql.DriverManager;
import java.sql.PreparedStatement;
import java.sql.ResultSet;
import java.sql.SQLException;
import java.sql.Statement;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Comparator;
import java.util.List;
import java.util.Map;
import java.util.UUID;
import java.util.concurrent.Callable;
import java.util.concurrent.Executors;
import java.util.concurrent.Future;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.stream.Stream;

public final class PendingRegistryTest {
    private static final String MANIFEST="{\"manifestVersion\":1,\"applicationId\":\"test.pending\",\"displayName\":\"Pending\",\"exports\":[{\"serviceId\":\"files\",\"label\":\"Files\",\"protocol\":\"http\",\"localTarget\":{\"host\":\"127.0.0.1\",\"port\":18481}}],\"imports\":[]}";
    private Path root;
    @Before public void setup()throws Exception { root=Paths.get("build","registry-tests",UUID.randomUUID().toString()).toAbsolutePath().normalize();Files.createDirectories(root); }
    @After public void cleanup()throws Exception {
        Path allowed=Paths.get("build","registry-tests").toAbsolutePath().normalize();
        assertTrue(root.startsWith(allowed));
        if(Files.exists(root))try(Stream<Path> paths=Files.walk(root)){for(Path path:(Iterable<Path>)paths.sorted(Comparator.reverseOrder())::iterator)Files.delete(path);}
    }
    private Jdbc db(String name){return new Jdbc(root.resolve(name+".db"));}
    private static ApplicationManifest manifest(){return ManifestContract.parse(bytes(MANIFEST));}
    private static ApplicationManifest changed(){return ManifestContract.parse(bytes(MANIFEST.replace("18481","18482")));}
    private static byte[] bytes(String text){return text.getBytes(StandardCharsets.UTF_8);}
    private static Map<String,Object> object(Object value){return ControlRequest.object(value);}
    private static Map<String,Object> json(String text){return object(StrictJson.parse(bytes(text),65536));}
    private static void failure(String code,Runnable action){try{action.run();fail("Expected "+code);}catch(PendingRegistry.Failure e){assertEquals(code,e.code);assertEquals("Registration rejected.",e.getMessage());assertNull(e.getCause());}}

    @Test public void pendingResultMatchesWindowsShape() {
        PendingRegistry store=new PendingRegistry(db("shape"));UUID op=UUID.randomUUID();
        Map<String,Object> value=json(store.register("caller",op,manifest()));
        assertEquals(3,value.size());assertEquals(Boolean.TRUE,value.get("approvalRequired"));ControlRequest.uuid(value.get("applicationRef"));
        Map<String,Object> diff=object(value.get("permissionDiff"));assertEquals(7,diff.size());assertEquals(Arrays.asList("files"),diff.get("addedExports"));
        assertEquals(value,object(json(store.status("caller",op)).get("result")));
    }
    @Test public void reopenAndCanonicalRetryReturnOriginalResult() {
        Jdbc db=db("reopen");PendingRegistry first=new PendingRegistry(db);UUID op=UUID.randomUUID();String original=first.register("caller",op,manifest());
        ApplicationManifest equivalent=ManifestContract.parse(bytes(" \n"+MANIFEST.replace("\"manifestVersion\":1,\"applicationId\":\"test.pending\"","\"applicationId\":\"test.pending\",\"manifestVersion\":1")+" "));
        assertEquals(original,new PendingRegistry(db).register("caller",op,equivalent));assertEquals("1",db.scalar("SELECT COUNT(*) FROM operations"));
    }
    @Test public void changedParametersConflictWithoutWriting() {
        Jdbc db=db("conflict");PendingRegistry store=new PendingRegistry(db);UUID op=UUID.randomUUID();store.register("caller",op,manifest());
        failure("OPERATION_CONFLICT",()->store.register("caller",op,changed()));assertTrue(db.scalar("SELECT manifest FROM applications").contains("18481"));
    }
    @Test public void oldRetryDoesNotOverwriteNewPendingDeclaration() {
        Jdbc db=db("newer");PendingRegistry store=new PendingRegistry(db);UUID op=UUID.randomUUID();String first=store.register("caller",op,manifest());
        Map<String,Object> update=json(store.register("caller",UUID.randomUUID(),changed()));
        assertEquals(json(first).get("applicationRef"),update.get("applicationRef"));assertEquals(Arrays.asList("files"),object(update.get("permissionDiff")).get("changedExports"));
        assertEquals(first,store.register("caller",op,manifest()));assertTrue(db.scalar("SELECT manifest FROM applications").contains("18482"));
    }
    @Test public void callerNamespacesDoNotShareResults() {
        PendingRegistry store=new PendingRegistry(db("callers"));UUID op=UUID.randomUUID();
        String a=(String)json(store.register("a",op,manifest())).get("applicationRef");
        failure("OPERATION_UNKNOWN",()->store.status("b",op));
        assertNotEquals(a,json(store.register("b",op,manifest())).get("applicationRef"));
    }
    @Test public void quotasPreserveOldOperationsAndRejectNewWork() {
        PendingRegistry store=new PendingRegistry(db("quota"),1,4,1);UUID op=UUID.randomUUID();String original=store.register("a",op,manifest());
        failure("RESOURCE_EXHAUSTED",()->store.register("a",UUID.randomUUID(),manifest()));assertEquals(original,store.register("a",op,manifest()));
        failure("RESOURCE_EXHAUSTED",()->store.register("b",UUID.randomUUID(),manifest()));
        PendingRegistry global=new PendingRegistry(db("global"),5,1,10);global.register("a",UUID.randomUUID(),manifest());
        failure("RESOURCE_EXHAUSTED",()->global.register("b",UUID.randomUUID(),manifest()));
    }
    @Test public void journalFailureRollsBackNewApplication() {
        Jdbc db=db("rollback");PendingRegistry store=new PendingRegistry(db);
        db.sql("CREATE TRIGGER fail_operation BEFORE INSERT ON operations BEGIN SELECT RAISE(ABORT,'private-marker'); END");
        failure("STORAGE_UNAVAILABLE",()->store.register("caller",UUID.randomUUID(),manifest()));
        assertEquals("0",db.scalar("SELECT COUNT(*) FROM applications"));assertEquals("0",db.scalar("SELECT COUNT(*) FROM operations"));
        db.sql("DROP TRIGGER fail_operation");assertNotNull(store.register("caller",UUID.randomUUID(),manifest()));
    }
    @Test public void journalFailurePreservesPreviousDeclaration() {
        Jdbc db=db("old");PendingRegistry store=new PendingRegistry(db);store.register("caller",UUID.randomUUID(),manifest());
        db.sql("CREATE TRIGGER fail_operation BEFORE INSERT ON operations BEGIN SELECT RAISE(ABORT,'private-marker'); END");
        failure("STORAGE_UNAVAILABLE",()->store.register("caller",UUID.randomUUID(),changed()));
        assertTrue(db.scalar("SELECT manifest FROM applications").contains("18481"));assertEquals("1",db.scalar("SELECT COUNT(*) FROM operations"));
    }
    @Test public void concurrentInstancesCommitOnlyOnce()throws Exception {
        Jdbc db=db("concurrent");PendingRegistry a=new PendingRegistry(db),b=new PendingRegistry(db);UUID op=UUID.randomUUID();
        java.util.concurrent.ExecutorService workers=Executors.newFixedThreadPool(4);
        try {
            List<Callable<String>> calls=new ArrayList<>();for(int i=0;i<16;i++){final PendingRegistry store=i%2==0?a:b;calls.add(()->store.register("caller",op,manifest()));}
            String result=null;for(Future<String> future:workers.invokeAll(calls)){String value=future.get();if(result!=null)assertEquals(result,value);result=value;}
            assertEquals("1",db.scalar("SELECT COUNT(*) FROM applications"));assertEquals("1",db.scalar("SELECT COUNT(*) FROM operations"));
        } finally{workers.shutdownNow();assertTrue(workers.awaitTermination(5,TimeUnit.SECONDS));}
    }
    @Test public void unknownSchemasAndCorruptResultsFailClosed() {
        Jdbc db=db("schema");PendingRegistry store=new PendingRegistry(db);UUID op=UUID.randomUUID();store.register("caller",op,manifest());
        db.sql("UPDATE operations SET result='{}'");failure("STORAGE_UNAVAILABLE",()->store.status("caller",op));failure("STORAGE_UNAVAILABLE",()->store.register("caller",op,manifest()));
        db.sql("PRAGMA user_version=99");failure("STORAGE_UNAVAILABLE",()->new PendingRegistry(db));assertEquals("99",db.scalar("PRAGMA user_version"));
        db.sql("PRAGMA user_version=0");failure("STORAGE_UNAVAILABLE",()->new PendingRegistry(db));assertEquals("1",db.scalar("SELECT COUNT(*) FROM applications"));
    }
    @Test public void storedUnknownFieldsAreNotReturnedToCallers() {
        Jdbc db=db("corrupt-shape");PendingRegistry store=new PendingRegistry(db);UUID op=UUID.randomUUID();String result=store.register("caller",op,manifest());
        String corrupt=result.replace("\"declarationChanged\":true","\"declarationChanged\":true,\"privateMarker\":\"private-marker\"");
        try(PendingRegistry.Session session=db.open()) {session.execute("UPDATE operations SET result=?",corrupt);}
        failure("STORAGE_UNAVAILABLE",()->store.status("caller",op));failure("STORAGE_UNAVAILABLE",()->store.register("caller",op,manifest()));
    }
    private static String request(String method,String params,UUID operation) {
        return "{\"kind\":\"request\",\"apiVersion\":1,\"requestId\":\""+UUID.randomUUID()+"\",\"method\":\""+method+"\",\"params\":"+params+(operation==null?"":",\"operationId\":\""+operation+"\"")+"}";
    }
    private static Map<String,Object> response(ControlRegistration protocol,String request,String caller){return object(StrictJson.parse(protocol.respond(bytes(request),caller),65536));}
    private static String error(ControlRegistration p,String input,String caller){return (String)object(response(p,input,caller).get("error")).get("code");}
    @Test public void helloAndValidationDoNotInitializeStorage() {
        AtomicInteger calls=new AtomicInteger();ControlRegistration p=new ControlRegistration(new ControlHello(),()->{calls.incrementAndGet();throw new PendingRegistry.Failure("STORAGE_UNAVAILABLE");});
        Map<String,Object> result=object(response(p,request("hello","{\"supportedApiVersions\":[1]}",null),null).get("result"));
        assertEquals(16,((List<?>)result.get("capabilities")).size());assertEquals(0,calls.get());
        assertEquals("INVALID_REQUEST",error(p,request("app.register","{\"manifest\":"+MANIFEST+"}",null),"caller"));
        assertEquals("AUTH_REQUIRED",error(p,request("app.register","{\"manifest\":"+MANIFEST+"}",UUID.randomUUID()),null));
        assertEquals("CAPABILITY_UNSUPPORTED",error(p,request("connection.open","{}",UUID.randomUUID()),"caller"));assertEquals(0,calls.get());
    }
    @Test public void protocolRegistersAndQueriesOnlyCurrentCaller() {
        PendingRegistry store=new PendingRegistry(db("protocol"));ControlRegistration p=new ControlRegistration(new ControlHello(),()->store);UUID op=UUID.randomUUID();
        Map<String,Object> first=response(p,request("app.register","{\"manifest\":"+MANIFEST+"}",op),"caller");
        Map<String,Object> retry=response(p,request("app.register","{\"manifest\":"+MANIFEST+"}",op),"caller");assertEquals(first.get("result"),retry.get("result"));
        Map<String,Object> status=object(response(p,request("operation.status","{\"operationId\":\""+op+"\"}",null),"caller").get("result"));
        assertEquals(first.get("result"),status.get("result"));assertEquals("OPERATION_UNKNOWN",error(p,request("operation.status","{\"operationId\":\""+op+"\"}",null),"other"));
        assertEquals("INVALID_REQUEST",error(p,request("app.register","{\"manifest\":"+MANIFEST+",\"uid\":10001}",UUID.randomUUID()),"caller"));
    }
    @Test public void rawManifestBoundaryIsNotLostDuringParsing() {
        AtomicInteger calls=new AtomicInteger();PendingRegistry store=new PendingRegistry(db("boundary"));ControlRegistration p=new ControlRegistration(new ControlHello(),()->{calls.incrementAndGet();return store;});
        String padded=MANIFEST.substring(0,MANIFEST.length()-1)+spaces(49152-MANIFEST.length())+"}";
        assertTrue(response(p,request("app.register","{\"manifest\":"+padded+"}",UUID.randomUUID()),"caller").containsKey("result"));
        String tooBig=padded.substring(0,padded.length()-1)+" }";
        assertEquals("INVALID_REQUEST",error(p,request("app.register","{\"manifest\":"+tooBig+"}",UUID.randomUUID()),"caller"));assertEquals(1,calls.get());
        assertEquals("INVALID_REQUEST",error(p,request("app.register","{\"manifest\":"+MANIFEST.replace("18481","18481.0")+"}",UUID.randomUUID()),"caller"));assertEquals(1,calls.get());
    }
    @Test public void storageErrorsAreSanitizedAndReadOnlyHandlerStaysDisabled() {
        ControlRegistration p=new ControlRegistration(new ControlHello(),()->{throw new IllegalStateException("private-marker");});
        Map<String,Object> result=response(p,request("app.register","{\"manifest\":"+MANIFEST+"}",UUID.randomUUID()),"caller");
        assertEquals("STORAGE_UNAVAILABLE",object(result.get("error")).get("code"));assertFalse(StrictJson.write(result).contains("private-marker"));
        Map<String,Object> readonly=object(StrictJson.parse(new ControlHello().respond(bytes(request("app.register","{\"manifest\":"+MANIFEST+"}",UUID.randomUUID()))),65536));
        assertEquals("CAPABILITY_UNSUPPORTED",object(readonly.get("error")).get("code"));
    }
    private static String spaces(int count){char[] text=new char[count];Arrays.fill(text,' ');return new String(text);}

    private static final class Jdbc implements PendingRegistry.Database {
        private final Path path;
        Jdbc(Path path){this.path=path;}
        @Override public PendingRegistry.Session open(){
            try {
                Connection connection=DriverManager.getConnection("jdbc:sqlite:"+path);
                try(Statement command=connection.createStatement()){command.execute("PRAGMA busy_timeout=1000");command.execute("PRAGMA synchronous=FULL");command.execute("PRAGMA journal_mode=DELETE");}
                return new JdbcSession(connection);
            }catch(SQLException e){throw new PendingRegistry.Failure("STORAGE_UNAVAILABLE");}
        }
        void sql(String sql){try(PendingRegistry.Session session=open()){session.execute(sql);}}
        String scalar(String sql){try(PendingRegistry.Session session=open()){return session.query(sql).get(0)[0];}}
    }
    private static final class JdbcSession implements PendingRegistry.Session {
        private final Connection connection;
        private boolean transaction;
        JdbcSession(Connection connection){this.connection=connection;}
        @Override public void begin(){execute("BEGIN IMMEDIATE");transaction=true;}
        private PreparedStatement command(String sql,String... args)throws SQLException{PreparedStatement statement=connection.prepareStatement(sql);for(int i=0;i<args.length;i++)statement.setString(i+1,args[i]);return statement;}
        @Override public void execute(String sql,String... args){try(PreparedStatement statement=command(sql,args)){statement.execute();}catch(SQLException e){throw new PendingRegistry.Failure("STORAGE_UNAVAILABLE");}}
        @Override public List<String[]> query(String sql,String... args){
            try(PreparedStatement statement=command(sql,args);ResultSet result=statement.executeQuery()){
                List<String[]> rows=new ArrayList<>();while(result.next()){if(rows.size()>=2)throw new PendingRegistry.Failure("STORAGE_UNAVAILABLE");String[] row=new String[result.getMetaData().getColumnCount()];for(int i=0;i<row.length;i++)row[i]=result.getString(i+1);rows.add(row);}return rows;
            }catch(SQLException e){throw new PendingRegistry.Failure("STORAGE_UNAVAILABLE");}
        }
        @Override public void commit(){execute("COMMIT");transaction=false;}
        @Override public void close(){try{if(transaction)execute("ROLLBACK");}finally{try{connection.close();}catch(SQLException e){throw new PendingRegistry.Failure("STORAGE_UNAVAILABLE");}}}
    }
}
