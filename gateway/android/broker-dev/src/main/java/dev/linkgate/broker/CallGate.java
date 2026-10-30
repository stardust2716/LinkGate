package dev.linkgate.broker;

import java.util.HashMap;
import java.util.Map;

final class CallGate {
    private final Map<Integer,Integer> callers = new HashMap<>();
    private int total;

    synchronized Lease acquire(int uid) {
        int active = callers.containsKey(uid) ? callers.get(uid) : 0;
        if (uid < 0 || active >= 8 || total >= 32) return null;
        callers.put(uid, active + 1); total++;
        return new Lease(uid);
    }
    synchronized int activeCallers() { return callers.size(); }
    synchronized int activeCalls() { return total; }

    final class Lease implements AutoCloseable {
        private final int uid;
        private boolean closed;
        private Lease(int uid) { this.uid = uid; }
        @Override public void close() {
            synchronized (CallGate.this) {
                if (closed) return;
                closed = true;
                int active = callers.get(uid) - 1;
                if (active == 0) callers.remove(uid); else callers.put(uid, active);
                total--;
            }
        }
    }
}
