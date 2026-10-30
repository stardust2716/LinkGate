package dev.linkgate.agent.android;

import android.content.Context;
import android.database.Cursor;
import android.database.sqlite.SQLiteDatabase;
import android.system.Os;
import dev.linkgate.agent.AgentRegistry;
import java.io.File;
import java.util.ArrayList;
import java.util.List;

final class AndroidDatabase implements AgentRegistry.Database {
    private final File directory;
    AndroidDatabase(Context context) {
        try{File root=context.getNoBackupFilesDir().getCanonicalFile();directory=new File(root,"authority-v1");if(!directory.getCanonicalFile().equals(directory)||(!directory.isDirectory()&&!directory.mkdir()))throw new java.io.IOException();Os.chmod(directory.getPath(),0700);check();}
        catch(Exception e){throw storage();}
    }
    private void check()throws java.io.IOException {
        if(!directory.getCanonicalFile().equals(directory))throw new java.io.IOException();
        for(String name:new String[]{"state.db","state.db-journal","state.db-wal","state.db-shm"}){File file=new File(directory,name);if(!file.getCanonicalFile().equals(file))throw new java.io.IOException();}
    }
    @Override public AgentRegistry.Session open() {
        SQLiteDatabase db=null;
        try {
            check();db=SQLiteDatabase.openDatabase(new File(directory,"state.db").getPath(),null,SQLiteDatabase.CREATE_IF_NECESSARY|SQLiteDatabase.NO_LOCALIZED_COLLATORS,corrupted->{throw storage();});db.disableWriteAheadLogging();
            try(Cursor cursor=db.rawQuery("PRAGMA journal_mode=DELETE",null)){if(!cursor.moveToFirst()||!"delete".equals(cursor.getString(0)))throw storage();}
            db.execSQL("PRAGMA synchronous=FULL");try(Cursor ignored=db.rawQuery("PRAGMA busy_timeout=1000",null)){if(!ignored.moveToFirst())throw storage();}
            if(db.setMaximumSize(1L<<30)>(1L<<30))throw storage();return new Session(db);
        }catch(Exception e){if(db!=null)db.close();throw storage();}
    }
    private static final class Session implements AgentRegistry.Session {
        private final SQLiteDatabase db;private boolean transaction,closed;
        Session(SQLiteDatabase db){this.db=db;}
        @Override public void begin(){db.beginTransactionNonExclusive();transaction=true;}
        @Override public void execute(String sql,String... parameters){if(parameters.length==0)db.execSQL(sql);else db.execSQL(sql,parameters);}
        @Override public List<String[]> query(String sql,String... parameters){
            List<String[]> rows=new ArrayList<>();try(Cursor cursor=db.rawQuery(sql,parameters)){while(cursor.moveToNext()){if(rows.size()>=64)throw storage();String[] row=new String[cursor.getColumnCount()];for(int i=0;i<row.length;i++)row[i]=cursor.isNull(i)?null:cursor.getString(i);rows.add(row);}}return rows;
        }
        @Override public void commit(){db.setTransactionSuccessful();db.endTransaction();transaction=false;}
        @Override public void close(){if(closed)return;closed=true;try{if(transaction)db.endTransaction();}finally{db.close();}}
    }
    private static AgentRegistry.Failure storage(){return new AgentRegistry.Failure("STORAGE_UNAVAILABLE");}
}
