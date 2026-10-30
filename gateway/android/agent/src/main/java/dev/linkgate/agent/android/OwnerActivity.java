package dev.linkgate.agent.android;

import android.Manifest;
import android.app.Activity;
import android.app.AlertDialog;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.view.Gravity;
import android.view.View;
import android.widget.Button;
import android.widget.FrameLayout;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;
import dev.linkgate.agent.AgentRegistry;
import dev.linkgate.contracts.WireJson;
import java.io.ByteArrayOutputStream;
import java.nio.ByteBuffer;
import java.nio.charset.CodingErrorAction;
import java.nio.charset.StandardCharsets;
import java.util.List;
import java.util.Map;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.ThreadPoolExecutor;
import java.util.concurrent.ArrayBlockingQueue;
import java.util.concurrent.TimeUnit;

public final class OwnerActivity extends Activity {
    private final ExecutorService work=new ThreadPoolExecutor(1,1,0,TimeUnit.SECONDS,new ArrayBlockingQueue<>(8));
    private final Handler main=new Handler(Looper.getMainLooper());
    private AgentRepository repository;private OwnerUi ui;private LinearLayout page,content;
    private String tab="home",list="reviews",cursor,leaf,export;private volatile long uiGeneration;private boolean resumed;
    private Snapshot snapshot=new Snapshot();private TextView statusTitle,statusDetail,statusBadge;private Button startButton;
    private static final int LEAF=41,ROOT=42,PEER=43,EXPORT=44,SERVER=45;
    private final Runnable tick=new Runnable(){@Override public void run(){if(!resumed)return;updateStatus();main.postDelayed(this,700);}};
    private static final class Snapshot {String id,issue;boolean enrolled,pending;ServerSettings server;}
    @Override public void onCreate(Bundle state){
        super.onCreate(state);ui=new OwnerUi(this);
        if(state!=null){tab=state.getString("tab","home");list=state.getString("list","reviews");leaf=state.getString("leaf");export=state.getString("export");}
        try{repository=AgentRepository.get(this);render();}catch(RuntimeException e){LinearLayout failed=ui.column();failed.setPadding(ui.dp(24),ui.dp(60),ui.dp(24),ui.dp(24));failed.setBackgroundColor(OwnerUi.BG);ui.empty(failed,"identity","暂时无法读取设备数据","请保留现有数据并检查恢复方案。LinkGate 没有重建或清除你的身份。");setContentView(failed);}
    }
    @Override protected void onResume(){super.onResume();resumed=true;if(repository!=null)render();main.removeCallbacks(tick);main.post(tick);}
    @Override protected void onPause(){resumed=false;main.removeCallbacks(tick);super.onPause();}
    @Override protected void onSaveInstanceState(Bundle state){super.onSaveInstanceState(state);state.putString("tab",tab);state.putString("list",list);state.putString("leaf",leaf);state.putString("export",export);}
    private void navigate(String destination){tab=destination;cursor=null;render();}
    private void render(){
        if(isDestroyed())return;
        final long generation=++uiGeneration;final String selected=tab,after=cursor,selectedList=list;
        statusTitle=null;statusDetail=null;statusBadge=null;startButton=null;
        LinearLayout root=ui.column();root.setBackgroundColor(OwnerUi.BG);
        ScrollView scroll=new ScrollView(this);scroll.setFillViewport(true);scroll.setClipToPadding(false);
        FrameLayout width=new FrameLayout(this);page=ui.column();page.setPadding(ui.dp(22),ui.dp(24),ui.dp(22),ui.dp(20));
        int screenDp=(int)(getResources().getDisplayMetrics().widthPixels/getResources().getDisplayMetrics().density);
        FrameLayout.LayoutParams centered=new FrameLayout.LayoutParams(screenDp>720?ui.dp(680):-1,-2,Gravity.TOP|Gravity.CENTER_HORIZONTAL);width.addView(page,centered);scroll.addView(width);root.addView(scroll,new LinearLayout.LayoutParams(-1,0,1));
        LinearLayout nav=ui.navigation(tab,this::navigate);root.addView(nav,new LinearLayout.LayoutParams(-1,-2));
        root.setOnApplyWindowInsetsListener((v,insets)->{int top=insets.getSystemWindowInsetTop(),bottom=insets.getSystemWindowInsetBottom();if(Build.VERSION.SDK_INT>=30){android.graphics.Insets safe=insets.getInsets(android.view.WindowInsets.Type.systemBars()|android.view.WindowInsets.Type.displayCutout());top=safe.top;bottom=safe.bottom;}root.setPadding(0,top,0,0);nav.setPadding(ui.dp(12),ui.dp(8),ui.dp(12),ui.dp(8)+bottom);return insets;});
        setContentView(root);root.requestApplyInsets();
        if(tab.equals("home"))home();else if(tab.equals("peers"))devices();else if(tab.equals("apps"))applications();else settings();
        if(tab.equals("peers")||tab.equals("apps")){
            TextView loading=ui.text("正在读取…",13,OwnerUi.MUTED,false);ui.add(content,loading);
            background(()->{
                List<String> refs=selected.equals("peers")?repository.registry.listPeers(after,20):selectedList.equals("reviews")?repository.registry.listApplications(after,20):repository.registry.listRequests(after,20);
                post(generation,()->content.removeView(loading));
                if(refs.isEmpty())post(generation,()->ui.empty(content,selected.equals("peers")?"peers":"apps",selected.equals("peers")?"还没有添加设备":selectedList.equals("reviews")?"还没有应用接入":"暂无访问请求",selected.equals("peers")?"添加电脑的公开配置，确认后即可为应用申请连接。":"应用接入 LinkGate 后，请求会显示在这里。你可以查看并决定是否授权。"));
                for(String id:refs){if(selected.equals("peers")){var peer=repository.registry.peer(id);post(generation,()->showPeer(peer));}else if(selectedList.equals("reviews")){var review=repository.registry.review(id);post(generation,()->showApp(review));}else{var request=repository.registry.inspectRequest(id);post(generation,()->showAccess(request));}}
                if(refs.size()==20)post(generation,()->ui.add(content,ui.button("查看更多",false,()->{cursor=refs.get(refs.size()-1);render();})));
            });
        }
        background(()->{Snapshot value=new Snapshot();try{value.id=repository.identity.deviceId();if(value.id!=null){try{repository.identity.load();value.enrolled=true;}catch(java.security.GeneralSecurityException e){if(!"Device enrollment required.".equals(e.getMessage()))throw e;}}if(value.enrolled)value.server=ServerSettings.load(this,repository.identity);value.pending=repository.actions.pending();}catch(Exception e){value.issue="设备身份或配置需要检查";}post(generation,()->{snapshot=value;if(selected.equals("home")||selected.equals("settings")){content.removeAllViews();if(selected.equals("home"))homeContent();else settingsContent();updateStatus();}});});
    }
    private void title(String heading,String subtitle){ui.add(page,ui.text(heading,28,OwnerUi.INK,true));ui.gap(page,8);ui.add(page,ui.text(subtitle,14,OwnerUi.MUTED,false));ui.gap(page,24);}
    private void home(){
        LinearLayout brand=ui.row();OwnerUi.Icon logo=new OwnerUi.Icon(this,"link",OwnerUi.TEAL);brand.addView(logo,new LinearLayout.LayoutParams(ui.dp(30),ui.dp(30)));TextView name=ui.text("LinkGate",28,OwnerUi.INK,true);LinearLayout.LayoutParams n=new LinearLayout.LayoutParams(-2,-2);n.leftMargin=ui.dp(10);brand.addView(name,n);ui.add(page,brand);ui.gap(page,8);ui.add(page,ui.text("连接设备，让应用自由互联",14,OwnerUi.MUTED,false));ui.gap(page,24);
        LinearLayout hero=ui.card(page);hero.setPadding(ui.dp(24),ui.dp(24),ui.dp(24),ui.dp(24));
        statusBadge=ui.badge("读取状态",OwnerUi.TEAL,OwnerUi.PALE);hero.addView(statusBadge);ui.gap(hero,24);
        statusTitle=ui.text("网关未启动",27,OwnerUi.INK,true);ui.add(hero,statusTitle);ui.gap(hero,12);statusDetail=ui.text("",14,OwnerUi.MUTED,false);ui.add(hero,statusDetail);ui.gap(hero,24);
        startButton=ui.button("启动网关",true,this::toggle);ui.add(hero,startButton);ui.gap(hero,12);ui.add(hero,ui.text("关闭页面不会停止已启动的网关",12,OwnerUi.MUTED,false));
        content=ui.column();ui.add(page,content);homeContent();updateStatus();
    }
    private void homeContent(){
        if(snapshot.issue!=null){LinearLayout c=ui.card(content);ui.add(c,ui.text("配置需要检查",17,OwnerUi.INK,true));ui.gap(c,8);ui.add(c,ui.text("现有身份和配置已保留，请在设置中检查。",14,OwnerUi.MUTED,false));ui.gap(c,14);ui.add(c,ui.button("查看设置",false,()->navigate("settings")));}
        else if(snapshot.enrolled&&snapshot.server==null){LinearLayout c=ui.card(content);ui.add(c,ui.text("完成设备配置",17,OwnerUi.INK,true));ui.gap(c,10);ui.add(c,ui.text(!snapshot.enrolled?"为这台手机建立设备身份，再添加服务器和电脑。":"设备身份已就绪，添加服务器以使用受认证的远程连接。",14,OwnerUi.MUTED,false));ui.gap(c,16);ui.add(c,ui.button("前往配置",false,()->navigate("settings")));}
        ui.section(content,"连接与访问");LinearLayout quick=ui.card(content);ui.action(quick,"peers","我的设备","添加和管理已配对的电脑",()->navigate("peers"));ui.line(quick);ui.action(quick,"apps","应用与授权","查看接入声明和访问请求",()->navigate("apps"));
        if(snapshot.pending){LinearLayout c=ui.card(content);ui.action(c,"identity","有一项操作等待确认","先恢复原操作，避免重复提交",this::recovery);}
        ui.gap(content,4);ui.add(content,ui.text("连接路径和传输效果需在应用实际使用时确认。",12,OwnerUi.MUTED,false));
    }
    private void updateStatus(){
        if(statusTitle==null)return;String state=GatewayService.state();boolean running=state.startsWith("running"),starting=state.equals("starting"),failed=!running&&!starting&&!state.equals("GATEWAY_STOPPED");
        statusBadge.setText(running?"●  网关运行中":starting?"●  正在启动":failed?"●  需要检查":"○  网关已停止");statusTitle.setText(running?"已准备好连接":starting?"正在准备连接":failed?"启动未完成":"按需开启连接");
        statusDetail.setText(running?(state.equals("running-server")?"受认证网关已启动，等待应用使用。":"当前使用局域网预览配置。可在设置中添加服务器。"):starting?"正在验证设备身份与服务器，请稍候。":failed?"请检查设备证书、服务器配置与网络后重试。":snapshot.enrolled?"启动后，已授权的应用可以连接你的设备。":"先完成设备身份配置，再连接你的电脑。");
        startButton.setEnabled(true);startButton.setText(running?"停止网关":starting?"取消启动":snapshot.enrolled?"启动网关":"完成设备配置");
    }
    private void toggle(){
        if(GatewayService.startingOrRunning()){GatewayService.requestStop(this);updateStatus();return;}
        if(!snapshot.enrolled){navigate("settings");return;}
        background(()->{repository.identity.load();runOnUiThread(()->{if(Build.VERSION.SDK_INT>=33&&checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS)!=PackageManager.PERMISSION_GRANTED)requestPermissions(new String[]{Manifest.permission.POST_NOTIFICATIONS},71);GatewayService.requestStart(this);updateStatus();});});
    }
    private void devices(){title("我的设备","管理你信任的电脑与连接入口");ui.add(page,ui.button("添加设备",true,()->pick(PEER,"选择设备的公开配置")));ui.gap(page,20);content=ui.column();ui.add(page,content);}
    private void applications(){title("应用与授权","每个应用，由你决定连接范围");LinearLayout choices=ui.row();for(String key:new String[]{"reviews","access"}){Button b=ui.button(key.equals("reviews")?"应用声明":"访问请求",list.equals(key),()->{list=key;cursor=null;render();});LinearLayout.LayoutParams lp=new LinearLayout.LayoutParams(0,-2,1);lp.setMargins(0,0,ui.dp(8),0);choices.addView(b,lp);}ui.add(page,choices);ui.gap(page,18);content=ui.column();ui.add(page,content);}
    private void settings(){title("设置","设备身份、服务器与恢复工具");content=ui.column();ui.add(page,content);settingsContent();}
    private void settingsContent(){
        ui.section(content,"连接配置");LinearLayout setup=ui.card(content);
        ui.action(setup,"identity","设备身份",snapshot.enrolled?"证书已就绪 · "+shortId(snapshot.id):snapshot.id==null?"尚未配置":"等待导入设备证书",this::identityMenu);ui.line(setup);
        ui.action(setup,"server","服务器",snapshot.server==null?"添加受认证服务器":snapshot.server.host+" · 已保存",this::serverMenu);
        if(snapshot.issue!=null){ui.gap(setup,8);ui.add(setup,ui.text(snapshot.issue+"，未清除现有数据。",13,OwnerUi.RED,false));}
        ui.section(content,"维护");LinearLayout tools=ui.card(content);ui.action(tools,"identity","操作恢复",snapshot.pending?"有操作等待确认":"查询提交结果与处理未知状态",this::recovery);ui.line(tools);
        ui.action(tools,"link","连接帮助","VPN 共存与应用接入说明",()->details("连接帮助","使用手机 VPN 时，可在其应用访问控制中排除 LinkGate。\n\nLinkGate 需要业务应用接入 SDK。配置设备不会自动授权应用，应用声明和精确访问请求仍需分别确认。\n\n设备就绪与应用实际直连是不同状态；路径由真实业务连接确定。"));
        ui.gap(content,20);String version="";try{version=getPackageManager().getPackageInfo(getPackageName(),0).versionName;}catch(Exception ignored){}ui.add(content,ui.text("LinkGate  "+version+"\n应用级连接网关 · 预览版",12,OwnerUi.MUTED,false));
    }
    private void identityMenu(){
        String[] items={"生成 / 导出设备 CSR","导入设备证书与所有者根","导出公开设备证书","查看设备 ID"};new AlertDialog.Builder(this).setTitle("设备身份").setItems(items,(d,i)->{
            if(i==0)background(()->{String value=repository.identity.request();runOnUiThread(()->export(value,"linkgate-device-request.pem"));});
            else if(i==1){if(stoppedForConfig())pick(LEAF,"选择所有者签发的设备证书");}
            else if(i==2)background(()->{String value=repository.identity.certificatePem();runOnUiThread(()->export(value,"linkgate-device-cert.pem"));});
            else details("设备 ID",snapshot.id==null?"尚未生成设备身份。":snapshot.id);
        }).setNegativeButton("关闭",null).show();
    }
    private void serverMenu(){String[] items={"导入服务器配置","查看当前配置"};new AlertDialog.Builder(this).setTitle("服务器").setItems(items,(d,i)->{if(i==0){if(stoppedForConfig())pick(SERVER,"选择服务器公开配置 JSON");}else if(snapshot.server==null)notice("尚未保存服务器配置。");else details("服务器配置",snapshot.server.host+"\n控制端口："+snapshot.server.controlPort+"\n数据端口："+snapshot.server.backendPort+"\nSTUN："+(snapshot.server.stunEndpoints.isEmpty()?"未配置":String.join(", ",snapshot.server.stunEndpoints)));}).setNegativeButton("关闭",null).show();}
    private boolean stoppedForConfig(){if(GatewayService.startingOrRunning()){notice("请先在概览页停止网关，再修改身份或服务器配置。");return false;}if(getSharedPreferences("owner",MODE_PRIVATE).getBoolean("enabled",false))GatewayService.requestStop(this);return true;}
    private void recovery(){new AlertDialog.Builder(this).setTitle("操作恢复").setItems(new String[]{"查询并恢复上次操作","放弃未记录的等待"},(d,i)->{if(i==0)background(()->{String value=repository.actions.recover();runOnUiThread(()->{notice(value);render();});});else confirm("放弃等待","仅在原操作仍未记录时解除本地等待，不撤销任何已提交决定。",()->background(()->{repository.actions.abandon();runOnUiThread(this::render);}));}).setNegativeButton("关闭",null).show();}
    private void showPeer(AgentRegistry.Peer peer){LinearLayout c=ui.card(content);c.addView(ui.badge(peer.active?"已信任":"已撤销",peer.active?OwnerUi.TEAL:OwnerUi.MUTED,peer.active?OwnerUi.PALE:OwnerUi.BG));ui.gap(c,14);ui.add(c,ui.text("设备 "+shortId(peer.id),17,OwnerUi.INK,true));ui.gap(c,8);ui.add(c,ui.text(peer.host+":"+peer.port,14,OwnerUi.MUTED,false));ui.gap(c,14);ui.add(c,ui.button("查看设备",false,()->{
        String info="设备公钥：\n"+peer.id+"\n\n版本："+peer.revision+"\n入口："+peer.host+":"+peer.port+"\n状态："+(peer.active?"已信任":"已撤销");dialog("设备详情",info).setPositiveButton(peer.active?"撤销信任":"关闭",(d,w)->{if(peer.active)confirm("撤销此设备",peer.id+"\n此设备的本地访问授权将同时失效。",()->background(()->{repository.actions.execute("peer.revoke","deviceId",peer.id,op->repository.registry.revokePeer(peer.id,peer.revision,op));runOnUiThread(this::render);}));}).setNegativeButton(peer.active?"返回":null,null).show();}));}
    private void showApp(AgentRegistry.Review review){
        String appId="接入应用";try{appId=String.valueOf(WireJson.read(review.pending.getBytes(StandardCharsets.UTF_8),65536).get("applicationId"));}catch(Exception ignored){}
        LinearLayout c=ui.card(content);boolean pending=review.approved==null||!review.pending.equals(review.approved);c.addView(ui.badge(review.approved==null?"未批准":pending?"有待审核变更":"已批准",OwnerUi.TEAL,OwnerUi.PALE));ui.gap(c,14);ui.add(c,ui.text(safe(appId),16,OwnerUi.INK,true));ui.gap(c,8);ui.add(c,ui.text("接入声明与访问权限分别管理",13,OwnerUi.MUTED,false));ui.gap(c,14);
        ui.add(c,ui.button("查看并审核",false,()->{String facts="应用引用："+review.reference+"\n\n实际调用方：\n"+safe(review.principal)+"\n\n待审核声明：\n"+safe(review.pending)+"\n\n已批准声明：\n"+safe(review.approved);dialog("审核应用声明",facts).setPositiveButton("选择操作",(d,w)->new AlertDialog.Builder(this).setTitle("对此声明的决定").setItems(new String[]{"批准此声明","拒绝新声明","撤销已批准声明"},(x,i)->{String decision=new String[]{"approve","deny","revoke"}[i];confirm("确认声明决定",facts+"\n\n决定："+new String[]{"批准","拒绝新声明","撤销"}[i],()->background(()->{repository.actions.execute("review."+decision,"applicationRef",review.reference,op->repository.registry.decide(review,decision,op));runOnUiThread(this::render);}));}).setNegativeButton("取消",null).show()).setNegativeButton("返回",null).show();}));
    }
    private void showAccess(AgentRegistry.RequestReview request){
        String state=request.state.equals("active")?"已授权":request.state.equals("pending")?"待确认":request.state.equals("revoked")?"已撤销":"需要重新申请";LinearLayout c=ui.card(content);c.addView(ui.badge(state,OwnerUi.TEAL,OwnerUi.PALE));ui.gap(c,14);ui.add(c,ui.text(safe(request.publisher)+" / "+safe(request.service),16,OwnerUi.INK,true));ui.gap(c,8);ui.add(c,ui.text("设备 "+shortId(request.device)+" · 导入 "+safe(request.alias),13,OwnerUi.MUTED,false));ui.gap(c,14);
        ui.add(c,ui.button("查看访问范围",false,()->{String facts="请求："+request.reference+"\n\n实际调用方：\n"+safe(request.principal)+"\n\n设备："+request.device+"\n远端应用 / 服务："+request.publisher+" / "+request.service+"\n本地导入："+request.alias+"\n状态："+state+"\n说明："+safe(request.reason);dialog("访问请求",facts).setPositiveButton(request.state.equals("active")?"撤销授权":"批准此范围",(d,w)->{boolean revoke=request.state.equals("active");confirm(revoke?"撤销访问":"批准访问",facts+(revoke?"":"\n\n仍需电脑端另行批准此应用的服务访问。"),()->background(()->{repository.actions.execute(revoke?"access.revoke":"access.approve","grantRequestId",request.reference,op->revoke?repository.registry.revokeAccess(request.reference,request.revision,op):repository.registry.approveAccess(request.reference,request.revision,op));runOnUiThread(this::render);}));}).setNegativeButton("返回",null).show();}));
    }
    private void pick(int code,String title){Intent intent=new Intent(Intent.ACTION_OPEN_DOCUMENT).setType("*/*").addCategory(Intent.CATEGORY_OPENABLE).putExtra(Intent.EXTRA_TITLE,title);startActivityForResult(intent,code);}
    private void export(String value,String title){export=value;startActivityForResult(new Intent(Intent.ACTION_CREATE_DOCUMENT).setType("application/x-pem-file").addCategory(Intent.CATEGORY_OPENABLE).putExtra(Intent.EXTRA_TITLE,title),EXPORT);}
    @Override protected void onActivityResult(int request,int result,Intent data){super.onActivityResult(request,result,data);if(result!=RESULT_OK||data==null||data.getData()==null)return;android.net.Uri uri=data.getData();background(()->{
        if(request==EXPORT){String value=export;if(value==null)throw new IllegalStateException();try(var output=getContentResolver().openOutputStream(uri,"w")){if(output==null)throw new java.io.IOException();output.write(value.getBytes(StandardCharsets.UTF_8));}export=null;return;}
        byte[] bytes;try(var input=getContentResolver().openInputStream(uri)){if(input==null)throw new java.io.IOException();ByteArrayOutputStream output=new ByteArrayOutputStream();byte[] buffer=new byte[4096];int n;while((n=input.read(buffer))!=-1){if(output.size()+n>65536)throw new java.io.IOException();output.write(buffer,0,n);}bytes=output.toByteArray();}
        String value=StandardCharsets.UTF_8.newDecoder().onMalformedInput(CodingErrorAction.REPORT).onUnmappableCharacter(CodingErrorAction.REPORT).decode(ByteBuffer.wrap(bytes)).toString();
        if(request==LEAF){leaf=value;runOnUiThread(()->pick(ROOT,"选择已通过可信途径确认的公开所有者根"));}
        else if(request==ROOT){String selectedRoot=value,selectedLeaf=leaf;String fingerprint=AndroidIdentity.fingerprint(AndroidIdentity.parse(selectedRoot));runOnUiThread(()->confirm("确认所有者根","根证书 SHA-256：\n"+fingerprint+"\n请通过可信途径核对；不接受未经确认的根。",()->background(()->{if(GatewayService.startingOrRunning())throw new IllegalStateException("OWNER_STOP_REQUIRED");repository.identity.install(selectedLeaf,selectedRoot);runOnUiThread(()->{notice("设备证书已安装，可启动网关。");render();});})));}
        else if(request==SERVER){ServerSettings selected=ServerSettings.parse(bytes,repository.identity);String fingerprint=AndroidIdentity.fingerprint(selected.certificate);runOnUiThread(()->confirm("确认受认证服务器","服务器证书 SHA-256：\n"+fingerprint+"\nIPv4："+selected.host+"\n控制端口："+selected.controlPort+" / 数据转接端口："+selected.backendPort+"\nSTUN："+(selected.stunEndpoints.isEmpty()?"未配置":String.join(", ",selected.stunEndpoints))+"\n请通过可信途径核对。配置后授权失败不会回退预览模式。",()->background(()->{selected.install(this);runOnUiThread(()->{notice("服务器配置已保存，请重新启动网关。");render();});})));}
        else if(request==PEER){Map<String,Object> config=WireJson.read(bytes,65536);WireJson.fields(config,"certificate","host","port","expectedRevision");if(!(config.get("certificate") instanceof String))throw new IllegalArgumentException();String pem=(String)config.get("certificate"),host=AgentRegistry.ipv4(WireJson.text(config.get("host"),15));int port=(int)WireJson.integer(config.get("port"),1,65535);long version=WireJson.integer(config.get("expectedRevision"),0,Long.MAX_VALUE-1);String id=repository.identity.peerId(pem);runOnUiThread(()->confirm("确认信任设备","设备公钥：\n"+id+"\nIPv4 入口："+host+":"+port+"\n预期版本："+version+"\n重新信任会使旧本地访问授权失效。",()->background(()->{repository.actions.execute("peer.trust","deviceId",id,op->repository.registry.trustPeer(id,pem,host,port,version,op));runOnUiThread(this::render);})));}
    });}
    private void post(long generation,Runnable action){runOnUiThread(()->{if(!isDestroyed()&&uiGeneration==generation)action.run();});}
    private AlertDialog.Builder dialog(String title,String value){ScrollView scroll=new ScrollView(this);TextView body=ui.text(value,14,OwnerUi.INK,false);body.setTextIsSelectable(true);body.setPadding(ui.dp(24),ui.dp(18),ui.dp(24),ui.dp(18));scroll.addView(body);return new AlertDialog.Builder(this).setTitle(title).setView(scroll);}
    private void details(String title,String message){if(!isDestroyed())dialog(title,message).setPositiveButton("知道了",null).show();}
    private void confirm(String title,String message,Runnable action){if(!isDestroyed())dialog(title,message).setNegativeButton("取消",null).setPositiveButton("确认",(d,w)->action.run()).show();}
    private void notice(String message){details("LinkGate",message);}
    private interface Task{void run()throws Exception;}
    private void background(Task task){try{work.execute(()->{try{task.run();}catch(Exception e){String code=e instanceof AgentRegistry.Failure?((AgentRegistry.Failure)e).code:"OWNER_OPERATION_FAILED";runOnUiThread(()->notice("操作未完成（"+code+"）。请检查配置；如有待确认的操作，请先到设置中恢复原操作。"));}});}catch(RuntimeException ignored){if(!isDestroyed())notice("正在处理其他操作，请稍后再试。");}}
    private static String shortId(String value){return value==null?"未配置":value.length()<17?value:value.substring(0,8)+"…"+value.substring(value.length()-6);}
    private static String safe(String value){if(value==null)return "无";StringBuilder text=new StringBuilder();for(char part:value.toCharArray()){if(part<32||part>126){String hex=Integer.toHexString(part);text.append("\\u");for(int i=hex.length();i<4;i++)text.append('0');text.append(hex);}else text.append(part);}return text.toString();}
    @Override protected void onDestroy(){main.removeCallbacks(tick);work.shutdownNow();super.onDestroy();}
}
