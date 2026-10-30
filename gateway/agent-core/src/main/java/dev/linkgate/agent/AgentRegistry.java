package dev.linkgate.agent;

import dev.linkgate.contracts.ApplicationManifest;
import dev.linkgate.contracts.ManifestContract;
import dev.linkgate.contracts.WireJson;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Collections;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.UUID;

/** Host-only state authority. The platform supplies the actual Binder caller, never request parameters. */
public final class AgentRegistry {
    public interface Database { Session open(); }
    public interface Session extends AutoCloseable {
        void begin();void execute(String sql,String... parameters);List<String[]> query(String sql,String... parameters);void commit();@Override void close();
    }
    public interface Clock { long millis(); }
    public static final class Failure extends RuntimeException {
        private static final long serialVersionUID=1L;
        public final String code;
        public Failure(String code){super("Agent state request rejected.");this.code=Arrays.asList("AUTH_REQUIRED","IDENTITY_CHANGED","REVIEW_STALE","OPERATION_CONFLICT","OPERATION_UNKNOWN","RESOURCE_EXHAUSTED","STORAGE_UNAVAILABLE","CLOCK_UNTRUSTED","CAPABILITY_UNSUPPORTED","PERMISSION_DENIED","CONNECTION_NOT_READY","CONNECTION_CLOSED","STREAM_ALREADY_ATTACHED","NETWORK_CHANGED").contains(code)?code:"STORAGE_UNAVAILABLE";}
    }
    public static final class Review {
        public final String reference,caller,principal,pending,approved;public final long pendingRevision,decisionVersion,authorityEpoch;public final Long approvedRevision;
        private Review(String[] row){reference=row[0];caller=row[1];principal=row[2];pending=row[3];pendingRevision=positive(row[4]);decisionVersion=nonnegative(row[5]);authorityEpoch=nonnegative(row[6]);approved=row[7];approvedRevision=row[8]==null?null:positive(row[8]);if((approved==null)!=(approvedRevision==null))throw failure();ManifestContract.parse(pending.getBytes(StandardCharsets.UTF_8));if(approved!=null)ManifestContract.parse(approved.getBytes(StandardCharsets.UTF_8));}
    }
    public static final class Peer {
        public final String id,certificate,host;public final int port;public final long revision;public final boolean active;
        private Peer(String[] row){id=WireJson.key(row[0]);certificate=row[1];host=ipv4(row[2]);port=Integer.parseInt(row[3]);revision=positive(row[4]);active=flag(row[5]);if(certificate==null||certificate.length()>65536||port<1||port>65535)throw failure();}
    }
    public static final class Scope {
        public final String caller,application,client,alias,device,publisher,service,protocol,authority,host;public final long epoch,approvedRevision,peerRevision,grantRevision;public final int port,timeoutMs;public final String policyMode;public final boolean allowRelay;public final dev.linkgate.contracts.ConnectionPolicy policy;
        private Scope(String caller,Review review,ApplicationManifest.Import item,Peer peer,String publisher,String service,long grant) {
            this.caller=caller;application=review.reference;client=ManifestContract.parse(review.approved.getBytes(StandardCharsets.UTF_8)).applicationId;alias=item.alias;device=peer.id;this.publisher=publisher;this.service=service;protocol=item.protocol;authority=item.logicalAuthority;host=peer.host;port=peer.port;policy=item.connectionPolicy;timeoutMs=item.connectionPolicy.connectTimeoutMs;policyMode=item.connectionPolicy.mode;allowRelay=item.connectionPolicy.allowRelay;epoch=review.authorityEpoch;approvedRevision=review.approvedRevision;peerRevision=peer.revision;grantRevision=grant;
        }
        public String json(){return AgentRegistry.json(WireJson.map("caller",caller,"application",application,"client",client,"alias",alias,"device",device,"publisher",publisher,"service",service,"protocol",protocol,"authority",authority,"host",host,"port",port,"timeoutMs",timeoutMs,"epoch",epoch,"approvedRevision",approvedRevision,"peerRevision",peerRevision,"grantRevision",grantRevision));}
    }
    public static final class Open {
        public final String caller,operation,reference,boot,state,error,path;public final Scope scope;public final boolean created;
        private Open(String[] row,Scope scope,boolean created,String path){this.path=path;caller=row[0];operation=row[1];reference=row[2];boot=row[3];state=row[4];error=row[5];this.scope=scope;this.created=created;WireJson.uuid(operation);WireJson.uuid(reference);WireJson.uuid(boot);if(!Arrays.asList("pending","ready","closed").contains(state)||error!=null&&!Arrays.asList("CONNECTION_CLOSED","DIRECT_FAILED","RELAY_FAILED","AUTH_REQUIRED","AGENT_RESTARTED","RESOURCE_EXHAUSTED","GATEWAY_STOPPED").contains(error))throw failure();}
    }
    public static final class RequestReview {
        public final String reference,caller,application,alias,device,publisher,service,reason,state,principal;public final long revision;
        private RequestReview(String[] row,String state,String principal,long revision){reference=row[0];caller=row[1];application=row[2];alias=row[3];device=row[4];publisher=row[5];service=row[6];reason=row[7];this.state=state;this.principal=principal;this.revision=revision;}
    }
    interface Work<T>{T apply(Session session);}
    private static final String OWNER="@owner";
    private final Database database;private final Clock clock;private final int callerLimit,totalLimit;
    public AgentRegistry(Database database,Clock clock){this(database,clock,500000,1000000);}
    public AgentRegistry(Database database,Clock clock,int callerLimit,int totalLimit) {
        if(database==null||clock==null||callerLimit<1||callerLimit>500000||totalLimit<1||totalLimit>1000000)throw new IllegalArgumentException("Valid authority ports and limits required.");this.database=database;this.clock=clock;this.callerLimit=callerLimit;this.totalLimit=totalLimit;
        transaction(s->{long version=count(s,"PRAGMA user_version");if(version<0||version>2)throw failure();
            if(version==0){if(count(s,"SELECT COUNT(*) FROM sqlite_master WHERE name NOT LIKE 'sqlite_%'")!=0)throw failure();
                s.execute("CREATE TABLE meta(singleton INTEGER PRIMARY KEY,device TEXT,root TEXT,max_utc INTEGER NOT NULL)");s.execute("INSERT INTO meta VALUES(1,NULL,NULL,0)");
                s.execute("CREATE TABLE applications(reference TEXT PRIMARY KEY,caller TEXT NOT NULL,app TEXT NOT NULL,principal TEXT NOT NULL,pending TEXT NOT NULL,pending_revision INTEGER NOT NULL,decision_version INTEGER NOT NULL,authority_epoch INTEGER NOT NULL,approved TEXT,approved_revision INTEGER,UNIQUE(caller,app))");
                s.execute("CREATE TABLE operations(caller TEXT NOT NULL,operation TEXT NOT NULL,digest TEXT NOT NULL,result TEXT NOT NULL,PRIMARY KEY(caller,operation))");
                s.execute("CREATE TABLE peers(device TEXT PRIMARY KEY,certificate TEXT NOT NULL,host TEXT NOT NULL,port INTEGER NOT NULL,revision INTEGER NOT NULL,active INTEGER NOT NULL)");
                s.execute("CREATE TABLE requests(reference TEXT PRIMARY KEY,caller TEXT NOT NULL,scope TEXT NOT NULL,application TEXT NOT NULL,alias TEXT NOT NULL,device TEXT NOT NULL,publisher TEXT NOT NULL,service TEXT NOT NULL,epoch INTEGER NOT NULL,approved_revision INTEGER NOT NULL,peer_revision INTEGER NOT NULL,reason TEXT NOT NULL)");
                s.execute("CREATE TABLE access(scope TEXT PRIMARY KEY,request TEXT NOT NULL,snapshot TEXT NOT NULL,revision INTEGER NOT NULL,active INTEGER NOT NULL,device TEXT NOT NULL)");
                s.execute("CREATE TABLE opens(caller TEXT NOT NULL,operation TEXT NOT NULL,reference TEXT NOT NULL,boot TEXT NOT NULL,state TEXT NOT NULL,error TEXT,snapshot TEXT NOT NULL,PRIMARY KEY(caller,operation))");s.execute("PRAGMA user_version=1");
            }
            if(version<2){s.execute("CREATE TABLE connections(reference TEXT PRIMARY KEY,caller TEXT NOT NULL,operation TEXT NOT NULL,boot TEXT NOT NULL,request TEXT NOT NULL,scope TEXT NOT NULL,sequence INTEGER NOT NULL,terminal INTEGER NOT NULL,snapshot TEXT NOT NULL,UNIQUE(caller,operation))");s.execute("CREATE INDEX connections_active ON connections(caller) WHERE terminal=0");s.execute("CREATE INDEX opens_active ON opens(caller) WHERE state!='closed'");s.execute("CREATE TABLE connection_attachments(reference TEXT PRIMARY KEY,caller TEXT NOT NULL,operation TEXT NOT NULL,stream TEXT NOT NULL UNIQUE,boot TEXT NOT NULL,consumed INTEGER NOT NULL)");s.execute("PRAGMA user_version=2");}
            verifySchema(s);return null;});
    }
    synchronized <T>T transaction(Work<T> action) {
        try(Session session=database.open()){session.begin();T result=action.apply(session);session.commit();return result;}
        catch(Failure e){throw e;}catch(RuntimeException e){throw failure();}
    }
    public void bindIdentity(String device,String root) {
        WireJson.key(device);WireJson.key(root);
        transaction(s->{String[] previous=one(s.query("SELECT device,root FROM meta WHERE singleton=1"),2);
            if(previous[0]!=null&&(!device.equals(previous[0])||!root.equals(previous[1])))throw new Failure("IDENTITY_CHANGED");
            if(previous[0]==null&&(count(s,"SELECT COUNT(*) FROM peers")!=0||count(s,"SELECT COUNT(*) FROM access")!=0))throw failure();
            s.execute("UPDATE meta SET device=?,root=? WHERE singleton=1",device,root);checkTime(s);return null;});
    }
    public String boundDeviceId(){return transaction(s->{String[] row=one(s.query("SELECT device FROM meta WHERE singleton=1"),1);return row[0]==null?null:WireJson.key(row[0]);});}
    public String ownerOperation(UUID operation){return transaction(s->{List<String[]> rows=s.query("SELECT result FROM operations WHERE caller=? AND operation=?",OWNER,operation.toString());if(rows.isEmpty())throw new Failure("OPERATION_UNKNOWN");String result=one(rows,1)[0];WireJson.read(result.getBytes(StandardCharsets.UTF_8),65536);return result;});}
    public String register(String caller,String principal,UUID operation,ApplicationManifest manifest) {
        requireCaller(caller);if(principal==null||principal.length()>1024||manifest==null||operation==null)throw new IllegalArgumentException("Caller provenance and declaration required.");String canonical=manifest.toJson(),digest=digest("app.register\n"+canonical);
        return transaction(s->{String retained=retained(s,caller,operation,digest);if(retained!=null)return retained;admit(s,caller,0);
            List<String[]> previous=s.query("SELECT reference,pending,pending_revision FROM applications WHERE caller=? AND app=?",caller,manifest.applicationId);String reference;String old=null;
            if(previous.isEmpty()){if(count(s,"SELECT COUNT(*) FROM applications")>=1024)throw new Failure("RESOURCE_EXHAUSTED");reference=UUID.randomUUID().toString();s.execute("INSERT INTO applications VALUES(?,?,?,?,?,1,0,0,NULL,NULL)",reference,caller,manifest.applicationId,principal,canonical);}
            else{String[] row=one(previous,3);reference=row[0];old=row[1];long revision=positive(row[2]);if(!canonical.equals(old)){if(revision==Long.MAX_VALUE)throw new Failure("RESOURCE_EXHAUSTED");s.execute("UPDATE applications SET pending=?,pending_revision=pending_revision+1,principal=? WHERE reference=?",canonical,principal,reference);}}
            String result=json(WireJson.map("applicationRef",reference,"permissionDiff",difference(old,canonical),"approvalRequired",true));record(s,caller,operation,digest,result);return result;});
    }
    public String operationStatus(String caller,UUID operation){requireCaller(caller);return transaction(s->{List<String[]> rows=s.query("SELECT result FROM operations WHERE caller=? AND operation=?",caller,operation.toString());if(rows.isEmpty())throw new Failure("OPERATION_UNKNOWN");String formal=ConnectionJournal.openOperation(s,caller,operation);return formal!=null?formal:json(WireJson.map("operationId",operation.toString(),"status","succeeded","result",WireJson.read(one(rows,1)[0].getBytes(StandardCharsets.UTF_8),65536)));});}
    public ConnectionJournal connections(){return new ConnectionJournal(this);}
    public Review review(String reference){WireJson.uuid(reference);return transaction(s->review(s,reference));}
    public List<String> listApplications(String after,int limit) {
        if(limit<1||limit>20)throw new IllegalArgumentException("Bounded page required.");
        return transaction(s->{List<String> result=new ArrayList<>();for(String[] row:s.query("SELECT reference FROM applications WHERE reference>? ORDER BY reference COLLATE BINARY LIMIT ?",after==null?"":after,Integer.toString(limit))){if(row.length!=1)throw failure();WireJson.uuid(row[0]);result.add(row[0]);}return Collections.unmodifiableList(result);});
    }
    public String decide(Review selected,String decision,UUID operation) {
        if(selected==null||!Arrays.asList("approve","deny","revoke").contains(decision))throw new IllegalArgumentException("Explicit reviewed decision required.");
        String digest=digest("review\n"+selected.reference+"\n"+selected.pendingRevision+"\n"+selected.decisionVersion+"\n"+decision);
        return transaction(s->{String old=retained(s,OWNER,operation,digest);if(old!=null)return old;Review current=review(s,selected.reference);
            if(!current.caller.equals(selected.caller)||current.pendingRevision!=selected.pendingRevision||current.decisionVersion!=selected.decisionVersion||!current.pending.equals(selected.pending))throw new Failure("REVIEW_STALE");
            boolean before=current.approved!=null,after=decision.equals("approve")||(!decision.equals("revoke")&&before);
            if(current.decisionVersion==Long.MAX_VALUE||(!decision.equals("revoke")&&after&&current.decisionVersion>=Long.MAX_VALUE-1)||(!decision.equals("deny")&&current.authorityEpoch>=Long.MAX_VALUE-1))throw new Failure("RESOURCE_EXHAUSTED");admit(s,OWNER,(after?1:0)-(before?1:0));
            String approved=decision.equals("approve")?current.pending:decision.equals("revoke")?null:current.approved;
            String revision=decision.equals("approve")?Long.toString(current.pendingRevision):decision.equals("revoke")?null:current.approvedRevision==null?null:current.approvedRevision.toString();
            long epoch=current.authorityEpoch+(decision.equals("deny")?0:1),version=current.decisionVersion+1;
            s.execute("UPDATE applications SET approved=?,approved_revision=?,decision_version=?,authority_epoch=? WHERE reference=?",approved,revision,Long.toString(version),Long.toString(epoch),current.reference);
            String result=json(WireJson.map("applicationRef",current.reference,"decision",decision,"decisionVersion",version,"authorityEpoch",epoch));record(s,OWNER,operation,digest,result);return result;});
    }
    public Peer peer(String device){WireJson.key(device);return transaction(s->peer(s,device));}
    public List<String> listPeers(String after,int limit){if(limit<1||limit>20)throw new IllegalArgumentException("Bounded page required.");return transaction(s->{List<String> result=new ArrayList<>();for(String[] row:s.query("SELECT device FROM peers WHERE device>? ORDER BY device COLLATE BINARY LIMIT ?",after==null?"":after,Integer.toString(limit))){if(row.length!=1)throw failure();result.add(WireJson.key(row[0]));}return Collections.unmodifiableList(result);});}
    public List<String> listRequests(String after,int limit){if(limit<1||limit>20)throw new IllegalArgumentException("Bounded page required.");return transaction(s->{List<String> result=new ArrayList<>();for(String[] row:s.query("SELECT reference FROM requests WHERE reference>? ORDER BY reference COLLATE BINARY LIMIT ?",after==null?"":after,Integer.toString(limit))){if(row.length!=1)throw failure();result.add(WireJson.uuid(row[0]).toString());}return Collections.unmodifiableList(result);});}
    public RequestReview inspectRequest(String reference) {
        WireJson.uuid(reference);return transaction(s->{String[] row=one(s.query("SELECT reference,caller,application,alias,device,publisher,service,reason,scope,epoch,approved_revision,peer_revision FROM requests WHERE reference=?",reference),12);Review application=review(s,row[2]);if(!row[1].equals(application.caller)||!scopeKey(row[1],row[2],row[3],row[4],row[5],row[6]).equals(row[8]))throw failure();
            List<String[]> grants=s.query("SELECT request,snapshot,revision,active FROM access WHERE scope=?",row[8]);long revision=grants.isEmpty()?0:positive(one(grants,4)[2]);String state="pending";
            try{Scope scope=resolve(s,row[1],row[2],row[3],row[4],row[5],row[6],false);if(scope.epoch!=positive(row[9])||scope.approvedRevision!=positive(row[10])||scope.peerRevision!=positive(row[11]))state="superseded";else if(!grants.isEmpty()&&grants.get(0)[0].equals(reference))state=flag(grants.get(0)[3])&&scope.json().equals(grants.get(0)[1])?"active":"revoked";}
            catch(Failure e){state="superseded";}
            return new RequestReview(row,state,application.principal,revision);});
    }
    public String trustPeer(String device,String certificate,String host,int port,long expected,UUID operation) {
        WireJson.key(device);ipv4(host);if(certificate==null||certificate.length()>65536||port<1||port>65535)throw new IllegalArgumentException("Validated peer certificate and endpoint required.");
        String digest=digest("peer.trust\n"+json(WireJson.map("device",device,"certificate",certificate,"host",host,"port",port,"expected",expected)));
        return transaction(s->{String retained=retained(s,OWNER,operation,digest);if(retained!=null)return retained;checkBoundIdentity(s);checkTime(s);Peer prior=peerOrNull(s,device);if((prior==null?0:prior.revision)!=expected)throw new Failure("REVIEW_STALE");if(expected>=Long.MAX_VALUE-1||(prior==null&&count(s,"SELECT COUNT(*) FROM peers")>=64))throw new Failure("RESOURCE_EXHAUSTED");admit(s,OWNER,prior!=null&&prior.active?0:1);
            if(prior==null)s.execute("INSERT INTO peers VALUES(?,?,?,?,?,1)",device,certificate,host,Integer.toString(port),Long.toString(expected+1));else s.execute("UPDATE peers SET certificate=?,host=?,port=?,revision=?,active=1 WHERE device=?",certificate,host,Integer.toString(port),Long.toString(expected+1),device);
            String result=json(WireJson.map("deviceId",device,"revision",expected+1,"active",true));record(s,OWNER,operation,digest,result);return result;});
    }
    public String revokePeer(String device,long expected,UUID operation) {
        String digest=digest("peer.revoke\n"+device+"\n"+expected);
        return transaction(s->{String retained=retained(s,OWNER,operation,digest);if(retained!=null)return retained;Peer prior=peer(s,device);if(prior.revision!=expected)throw new Failure("REVIEW_STALE");if(expected==Long.MAX_VALUE)throw new Failure("RESOURCE_EXHAUSTED");long grants=count(s,"SELECT COUNT(*) FROM access WHERE device=? AND active=1",device);admit(s,OWNER,(prior.active?-1:0)-(int)grants);s.execute("UPDATE peers SET active=0,revision=revision+1 WHERE device=?",device);s.execute("UPDATE access SET active=0,revision=revision+1 WHERE device=? AND active=1",device);String result=json(WireJson.map("deviceId",device,"revision",expected+1,"active",false));record(s,OWNER,operation,digest,result);return result;});
    }
    public String requestAccess(String caller,UUID operation,String application,String alias,String device,String publisher,String service,String reason) {
        requireCaller(caller);scopeFields(application,alias,device,publisher,service);if(reason==null)reason="";if(reason.codePointCount(0,reason.length())>256||reason.codePoints().anyMatch(Character::isISOControl))throw new IllegalArgumentException("Bounded reason required.");final String purpose=reason;
        String hash=digest("grant.request\n"+json(WireJson.map("applicationRef",application,"importAlias",alias,"deviceId",device,"publisherAppId",publisher,"remoteServiceId",service,"reason",purpose)));
        return transaction(s->{String retained=retained(s,caller,operation,hash);if(retained!=null)return retained;Scope scope=resolve(s,caller,application,alias,device,publisher,service,false);if(count(s,"SELECT COUNT(*) FROM requests WHERE caller=?",caller)>=1024||count(s,"SELECT COUNT(*) FROM requests")>=16384)throw new Failure("RESOURCE_EXHAUSTED");admit(s,caller,0);String reference=UUID.randomUUID().toString();
            s.execute("INSERT INTO requests VALUES(?,?,?,?,?,?,?,?,?,?,?,?)",reference,caller,scopeKey(caller,application,alias,device,publisher,service),application,alias,device,publisher,service,Long.toString(scope.epoch),Long.toString(scope.approvedRevision),Long.toString(scope.peerRevision),purpose);
            String result=json(WireJson.map("grantRequestId",reference,"pendingUserApproval",true));record(s,caller,operation,hash,result);return result;});
    }
    public String approveAccess(String request,long expected,UUID operation) {
        WireJson.uuid(request);String digest=digest("grant.approve\n"+request+"\n"+expected);
        return transaction(s->{String retained=retained(s,OWNER,operation,digest);if(retained!=null)return retained;String[] row=one(s.query("SELECT caller,scope,application,alias,device,publisher,service,epoch,approved_revision,peer_revision FROM requests WHERE reference=?",request),10);Scope scope=resolve(s,row[0],row[2],row[3],row[4],row[5],row[6],false);
            if(scope.epoch!=positive(row[7])||scope.approvedRevision!=positive(row[8])||scope.peerRevision!=positive(row[9]))throw new Failure("REVIEW_STALE");List<String[]> existing=s.query("SELECT revision,active FROM access WHERE scope=?",row[1]);long revision=existing.isEmpty()?0:positive(one(existing,2)[0]);boolean active=!existing.isEmpty()&&flag(existing.get(0)[1]);if(revision!=expected)throw new Failure("REVIEW_STALE");if(revision>=Long.MAX_VALUE-1)throw new Failure("RESOURCE_EXHAUSTED");admit(s,OWNER,active?0:1);
            if(existing.isEmpty())s.execute("INSERT INTO access VALUES(?,?,?,1,1,?)",row[1],request,scope.json(),scope.device);else s.execute("UPDATE access SET request=?,snapshot=?,revision=revision+1,active=1 WHERE scope=?",request,scope.json(),row[1]);
            String result=json(WireJson.map("grantRequestId",request,"revision",revision+1,"active",true));record(s,OWNER,operation,digest,result);return result;});
    }
    public String revokeAccess(String request,long expected,UUID operation) {
        WireJson.uuid(request);String digest=digest("grant.revoke\n"+request+"\n"+expected);
        return transaction(s->{String retained=retained(s,OWNER,operation,digest);if(retained!=null)return retained;String[] row=one(s.query("SELECT scope,revision,active FROM access WHERE request=?",request),3);long revision=positive(row[1]);if(revision!=expected)throw new Failure("REVIEW_STALE");if(revision==Long.MAX_VALUE)throw new Failure("RESOURCE_EXHAUSTED");admit(s,OWNER,flag(row[2])?-1:0);s.execute("UPDATE access SET active=0,revision=revision+1 WHERE scope=?",row[0]);String result=json(WireJson.map("grantRequestId",request,"revision",revision+1,"active",false));record(s,OWNER,operation,digest,result);return result;});
    }
    public Scope authorize(String caller,String application,String alias,String device,String publisher,String service){requireCaller(caller);scopeFields(application,alias,device,publisher,service);return transaction(s->resolve(s,caller,application,alias,device,publisher,service,true));}
    Scope resolve(Session s,String caller,String application,String alias,String device,String publisher,String service,boolean requireGrant) {
        checkBoundIdentity(s);checkTime(s);Review review=review(s,application);if(!review.caller.equals(caller)||review.approved==null||review.authorityEpoch<=0)throw new Failure("AUTH_REQUIRED");ApplicationManifest manifest=ManifestContract.parse(review.approved.getBytes(StandardCharsets.UTF_8));ApplicationManifest.Import item=null;for(ApplicationManifest.Import candidate:manifest.imports)if(candidate.alias.equals(alias))item=candidate;
        Peer peer=peer(s,device);if(item==null||!peer.active)throw new Failure("AUTH_REQUIRED");if(!item.endpointMode.equals("brokered")||item.connectionPolicy.prewarm||item.connectionPolicy.relayBudgetBytes!=null||item.connectionPolicy.relayWarningBytes!=null)throw new Failure("CAPABILITY_UNSUPPORTED");
        Scope ungranted=new Scope(caller,review,item,peer,publisher,service,0);long revision=0;
        if(requireGrant){String key=scopeKey(caller,application,alias,device,publisher,service);List<String[]> rows=s.query("SELECT snapshot,revision,active FROM access WHERE scope=?",key);if(rows.isEmpty())throw new Failure("AUTH_REQUIRED");String[] row=one(rows,3);revision=positive(row[1]);if(!flag(row[2])||!ungranted.json().equals(row[0]))throw new Failure("AUTH_REQUIRED");}
        return new Scope(caller,review,item,peer,publisher,service,revision);
    }
    public Open beginOpen(String caller,UUID operation,String application,String alias,String device,String publisher,String service,UUID boot) {
        return beginOpen(caller,operation,application,alias,device,publisher,service,boot,false);
    }
    public Open beginRelayOpen(String caller,UUID operation,String application,String alias,String device,String publisher,String service,UUID boot) {
        return beginOpen(caller,operation,application,alias,device,publisher,service,boot,true);
    }
    private Open beginOpen(String caller,UUID operation,String application,String alias,String device,String publisher,String service,UUID boot,boolean relay) {
        requireCaller(caller);scopeFields(application,alias,device,publisher,service);String hash=digest((relay?"secure.relay.open\n":"secure.connection.open\n")+json(WireJson.map("applicationRef",application,"importAlias",alias,"peer",device,"publisher",publisher,"service",service)));
        return transaction(s->{String retained=retained(s,caller,operation,hash);if(retained!=null)return readOpen(s,caller,operation,null,false);Scope scope=resolve(s,caller,application,alias,device,publisher,service,true);if(relay?(!scope.allowRelay||scope.policyMode.equals("direct-only")):(!scope.policyMode.equals("direct-only")||scope.allowRelay))throw new Failure("CAPABILITY_UNSUPPORTED");if(count(s,"SELECT COUNT(*) FROM opens WHERE caller=? AND state!='closed'",caller)+count(s,"SELECT COUNT(*) FROM connections WHERE caller=? AND terminal=0",caller)>=128||count(s,"SELECT COUNT(*) FROM opens WHERE state!='closed'")+count(s,"SELECT COUNT(*) FROM connections WHERE terminal=0")>=1024)throw new Failure("RESOURCE_EXHAUSTED");admit(s,caller,0);String reference=UUID.randomUUID().toString();s.execute("INSERT INTO opens VALUES(?,?,?,?,'pending',NULL,?)",caller,operation.toString(),reference,boot.toString(),scope.json());record(s,caller,operation,hash,json(WireJson.map("connectionId",reference,"path",relay?"relay":"direct")));return readOpen(s,caller,operation,scope,true);});
    }
    public Open openStatus(String caller,UUID operation){requireCaller(caller);return transaction(s->readOpen(s,caller,operation,null,false));}
    public void setOpen(String caller,UUID operation,UUID boot,String state,String error) {
        if(!Arrays.asList("ready","closed").contains(state)||error!=null&&!Arrays.asList("CONNECTION_CLOSED","DIRECT_FAILED","RELAY_FAILED","AUTH_REQUIRED","AGENT_RESTARTED","RESOURCE_EXHAUSTED","GATEWAY_STOPPED").contains(error))throw new IllegalArgumentException("Fixed connection state required.");
        transaction(s->{s.execute("UPDATE opens SET state=?,error=? WHERE caller=? AND operation=? AND boot=? AND state!='closed'",state,error,caller,operation.toString(),boot.toString());return null;});
    }
    public void recoverOpens(UUID boot){transaction(s->{s.execute("UPDATE opens SET state='closed',error='AGENT_RESTARTED' WHERE boot!=? AND state!='closed'",boot.toString());return null;});}
    public boolean[] currentScopes(List<Scope> scopes) {
        boolean[] valid=new boolean[scopes.size()];if(scopes.isEmpty())return valid;
        try{return transaction(s->{for(int i=0;i<scopes.size();i++){Scope expected=scopes.get(i);try{valid[i]=resolve(s,expected.caller,expected.application,expected.alias,expected.device,expected.publisher,expected.service,true).json().equals(expected.json());}catch(Failure e){valid[i]=false;}}return valid;});}catch(RuntimeException e){return new boolean[scopes.size()];}
    }
    private static Review review(Session s,String reference){List<String[]> rows=s.query("SELECT reference,caller,principal,pending,pending_revision,decision_version,authority_epoch,approved,approved_revision FROM applications WHERE reference=?",reference);if(rows.isEmpty())throw new Failure("AUTH_REQUIRED");return new Review(one(rows,9));}
    private static Peer peer(Session s,String id){Peer peer=peerOrNull(s,id);if(peer==null)throw new Failure("AUTH_REQUIRED");return peer;}
    private static Peer peerOrNull(Session s,String id){List<String[]> rows=s.query("SELECT device,certificate,host,port,revision,active FROM peers WHERE device=?",id);return rows.isEmpty()?null:new Peer(one(rows,6));}
    private static Open readOpen(Session s,String caller,UUID operation,Scope scope,boolean created){List<String[]> rows=s.query("SELECT caller,operation,reference,boot,state,error FROM opens WHERE caller=? AND operation=?",caller,operation.toString());if(rows.isEmpty())throw new Failure("OPERATION_UNKNOWN");String[] row=one(rows,6);String outcome=one(s.query("SELECT result FROM operations WHERE caller=? AND operation=?",caller,operation.toString()),1)[0];Map<String,Object> result=WireJson.read(outcome.getBytes(StandardCharsets.UTF_8),65536);String path="direct";if(result.containsKey("path")){WireJson.fields(result,"connectionId","path");path=WireJson.text(result.get("path"),6);if(!Arrays.asList("direct","relay").contains(path))throw failure();}else WireJson.fields(result,"connectionId");if(!row[2].equals(result.get("connectionId")))throw failure();return new Open(row,scope,created,path);}
    private void checkTime(Session s){long previous=count(s,"SELECT max_utc FROM meta WHERE singleton=1"),now=clock.millis();if(now<0||now<previous-120000)throw new Failure("CLOCK_UNTRUSTED");if(now>previous+30000)s.execute("UPDATE meta SET max_utc=? WHERE singleton=1",Long.toString(now));}
    private static void checkBoundIdentity(Session s){String[] row=one(s.query("SELECT device,root FROM meta WHERE singleton=1"),2);if(row[0]==null||row[1]==null)throw new Failure("AUTH_REQUIRED");WireJson.key(row[0]);WireJson.key(row[1]);}
    void admit(Session s,String caller,int reservationDelta) {
        long retained=count(s,"SELECT COUNT(*) FROM operations"),host=count(s,"SELECT COUNT(*) FROM operations WHERE caller=?",OWNER),reserved=count(s,"SELECT (SELECT COUNT(*) FROM applications WHERE approved IS NOT NULL)+(SELECT COUNT(*) FROM peers WHERE active=1)+(SELECT COUNT(*) FROM access WHERE active=1)"),cost=1L+reservationDelta;
        if(retained+reserved+cost>totalLimit||(!caller.equals(OWNER)&&count(s,"SELECT COUNT(*) FROM operations WHERE caller=?",caller)>=callerLimit)||(caller.equals(OWNER)&&host+reserved+cost>callerLimit))throw new Failure("RESOURCE_EXHAUSTED");
    }
    static String retained(Session s,String caller,UUID operation,String digest){if(operation==null)throw new IllegalArgumentException("Operation UUID required.");List<String[]> rows=s.query("SELECT digest,result FROM operations WHERE caller=? AND operation=?",caller,operation.toString());if(rows.isEmpty())return null;String[] row=one(rows,2);if(!digest.equals(row[0]))throw new Failure("OPERATION_CONFLICT");WireJson.read(row[1].getBytes(StandardCharsets.UTF_8),65536);return row[1];}
    static void record(Session s,String caller,UUID operation,String digest,String result){s.execute("INSERT INTO operations VALUES(?,?,?,?)",caller,operation.toString(),digest,result);}
    private static String scopeKey(String caller,String application,String alias,String device,String publisher,String service){return digest(json(WireJson.map("caller",caller,"application",application,"alias",alias,"device",device,"publisher",publisher,"service",service)));}
    private static void scopeFields(String application,String alias,String device,String publisher,String service){WireJson.uuid(application);WireJson.key(device);if(alias==null||!alias.matches("[a-z][a-z0-9-]{0,31}")||service==null||!service.matches("[a-z][a-z0-9-]{0,31}")||publisher==null||publisher.length()>128||!publisher.matches("[a-z][a-z0-9]*(?:[.-][a-z0-9]+)+"))throw new IllegalArgumentException("Invalid service scope.");}
    static String[] one(List<String[]> rows,int columns){if(rows.size()!=1||rows.get(0).length!=columns)throw failure();return rows.get(0);}
    static long count(Session s,String sql,String... parameters){return nonnegative(one(s.query(sql,parameters),1)[0]);}
    static long positive(String value){long n=nonnegative(value);if(n<1)throw failure();return n;}
    static long nonnegative(String value){if(value==null||!value.matches("0|[1-9][0-9]*"))throw failure();try{return Long.parseLong(value);}catch(NumberFormatException e){throw failure();}}
    private static boolean flag(String value){if(!"0".equals(value)&&!"1".equals(value))throw failure();return "1".equals(value);}
    static void requireCaller(String caller){if(caller==null||caller.length()<1||caller.length()>128||caller.startsWith("@"))throw new IllegalArgumentException("Actual caller scope required.");}
    public static String ipv4(String value){if(value==null||value.length()>15)throw new IllegalArgumentException("IPv4 literal required.");String[] parts=value.split("\\.",-1);if(parts.length!=4)throw new IllegalArgumentException("IPv4 literal required.");for(String part:parts)if(!part.matches("0|[1-9][0-9]{0,2}")||Integer.parseInt(part)>255)throw new IllegalArgumentException("IPv4 literal required.");if(value.equals("0.0.0.0")||value.equals("255.255.255.255"))throw new IllegalArgumentException("Specific IPv4 required.");return value;}
    static String digest(String text){try{byte[] bytes=MessageDigest.getInstance("SHA-256").digest(text.getBytes(StandardCharsets.UTF_8));StringBuilder output=new StringBuilder(64);for(byte value:bytes){output.append(Character.forDigit((value>>>4)&15,16));output.append(Character.forDigit(value&15,16));}return output.toString();}catch(java.security.NoSuchAlgorithmException e){throw failure();}}
    static String json(Object value){return new String(WireJson.write(value,65536),StandardCharsets.UTF_8);}
    private static Map<String,Object> difference(String old,String current) {
        Map<String,Object> result=new LinkedHashMap<>();for(String category:Arrays.asList("exports","imports")){String key=category.equals("exports")?"serviceId":"alias",suffix=category.equals("exports")?"Exports":"Imports";Map<String,String> before=items(old,category,key),after=items(current,category,key);List<String> added=new ArrayList<>(),removed=new ArrayList<>(),changed=new ArrayList<>();for(String id:after.keySet())if(!before.containsKey(id))added.add(id);else if(!before.get(id).equals(after.get(id)))changed.add(id);for(String id:before.keySet())if(!after.containsKey(id))removed.add(id);result.put("added"+suffix,added);result.put("removed"+suffix,removed);result.put("changed"+suffix,changed);}result.put("declarationChanged",!current.equals(old));return result;
    }
    private static Map<String,String> items(String manifest,String category,String key){Map<String,String> result=new LinkedHashMap<>();if(manifest==null)return result;Object value=WireJson.read(manifest.getBytes(StandardCharsets.UTF_8),49152).get(category);if(!(value instanceof List<?>))throw failure();for(Object item:(List<?>)value){Map<String,Object> record=WireJson.object(item);result.put(WireJson.text(record.get(key),32),json(record));}return result;}
    private static Failure failure(){return new Failure("STORAGE_UNAVAILABLE");}
    private static void verifySchema(Session s){if(count(s,"PRAGMA user_version")!=2)throw failure();for(String name:Arrays.asList("meta","applications","operations","peers","requests","access","opens","connections","connection_attachments"))if(count(s,"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=?",name)!=1)throw failure();}
}
