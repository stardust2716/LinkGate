package dev.linkgate.agent;

import dev.linkgate.contracts.ConnectionOpenRequest;
import dev.linkgate.contracts.ConnectionStatus;
import dev.linkgate.contracts.WireJson;
import java.nio.charset.StandardCharsets;
import java.util.Arrays;
import java.util.List;
import java.util.Map;
import java.util.UUID;
import java.util.function.LongSupplier;
import static dev.linkgate.agent.AgentRegistry.*;

/** Agent-owned durable state; callers supply an OS-captured principal, not request identity. */
public final class ConnectionJournal {
    public static final class Accepted {
        public final Scope scope;public final ConnectionStatus status;public final boolean created;
        private Accepted(Scope scope,ConnectionStatus status,boolean created){this.scope=scope;this.status=status;this.created=created;}
    }
    private static final class Row {
        final String caller,operation,request,scope;final long sequence;final boolean terminal;final ConnectionStatus status;
        Row(String[] row){caller=row[0];operation=row[1];request=row[2];scope=row[3];sequence=positive(row[4]);terminal=row[5].equals("1");if(!terminal&&!row[5].equals("0"))throw new Failure("STORAGE_UNAVAILABLE");status=ConnectionStatus.parse(row[6].getBytes(StandardCharsets.UTF_8));if(!status.connectionId.equals(row[7])||!status.bootId.equals(row[8])||terminal!=(status.closedUtc!=null))throw new Failure("STORAGE_UNAVAILABLE");}
    }
    private final AgentRegistry registry;
    ConnectionJournal(AgentRegistry registry){this.registry=registry;}
    public Accepted begin(String caller,UUID operation,ConnectionOpenRequest request,UUID boot,String generation,long utc,long admitted,LongSupplier clock){
        requireCaller(caller);String hash=digest("connection.open\n"+request.toJson());
        return registry.transaction(s->{
            if(retained(s,caller,operation,hash)!=null){Row prior=readOperation(s,caller,operation);if(!prior.request.equals(request.toJson()))throw new Failure("STORAGE_UNAVAILABLE");return new Accepted(null,prior.status,false);}
            Scope scope=registry.resolve(s,caller,request.applicationRef,request.importAlias,request.deviceId,request.publisherAppId,request.remoteServiceId,true);
            if(!request.withinApproved(scope.policy))throw new Failure("PERMISSION_DENIED");
            if(count(s,"SELECT COUNT(*) FROM opens WHERE caller=? AND state!='closed'",caller)+count(s,"SELECT COUNT(*) FROM connections WHERE caller=? AND terminal=0",caller)>=128||count(s,"SELECT COUNT(*) FROM opens WHERE state!='closed'")+count(s,"SELECT COUNT(*) FROM connections WHERE terminal=0")>=1024)throw new Failure("RESOURCE_EXHAUSTED");
            registry.admit(s,caller,0);String reference=UUID.randomUUID().toString(),owner=one(s.query("SELECT root FROM meta WHERE singleton=1"),1)[0];
            ConnectionStatus status=new ConnectionMeter(reference,owner,request.deviceId,request.publisherAppId,request.remoteServiceId,boot.toString(),generation,utc,admitted,clock).snapshot().status;
            s.execute("INSERT INTO connections VALUES(?,?,?,?,?,?,1,0,?)",reference,caller,operation.toString(),boot.toString(),request.toJson(),scope.json(),status.toJson());record(s,caller,operation,hash,json(WireJson.map("connectionId",reference)));return new Accepted(scope,status,true);
        });
    }
    public ConnectionStatus status(String caller,String reference){requireCaller(caller);WireJson.uuid(reference);return registry.transaction(s->read(s,caller,reference).status);}
    public ConnectionStatus atOperation(String caller,UUID operation){requireCaller(caller);return registry.transaction(s->readOperation(s,caller,operation).status);}
    public boolean retainedOpen(String caller,UUID operation,ConnectionOpenRequest request){requireCaller(caller);return registry.transaction(s->{if(retained(s,caller,operation,digest("connection.open\n"+request.toJson()))==null)return false;readOperation(s,caller,operation);return true;});}
    public boolean publish(String caller,UUID boot,ConnectionMeter.Snapshot update){requireCaller(caller);return registry.transaction(s->{Row prior=read(s,caller,update.status.connectionId);return publish(s,prior,boot,update.sequence,update.status);});}
    private static boolean publish(Session s,Row prior,UUID boot,long sequence,ConnectionStatus next){
        ConnectionStatus old=prior.status;if(!old.bootId.equals(boot.toString())||prior.terminal||sequence<=prior.sequence)return false;
        if(!old.connectionId.equals(next.connectionId)||!old.bootId.equals(next.bootId)||!old.networkGeneration.equals(next.networkGeneration)||!old.ownerId.equals(next.ownerId)||!old.deviceId.equals(next.deviceId)||!old.publisherAppId.equals(next.publisherAppId)||!old.serviceId.equals(next.serviceId)||!old.acceptedUtc.equals(next.acceptedUtc)||!old.selectedPath.equals("none")&&!old.selectedPath.equals(next.selectedPath)||old.readyUtc!=null&&!old.readyUtc.equals(next.readyUtc)||old.setupMs!=null&&!old.setupMs.equals(next.setupMs)||next.directTx.compareTo(old.directTx)<0||next.directRx.compareTo(old.directRx)<0||next.relayTx.compareTo(old.relayTx)<0||next.relayRx.compareTo(old.relayRx)<0)throw new Failure("STORAGE_UNAVAILABLE");
        if(next.closedUtc==null&&Arrays.asList("pending","attaching","open","draining").indexOf(next.flowState)<Arrays.asList("pending","attaching","open","draining").indexOf(old.flowState))throw new Failure("STORAGE_UNAVAILABLE");
        s.execute("UPDATE connections SET sequence=?,terminal=?,snapshot=? WHERE reference=? AND caller=? AND boot=? AND terminal=0 AND sequence=?",Long.toString(sequence),next.closedUtc==null?"0":"1",next.toJson(),next.connectionId,prior.caller,boot.toString(),Long.toString(prior.sequence));return true;
    }
    static String openOperation(Session s,String caller,UUID operation){
        List<String[]> rows=s.query("SELECT caller,operation,request,scope,sequence,terminal,snapshot,reference,boot FROM connections WHERE caller=? AND operation=?",caller,operation.toString());if(rows.isEmpty())return null;ConnectionStatus status=new Row(one(rows,9)).status;
        if(status.readyUtc!=null)return success(operation,WireJson.map("connectionId",status.connectionId));
        if(status.closedUtc!=null)return json(WireJson.map("operationId",operation.toString(),"status","failed","error",status.wire().get("error")));
        return json(WireJson.map("operationId",operation.toString(),"status","pending"));
    }
    public String attach(String caller,UUID operation,String reference,UUID boot){requireCaller(caller);WireJson.uuid(reference);String hash=digest("connection.attach\n"+reference);return registry.transaction(s->{
        String prior=retained(s,caller,operation,hash);if(prior!=null)return success(operation,WireJson.read(prior.getBytes(StandardCharsets.UTF_8),16384));
        Row row=read(s,caller,reference);if(row.terminal||!row.status.bootId.equals(boot.toString()))throw new Failure("CONNECTION_CLOSED");if(!row.status.flowState.equals("attaching"))throw new Failure("CONNECTION_NOT_READY");current(s,row);
        if(count(s,"SELECT COUNT(*) FROM connection_attachments WHERE reference=?",reference)!=0)throw new Failure("STREAM_ALREADY_ATTACHED");registry.admit(s,caller,0);String stream=UUID.randomUUID().toString();String result=json(WireJson.map("streamRef",stream));
        s.execute("INSERT INTO connection_attachments VALUES(?,?,?,?,?,0)",reference,caller,operation.toString(),stream,boot.toString());record(s,caller,operation,hash,result);return success(operation,WireJson.map("streamRef",stream));
    });}
    public String consume(String caller,String stream,UUID boot){requireCaller(caller);WireJson.uuid(stream);return registry.transaction(s->{
        List<String[]> rows=s.query("SELECT reference FROM connection_attachments WHERE caller=? AND stream=? AND boot=? AND consumed=0",caller,stream,boot.toString());if(rows.isEmpty())throw new Failure("AUTH_REQUIRED");String reference=one(rows,1)[0];Row row=read(s,caller,reference);if(row.terminal||!row.status.bootId.equals(boot.toString())||!row.status.flowState.equals("attaching"))throw new Failure("AUTH_REQUIRED");current(s,row);s.execute("UPDATE connection_attachments SET consumed=1 WHERE reference=? AND caller=? AND consumed=0",reference,caller);return reference;
    });}
    private Scope current(Session s,Row row){ConnectionOpenRequest request=ConnectionOpenRequest.parse(row.request.getBytes(StandardCharsets.UTF_8));Scope scope=registry.resolve(s,row.caller,request.applicationRef,request.importAlias,request.deviceId,request.publisherAppId,request.remoteServiceId,true);if(!scope.json().equals(row.scope)||!request.withinApproved(scope.policy))throw new Failure("AUTH_REQUIRED");return scope;}
    public String prepareClose(String caller,UUID operation,String reference){requireCaller(caller);WireJson.uuid(reference);String hash=digest("connection.close\n"+reference);return registry.transaction(s->{String previous=retained(s,caller,operation,hash);if(previous==null){read(s,caller,reference);registry.admit(s,caller,0);record(s,caller,operation,hash,json(WireJson.map("connectionId",reference)));}return success(operation,WireJson.map("connectionId",reference));});}
    public void terminate(String caller,String reference,String code,long utc){requireCaller(caller);registry.transaction(s->{Row prior=read(s,caller,reference);if(!prior.terminal){ConnectionStatus closed=ConnectionMeter.recovered(prior.status,code,utc);publish(s,prior,UUID.fromString(prior.status.bootId),Math.addExact(prior.sequence,1),closed);}return null;});}
    public void recover(UUID boot,long utc){while(registry.transaction(s->{List<String[]> rows=s.query("SELECT caller,operation,request,scope,sequence,terminal,snapshot,reference,boot FROM connections WHERE terminal=0 AND boot<>? LIMIT 32",boot.toString());for(String[] value:rows){Row row=new Row(value);publish(s,row,UUID.fromString(row.status.bootId),Math.addExact(row.sequence,1),ConnectionMeter.recovered(row.status,"GATEWAY_RESTARTED",utc));}return rows.size();})!=0){}}
    private static Row read(Session s,String caller,String reference){List<String[]> rows=s.query("SELECT caller,operation,request,scope,sequence,terminal,snapshot,reference,boot FROM connections WHERE reference=? AND caller=?",reference,caller);if(rows.isEmpty())throw new Failure("OPERATION_UNKNOWN");return new Row(one(rows,9));}
    private static Row readOperation(Session s,String caller,UUID operation){List<String[]> rows=s.query("SELECT caller,operation,request,scope,sequence,terminal,snapshot,reference,boot FROM connections WHERE operation=? AND caller=?",operation.toString(),caller);if(rows.isEmpty())throw new Failure("OPERATION_UNKNOWN");return new Row(one(rows,9));}
    private static String success(UUID operation,Object result){return json(WireJson.map("operationId",operation.toString(),"status","succeeded","result",result));}
}
