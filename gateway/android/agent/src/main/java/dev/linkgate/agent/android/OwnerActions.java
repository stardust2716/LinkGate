package dev.linkgate.agent.android;

import android.content.Context;
import android.util.AtomicFile;
import dev.linkgate.agent.AgentRegistry;
import dev.linkgate.contracts.WireJson;
import java.io.File;
import java.io.FileOutputStream;
import java.io.IOException;
import java.util.Map;
import java.util.UUID;

final class OwnerActions {
    interface Decision { String apply(UUID operation)throws Exception; }
    private final AgentRegistry registry;private final AtomicFile receipt;
    OwnerActions(Context context,AgentRegistry registry){this.registry=registry;receipt=new AtomicFile(new File(context.getNoBackupFilesDir(),"owner-intent-v1.json"));}
    synchronized boolean pending()throws IOException {Map<String,Object> value=read();return value!=null&&!Boolean.TRUE.equals(value.get("confirmed"));}
    synchronized String execute(String method,String field,String subject,Decision decision)throws Exception {
        if(pending())throw new IllegalStateException("OWNER_RECOVERY_REQUIRED");UUID operation=UUID.randomUUID();
        Map<String,Object> intent=WireJson.map("operationId",operation.toString(),"method",method,"field",field,"subject",subject,"confirmed",false);write(intent);
        try{String result=decision.apply(operation);confirm(intent,result);return result;}
        catch(AgentRegistry.Failure e){if(!e.code.equals("STORAGE_UNAVAILABLE")){intent.put("confirmed",true);write(intent);}throw e;}
    }
    synchronized String recover()throws Exception {
        Map<String,Object> intent=read();if(intent==null||Boolean.TRUE.equals(intent.get("confirmed")))return "没有待恢复的操作";
        try{String result=registry.ownerOperation(WireJson.uuid(intent.get("operationId")));confirm(intent,result);return "已确认原操作结果，未重复提交";}
        catch(AgentRegistry.Failure e){if(e.code.equals("OPERATION_UNKNOWN"))return "原操作未记录。核对当前状态后，可选择放弃本次等待。";throw e;}
    }
    synchronized void abandon()throws Exception {
        Map<String,Object> intent=read();if(intent==null||Boolean.TRUE.equals(intent.get("confirmed")))return;
        try{registry.ownerOperation(WireJson.uuid(intent.get("operationId")));throw new IllegalStateException("RECOVER_EXISTING_RESULT_FIRST");}
        catch(AgentRegistry.Failure e){if(!e.code.equals("OPERATION_UNKNOWN"))throw e;intent.put("confirmed",true);write(intent);}
    }
    private void confirm(Map<String,Object> intent,String encoded)throws IOException {
        Map<String,Object> value=WireJson.read(encoded.getBytes(java.nio.charset.StandardCharsets.UTF_8),65536);String field=WireJson.text(intent.get("field"),32);
        if(!intent.get("subject").equals(value.get(field)))throw new IOException("Owner outcome mismatch.");intent.put("confirmed",true);write(intent);
    }
    private Map<String,Object> read()throws IOException {
        if(!receipt.getBaseFile().exists()&&!new File(receipt.getBaseFile().getPath()+".bak").exists())return null;
        check();try(var input=receipt.openRead()){java.io.ByteArrayOutputStream bytes=new java.io.ByteArrayOutputStream();byte[] buffer=new byte[1024];int n;while((n=input.read(buffer))!=-1){if(bytes.size()+n>4096)throw new IOException("Owner receipt exceeds limit.");bytes.write(buffer,0,n);}Map<String,Object> value=WireJson.read(bytes.toByteArray(),4096);WireJson.fields(value,"operationId","method","field","subject","confirmed");WireJson.uuid(value.get("operationId"));if(!(value.get("confirmed") instanceof Boolean))throw new IOException("Invalid owner receipt.");return value;}
        catch(RuntimeException e){throw new IOException("Owner receipt unavailable.");}
    }
    private void write(Map<String,Object> value)throws IOException {
        check();FileOutputStream output=null;try{output=receipt.startWrite();output.write(WireJson.write(value,4096));output.getFD().sync();receipt.finishWrite(output);}catch(Exception e){if(output!=null)receipt.failWrite(output);throw new IOException("Owner receipt unavailable.");}
    }
    private void check()throws IOException {for(String suffix:new String[]{"",".bak",".new"}){File path=new File(receipt.getBaseFile().getPath()+suffix);if(!path.getCanonicalFile().equals(path.getAbsoluteFile()))throw new IOException("Linked owner receipt rejected.");}}
}
