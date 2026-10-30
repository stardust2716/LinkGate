package dev.linkgate.client;

import java.util.Collection;
import java.util.Collections;
import java.util.HashSet;
import java.util.Locale;
import java.util.Set;

final class PeerPin {
    final String packageName, className;
    private final Set<String> trusted;
    PeerPin(String packageName, String className, Collection<String> signers) {
        if (!canonical(packageName) || !canonical(className)) throw new IllegalArgumentException("Invalid Broker pin.");
        this.packageName = packageName; this.className = className; trusted = hashes(signers);
    }
    void verify(Snapshot value) {
        if (!packageName.equals(value.packageName) || !className.equals(value.className) || value.uid < 0 ||
            value.firstInstalled <= 0 || value.updated <= 0 || !value.enabled || !value.exported || value.shared || !trusted.equals(value.signers))
            throw new SecurityException("Broker identity mismatch.");
    }
    static boolean same(Snapshot a, Snapshot b) {
        return a.packageName.equals(b.packageName) && a.className.equals(b.className) && a.uid == b.uid &&
            a.firstInstalled == b.firstInstalled && a.updated == b.updated && a.signers.equals(b.signers) &&
            a.enabled == b.enabled && a.exported == b.exported && a.shared == b.shared;
    }
    private static boolean canonical(String value) {
        return value != null && value.length() <= 256 && value.matches("[A-Za-z_][A-Za-z0-9_]*(?:\\.[A-Za-z_][A-Za-z0-9_]*)+");
    }
    private static Set<String> hashes(Collection<String> values) {
        if (values == null || values.isEmpty() || values.size() > 32) throw new IllegalArgumentException("Invalid Broker pin.");
        Set<String> result = new HashSet<>();
        for (String value : values)
            if (value == null || !value.matches("[0-9a-fA-F]{64}") || !result.add(value.toLowerCase(Locale.ROOT))) throw new IllegalArgumentException("Invalid Broker pin.");
        return Collections.unmodifiableSet(result);
    }
    static final class Snapshot {
        final String packageName, className;
        final int uid;
        final long firstInstalled, updated;
        final Set<String> signers;
        final boolean enabled, exported, shared;
        Snapshot(String pkg, String cls, int uid, long installed, long updated, Collection<String> signers, boolean enabled, boolean exported, boolean shared) {
            packageName=pkg; className=cls; this.uid=uid; firstInstalled=installed; this.updated=updated;
            this.signers=hashes(signers); this.enabled=enabled; this.exported=exported; this.shared=shared;
        }
    }
}
