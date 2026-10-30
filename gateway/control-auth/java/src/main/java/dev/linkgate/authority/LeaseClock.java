package dev.linkgate.authority;

public final class LeaseClock {
    public interface Source {long utcMillis();long nanos();}
    static final class Reading {final long utcMillis,nanos;Reading(long utc,long mono){utcMillis=utc;nanos=mono;}}
    private final Source source;private long effectiveMillis,timestamp,remainder;private boolean invalid;
    public LeaseClock(){this(new Source(){private final long began=System.nanoTime();public long utcMillis(){return System.currentTimeMillis();}public long nanos(){return System.nanoTime()-began;}});}
    public LeaseClock(Source source){if(source==null)throw new IllegalArgumentException("Held clock required.");this.source=source;timestamp=source.nanos();effectiveMillis=source.utcMillis();}
    synchronized void observeAuthenticatedTime(long utcMillis)throws LeaseRejectedException {if(utcMillis<1)throw new LeaseRejectedException();read();if(utcMillis>effectiveMillis){effectiveMillis=utcMillis;remainder=0;}}
    synchronized void observeAuthenticatedResponseDate(long utcMillis,long requestBegan)throws LeaseRejectedException {
        try{Reading now=read();long elapsed=Math.subtractExact(now.nanos,requestBegan);if(elapsed<0||utcMillis<1)throw new ArithmeticException();long rounded=Math.addExact(elapsed,999999L)/1000000L;observeAuthenticatedTime(Math.addExact(utcMillis,Math.addExact(1000L,rounded)));}
        catch(ArithmeticException failure){throw new LeaseRejectedException();}
    }
    synchronized Reading read()throws LeaseRejectedException {
        if(invalid)throw new LeaseRejectedException();
        try {long now=source.nanos(),elapsed=Math.subtractExact(now,timestamp);if(elapsed<0)throw new ArithmeticException();long combined=Math.addExact(elapsed,remainder),floor=Math.addExact(effectiveMillis,combined/1000000L),wall=source.utcMillis();remainder=combined%1000000L;effectiveMillis=Math.max(floor,wall);if(wall>floor)remainder=0;timestamp=now;return new Reading(effectiveMillis,now);}
        catch(RuntimeException failure){invalid=true;throw new LeaseRejectedException();}
    }
}
