package dev.linkgate.agent.android;

import android.content.Context;
import dev.linkgate.agent.AgentRegistry;

final class AgentRepository {
    private static AgentRepository instance;
    final AgentRegistry registry;
    final AndroidIdentity identity;
    final OwnerActions actions;
    private AgentRepository(Context context){registry=new AgentRegistry(new AndroidDatabase(context.getApplicationContext()),System::currentTimeMillis);registry.recoverOpens(java.util.UUID.randomUUID());identity=new AndroidIdentity(registry);actions=new OwnerActions(context,registry);}
    static synchronized AgentRepository get(Context context){if(instance==null)instance=new AgentRepository(context);return instance;}
}
