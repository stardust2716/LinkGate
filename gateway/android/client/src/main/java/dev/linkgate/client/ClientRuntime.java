package dev.linkgate.client;

import java.util.concurrent.ScheduledFuture;
import java.util.concurrent.ScheduledThreadPoolExecutor;
import java.util.concurrent.Semaphore;
import java.util.concurrent.SynchronousQueue;
import java.util.concurrent.ThreadFactory;
import java.util.concurrent.ThreadPoolExecutor;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.RejectedExecutionException;
import java.util.concurrent.atomic.AtomicBoolean;

final class ClientRuntime implements AutoCloseable {
    private final ThreadPoolExecutor workers;
    private final ScheduledThreadPoolExecutor timer;
    private final Semaphore clients = new Semaphore(8);
    ClientRuntime() {
        ThreadFactory factory = work -> { Thread thread = new Thread(work, "LinkGate-client"); thread.setDaemon(true); return thread; };
        workers = new ThreadPoolExecutor(0, 2, 30, TimeUnit.SECONDS, new SynchronousQueue<>(), factory);
        timer = new ScheduledThreadPoolExecutor(1, factory); timer.setRemoveOnCancelPolicy(true);
    }
    boolean execute(Runnable work) {
        try { workers.execute(work); return true; } catch (RejectedExecutionException e) { return false; }
    }
    ScheduledFuture<?> after(long millis, Runnable action) { return timer.schedule(action, millis, TimeUnit.MILLISECONDS); }
    Lease acquire() { return clients.tryAcquire() ? new Lease() : null; }
    int activeWorkers() { return workers.getPoolSize(); }
    int timerTasks() { return timer.getQueue().size(); }
    final class Lease implements AutoCloseable {
        private final AtomicBoolean released = new AtomicBoolean();
        @Override public void close() { if (released.compareAndSet(false, true)) clients.release(); }
    }
    @Override public void close() { workers.shutdownNow(); timer.shutdownNow(); }
}
