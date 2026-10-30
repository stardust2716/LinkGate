package dev.linkgate.agent.android;

import android.content.Context;
import android.content.res.ColorStateList;
import android.graphics.Canvas;
import android.graphics.Paint;
import android.graphics.Path;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.graphics.drawable.RippleDrawable;
import android.view.Gravity;
import android.view.View;
import android.widget.Button;
import android.widget.LinearLayout;
import android.widget.TextView;

/** Presentation only. Owner actions remain in the Activity and authority layer. */
final class OwnerUi {
    static final int BG=0xfff5f7fa,INK=0xff16283c,MUTED=0xff66788a,TEAL=0xff087f75,
        PALE=0xffe7f5f0,LINE=0xffe7edf2,WHITE=0xffffffff,RED=0xffae4242;
    final Context context;
    OwnerUi(Context context){this.context=context;}
    int dp(float n){return Math.round(n*context.getResources().getDisplayMetrics().density);}
    LinearLayout column(){LinearLayout v=new LinearLayout(context);v.setOrientation(LinearLayout.VERTICAL);return v;}
    LinearLayout row(){LinearLayout v=new LinearLayout(context);v.setGravity(Gravity.CENTER_VERTICAL);return v;}
    TextView text(String value,float size,int color,boolean bold){
        TextView t=new TextView(context);t.setText(value);t.setTextSize(size);t.setTextColor(color);
        t.setTypeface(Typeface.create(bold?"sans-serif-medium":"sans-serif",Typeface.NORMAL));
        t.setLineSpacing(dp(3),1);t.setIncludeFontPadding(false);return t;
    }
    GradientDrawable shape(int color,float radius){GradientDrawable d=new GradientDrawable();d.setColor(color);d.setCornerRadius(dp(radius));return d;}
    void gap(LinearLayout parent,int height){parent.addView(new View(context),new LinearLayout.LayoutParams(1,dp(height)));}
    void add(LinearLayout parent,View view){parent.addView(view,new LinearLayout.LayoutParams(-1,-2));}
    LinearLayout card(LinearLayout parent){
        LinearLayout card=column();card.setPadding(dp(20),dp(20),dp(20),dp(20));card.setBackground(shape(WHITE,24));
        LinearLayout.LayoutParams p=new LinearLayout.LayoutParams(-1,-2);p.bottomMargin=dp(14);parent.addView(card,p);return card;
    }
    void section(LinearLayout parent,String title){gap(parent,8);TextView t=text(title,16,INK,true);add(parent,t);gap(parent,12);}
    Button button(String label,boolean primary,Runnable action){
        Button b=new Button(context);b.setText(label);b.setAllCaps(false);b.setTextSize(15);b.setTypeface(Typeface.create("sans-serif-medium",0));
        b.setMinHeight(dp(50));b.setMinimumHeight(dp(50));b.setPadding(dp(16),dp(12),dp(16),dp(12));
        b.setTextColor(new ColorStateList(new int[][]{new int[]{-android.R.attr.state_enabled},new int[]{}},new int[]{MUTED,primary?WHITE:TEAL}));
        b.setBackground(new RippleDrawable(ColorStateList.valueOf(0x2266788a),shape(primary?TEAL:PALE,15),null));
        b.setStateListAnimator(null);b.setOnClickListener(v->action.run());return b;
    }
    TextView badge(String value,int color,int fill){TextView t=text(value,12,color,true);t.setPadding(dp(10),dp(5),dp(10),dp(5));t.setBackground(shape(fill,10));t.setLayoutParams(new LinearLayout.LayoutParams(-2,-2));return t;}
    void action(LinearLayout parent,String symbol,String title,String subtitle,Runnable action){
        LinearLayout row=row();row.setPadding(0,dp(12),0,dp(12));row.setMinimumHeight(dp(64));
        Icon icon=new Icon(context,symbol,TEAL);LinearLayout.LayoutParams ip=new LinearLayout.LayoutParams(dp(25),dp(25));ip.rightMargin=dp(14);row.addView(icon,ip);
        LinearLayout labels=column();add(labels,text(title,15,INK,true));if(subtitle!=null){gap(labels,5);add(labels,text(subtitle,12,MUTED,false));}
        row.addView(labels,new LinearLayout.LayoutParams(0,-2,1));
        Icon chevron=new Icon(context,"chevron",MUTED);LinearLayout.LayoutParams cp=new LinearLayout.LayoutParams(dp(18),dp(18));cp.leftMargin=dp(8);row.addView(chevron,cp);
        row.setBackground(new RippleDrawable(ColorStateList.valueOf(LINE),shape(WHITE,12),null));row.setFocusable(true);row.setContentDescription(title+(subtitle==null?"":"，"+subtitle));row.setOnClickListener(v->action.run());add(parent,row);
    }
    void line(LinearLayout parent){View v=new View(context);v.setBackgroundColor(LINE);parent.addView(v,new LinearLayout.LayoutParams(-1,dp(1)));}
    void empty(LinearLayout parent,String symbol,String title,String detail){
        LinearLayout c=card(parent);c.setGravity(Gravity.CENTER_HORIZONTAL);gap(c,12);
        Icon icon=new Icon(context,symbol,TEAL);c.addView(icon,new LinearLayout.LayoutParams(dp(42),dp(42)));gap(c,18);
        TextView heading=text(title,17,INK,true);heading.setGravity(Gravity.CENTER);add(c,heading);gap(c,10);
        TextView body=text(detail,14,MUTED,false);body.setGravity(Gravity.CENTER);add(c,body);gap(c,14);
    }
    LinearLayout navigation(String selected,java.util.function.Consumer<String> choose){
        LinearLayout bar=row();bar.setPadding(dp(12),dp(8),dp(12),dp(8));bar.setBackgroundColor(WHITE);
        String[] keys={"home","peers","apps","settings"},labels={"概览","设备","应用","设置"};
        for(int i=0;i<keys.length;i++){
            String key=keys[i];boolean active=selected.equals(key);LinearLayout item=column();item.setGravity(Gravity.CENTER);item.setMinimumHeight(dp(56));item.setPadding(dp(4),dp(6),dp(4),dp(6));
            Icon icon=new Icon(context,key,active?TEAL:MUTED);item.addView(icon,new LinearLayout.LayoutParams(dp(24),dp(24)));gap(item,5);TextView label=text(labels[i],11,active?TEAL:MUTED,active);label.setGravity(Gravity.CENTER);item.addView(label,new LinearLayout.LayoutParams(-2,-2));
            item.setBackground(new RippleDrawable(ColorStateList.valueOf(LINE),shape(active?PALE:WHITE,16),null));item.setFocusable(true);item.setSelected(active);item.setContentDescription(labels[i]+(active?"，已选中":""));item.setOnClickListener(v->choose.accept(key));
            LinearLayout.LayoutParams p=new LinearLayout.LayoutParams(0,-2,1);p.setMargins(dp(3),0,dp(3),0);bar.addView(item,p);
        }return bar;
    }
    static final class Icon extends View {
        private final Paint paint=new Paint(Paint.ANTI_ALIAS_FLAG);private final String kind;private final int color;
        Icon(Context c,String kind,int color){super(c);this.kind=kind;this.color=color;setImportantForAccessibility(IMPORTANT_FOR_ACCESSIBILITY_NO);}
        @Override protected void onDraw(Canvas c){super.onDraw(c);c.save();c.scale(getWidth()/24f,getHeight()/24f);paint.setColor(color);paint.setStyle(Paint.Style.STROKE);paint.setStrokeWidth(1.8f);paint.setStrokeCap(Paint.Cap.ROUND);paint.setStrokeJoin(Paint.Join.ROUND);
            Path p=new Path();switch(kind){
                case "home":p.moveTo(3,10);p.lineTo(12,3);p.lineTo(21,10);p.moveTo(5,9);p.lineTo(5,21);p.lineTo(10,21);p.lineTo(10,14);p.lineTo(14,14);p.lineTo(14,21);p.lineTo(19,21);p.lineTo(19,9);c.drawPath(p,paint);break;
                case "peers":c.drawRoundRect(3,4,21,16,2,2,paint);c.drawLine(8,21,16,21,paint);c.drawLine(12,16,12,21,paint);break;
                case "apps":for(int x:new int[]{3,14})for(int y:new int[]{3,14})c.drawRoundRect(x,y,x+7,y+7,1.5f,1.5f,paint);break;
                case "settings":case "identity":c.drawCircle(12,12,4,paint);c.drawCircle(12,12,9,paint);for(int i=0;i<8;i++){c.save();c.rotate(i*45,12,12);c.drawLine(12,1,12,4,paint);c.restore();}break;
                case "link":c.drawCircle(6,6,3,paint);c.drawCircle(18,18,3,paint);p.moveTo(8,8);p.lineTo(16,16);p.moveTo(9,6);p.lineTo(17,6);p.lineTo(17,10);p.moveTo(7,14);p.lineTo(7,18);p.lineTo(15,18);c.drawPath(p,paint);break;
                case "server":c.drawRoundRect(4,3,20,10,2,2,paint);c.drawRoundRect(4,14,20,21,2,2,paint);c.drawPoint(8,6.5f,paint);c.drawPoint(8,17.5f,paint);c.drawLine(12,6.5f,16,6.5f,paint);c.drawLine(12,17.5f,16,17.5f,paint);break;
                case "check":p.moveTo(5,12);p.lineTo(10,17);p.lineTo(20,7);c.drawPath(p,paint);break;
                case "chevron":p.moveTo(9,5);p.lineTo(16,12);p.lineTo(9,19);c.drawPath(p,paint);break;
                default:c.drawCircle(12,12,9,paint);c.drawLine(12,7,12,13,paint);c.drawPoint(12,17,paint);
            }c.restore();
        }
    }
}
